using System.Net.Http.Json;
using System.Text.Json;

namespace Teos.OpportunityNetwork.Matching;

/// <summary>
/// OpenAI-compatible chat-completions adapter. Works with any provider that exposes a
/// <c>/chat/completions</c> endpoint — OpenAI, Groq, OpenRouter, Mistral, Gemini's
/// OpenAI-compatible endpoint, GitHub Models, Ollama, LM Studio. Returns the assistant
/// content raw; the caller validates it (<see cref="SemanticValidator"/>).
///
/// An empty apiKey is allowed for keyless local servers (Ollama / LM Studio): the
/// Authorization header is simply omitted. The key is never logged and is redacted from
/// error text. Timeouts and failures throw <see cref="SemanticProviderException"/> so a
/// caller (e.g. the ensemble) can degrade per provider instead of failing the pipeline.
/// </summary>
public sealed class ChatCompletionsSemanticEvaluator : ISemanticEvaluator
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";
    public const string DefaultModel = "gpt-4o-mini";

    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly string _endpoint;
    private readonly TimeSpan _timeout;

    public ChatCompletionsSemanticEvaluator(
        HttpClient http,
        string? apiKey,
        string model = DefaultModel,
        string? baseUrl = null,
        TimeSpan? timeout = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _model = model;
        _endpoint = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl).TrimEnd('/') + "/chat/completions";
        _timeout = timeout ?? TimeSpan.FromSeconds(90);
    }

    public async Task<string> EvaluateAsync(string opportunityText, string profileText, CancellationToken ct = default)
    {
        var payload = new
        {
            model = _model,
            temperature = 0,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "You judge how relevant an opportunity is to a candidate profile. " +
                              "Respond with ONLY a JSON object: {\"relevance\": <number 0-100>, \"rationale\": <string>}."
                },
                new
                {
                    role = "user",
                    content = $"OPPORTUNITY:\n{opportunityText}\n\nPROFILE:\n{profileText}"
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        if (_apiKey is not null)
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);

        HttpResponseMessage response;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);
            response = await _http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SemanticProviderException($"provider request timed out after {_timeout.TotalSeconds:0}s");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new SemanticProviderException($"provider HTTP error {(int)response.StatusCode} {response.ReasonPhrase}");

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
                throw new SemanticProviderException("provider response contained no choices");

            var message = choices[0].GetProperty("message");
            return message.GetProperty("content").GetString() ?? string.Empty;
        }
    }
}

/// <summary>
/// Thrown when a semantic provider itself fails (network, HTTP status, timeout, malformed
/// response). Distinct from <see cref="SemanticValidationException"/>, which is thrown for
/// schema-invalid MODEL OUTPUT. The ensemble catches this to skip the failed provider.
/// </summary>
public sealed class SemanticProviderException : Exception
{
    public SemanticProviderException(string message, Exception? inner = null) : base(message, inner) { }
}