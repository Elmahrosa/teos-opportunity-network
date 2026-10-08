using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Teos.OpportunityNetwork.Telegram;

/// <summary>Typed adapter error: caller decides retryability from the type, not string matching.</summary>
public sealed class TelegramApiException : Exception
{
    public bool Retryable { get; }
    public int? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }

    public TelegramApiException(string message, bool retryable, int? statusCode = null, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        Retryable = retryable;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }
}

public sealed record TelegramSendResult(bool Ok, string? ProviderMessageId, string? Description);

/// <summary>
/// Transport-only adapter for the Telegram Bot API.
/// - No scoring/matching/LLM/dedup logic.
/// - Never logs or returns the token; all error text passes through a redaction filter.
/// - 429 honors retry_after; timeouts and 5xx are retryable; 4xx are permanent.
/// </summary>
public interface ITelegramClient
{
    Task<TelegramSendResult> SendAsync(string chatId, string htmlText, CancellationToken ct = default);
}

public sealed class TelegramBotClient : ITelegramClient
{
    private const string ApiBase = "https://api.telegram.org";
    private static readonly Regex TokenPattern = new(@"\b\d{6,}:[A-Za-z0-9_-]{20,}\b", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly string _botToken;

    public TelegramBotClient(HttpClient http, string botToken)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(botToken))
            throw new TelegramConfigException(
                "TELEGRAM_BOT_TOKEN is missing or empty. Set it in the environment/configuration. No secret value is included in this message.");
        _botToken = botToken;
        if (_http.Timeout == TimeSpan.FromSeconds(100)) _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<TelegramSendResult> SendAsync(string chatId, string htmlText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chatId))
            throw new TelegramApiException("chat id is empty", retryable: false);

        var payload = new Dictionary<string, object>
        {
            ["chat_id"] = chatId,
            ["text"] = htmlText,
            ["parse_mode"] = "HTML",
            ["disable_web_page_preview"] = false
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/bot{_botToken}/sendMessage")
        {
            Content = JsonContent.Create(payload)
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TelegramApiException(Redact("Telegram request timed out"), retryable: true);
        }
        catch (HttpRequestException ex)
        {
            throw new TelegramApiException(Redact($"network error: {ex.Message}"), retryable: true, inner: ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (status == 429)
            {
                TimeSpan? retryAfter = TryGetRetryAfter(body);
                throw new TelegramApiException(
                    Redact("rate limited by Telegram (429)"), retryable: true, statusCode: 429, retryAfter: retryAfter);
            }

            if (status >= 500)
                throw new TelegramApiException(Redact($"Telegram server error ({status})"), retryable: true, statusCode: status);

            var parsed = TryParse(body);
            if (parsed is { Ok: true })
            {
                var value = parsed.Value;
                if (value.ResultId is not null)
                    return new TelegramSendResult(true, value.ResultId, value.Description);
                return new TelegramSendResult(true, null, value.Description);
            }

            // Permanent 4xx (bad token, chat not found, blocked by user…)
            var description = Redact(parsed?.Description ?? $"HTTP {status}");
            var retryable = status >= 500;
            if (parsed?.ErrorCode == 429) retryable = true;
            throw new TelegramApiException(description, retryable: retryable, statusCode: status);
        }
    }

    /// <summary>Redacts bot tokens (and token-bearing URLs) from any outbound text.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var redacted = TokenPattern.Replace(text, "[REDACTED_TOKEN]");
        return Regex.Replace(redacted, @"/bot[^/\s]+", "/bot[REDACTED_TOKEN]");
    }

    private static (bool Ok, string? ResultId, string? Description, int? ErrorCode)? TryParse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            var description = root.TryGetProperty("description", out var d) ? d.GetString() : null;
            var errorCode = root.TryGetProperty("error_code", out var e) ? e.GetInt32() : (int?)null;
            string? resultId = null;
            if (ok && root.TryGetProperty("result", out var result) &&
                result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("message_id", out var mid))
                resultId = mid.ToString();
            return (ok, resultId, description, errorCode);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TimeSpan? TryGetRetryAfter(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("parameters", out var p) &&
                p.TryGetProperty("retry_after", out var seconds))
                return TimeSpan.FromSeconds(seconds.GetInt32());
        }
        catch (JsonException) { }
        return null;
    }
}

/// <summary>Fail-fast configuration. Missing token → clear, secret-free error.</summary>
public static class TelegramConfig
{
    public static string RequireBotToken(IReadOnlyDictionary<string, string?>? env = null)
    {
        string? value;
        if (env is not null)
            env.TryGetValue("TELEGRAM_BOT_TOKEN", out value);
        else
            value = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");

        if (string.IsNullOrWhiteSpace(value))
            throw new TelegramConfigException(
                "TELEGRAM_BOT_TOKEN is not set. Export TELEGRAM_BOT_TOKEN (see .env.example). The error contains no secret material.");
        if (value.Length < 10 || !value.Contains(':'))
            throw new TelegramConfigException(
                "TELEGRAM_BOT_TOKEN is malformed (expected '<digits>:<secret>' format from @BotFather). The error contains no secret material.");
        return value;
    }

    public static string? GetChatId(IReadOnlyDictionary<string, string?>? env = null)
    {
        if (env is not null)
        {
            env.TryGetValue("TELEGRAM_CHAT_ID", out var v);
            return v;
        }
        return Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID");
    }
}

public sealed class TelegramConfigException : Exception
{
    public TelegramConfigException(string message) : base(message) { }
}
