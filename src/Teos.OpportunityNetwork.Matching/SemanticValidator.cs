using System.Text.Json;

namespace Teos.OpportunityNetwork.Matching;

/// <summary>Thrown when raw LLM output fails schema validation; scoring must never run on it.</summary>
public sealed class SemanticValidationException : Exception
{
    public SemanticValidationException(string message) : base(message) { }
}

/// <summary>Validated semantic judgment extracted from raw LLM output.</summary>
public sealed class SemanticJudgment
{
    public double Relevance { get; init; }
    public string? Rationale { get; init; }
    public string[] MatchedKeywords { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Schema gate between the LLM and the matcher. Raw output is validated to a JSON object
/// with a numeric <c>relevance</c> in [0,100] BEFORE it can influence any score.
/// Accepts a single fenced code block (```json ... ```) defensively.
/// </summary>
public static class SemanticValidator
{
    public static SemanticJudgment Validate(string? rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            throw new SemanticValidationException("LLM output is empty");

        var json = StripCodeFence(rawOutput);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new SemanticValidationException($"LLM output is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new SemanticValidationException("LLM output must be a JSON object");

            if (!root.TryGetProperty("relevance", out var relevance))
                throw new SemanticValidationException("LLM output is missing required field 'relevance'");

            if (relevance.ValueKind != JsonValueKind.Number)
                throw new SemanticValidationException("'relevance' must be a number between 0 and 100");

            var value = relevance.GetDouble();
            if (value < 0 || value > 100)
                throw new SemanticValidationException($"'relevance' out of range [0,100]: {value}");

            var rationale = root.TryGetProperty("rationale", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;

            string[] keywords = Array.Empty<string>();
            if (root.TryGetProperty("matched_keywords", out var kw) && kw.ValueKind == JsonValueKind.Array)
            {
                keywords = kw.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToArray();
            }

            return new SemanticJudgment
            {
                Relevance = value,
                Rationale = rationale,
                MatchedKeywords = keywords
            };
        }
    }

    private static string StripCodeFence(string raw)
    {
        var text = raw.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;

        text = text[3..];
        var newline = text.IndexOf('\n');
        if (newline >= 0)
            text = text[(newline + 1)..];

        var end = text.LastIndexOf("```", StringComparison.Ordinal);
        if (end >= 0)
            text = text[..end];

        return text.Trim();
    }
}
