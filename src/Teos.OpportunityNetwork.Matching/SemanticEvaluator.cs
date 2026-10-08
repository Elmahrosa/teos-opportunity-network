using System.Net.Http.Json;
using System.Text.Json;

namespace Teos.OpportunityNetwork.Matching;

/// <summary>
/// Produces RAW semantic output for an (opportunity, profile) pair. Implementations must
/// return the model's content verbatim; validation is done separately by
/// <see cref="SemanticValidator"/> before any value reaches the scorer.
/// </summary>
public interface ISemanticEvaluator
{
    Task<string> EvaluateAsync(string opportunityText, string profileText, CancellationToken ct = default);
}

/// <summary>Deterministic evaluator for tests/replay harnesses; returns a fixed raw output.</summary>
public sealed class StaticSemanticEvaluator : ISemanticEvaluator
{
    private readonly string _output;

    public StaticSemanticEvaluator(string output) => _output = output;

    public Task<string> EvaluateAsync(string opportunityText, string profileText, CancellationToken ct = default) =>
        Task.FromResult(_output);
}

/// <summary>
/// Guard used to prove the replay/benchmark path never calls an LLM: any invocation throws.
/// </summary>
public sealed class ForbiddenSemanticEvaluator : ISemanticEvaluator
{
    public Task<string> EvaluateAsync(string opportunityText, string profileText, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            "LLM evaluation is forbidden on this path (replay/benchmark must use the stored snapshot).");
}

/// <summary>
/// Chat-completions adapter. Reads the assistant content from
/// <c>choices[0].message.content</c> and returns it raw; the caller validates it.
/// The API key is supplied by the caller and never logged.
/// </summary>
public sealed class ChatCompletionsSemanticEvaluator : ISemanticEvaluator
{
    private const string Endpoint = "https://api.openai.com/v1/chat/completions";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    public ChatCompletionsSemanticEvaluator(HttpClient http, string apiKey, string model = "gpt-4o-mini")
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("semantic evaluator requires an API key", nameof(apiKey));
        _apiKey = apiKey;
        _model = model;
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

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);

        if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
            throw new InvalidOperationException("chat-completions response contained no choices");

        var message = choices[0].GetProperty("message");
        return message.GetProperty("content").GetString() ?? string.Empty;
    }
}
