namespace Teos.OpportunityNetwork.Matching;

/// <summary>Result of building the semantic evaluator from configuration.</summary>
public sealed record SemanticEvaluatorBuild(
    ISemanticEvaluator? Evaluator,
    string Mode,
    string[] Warnings);

/// <summary>
/// Builds the semantic evaluator from environment-style configuration — "all free LLMs",
/// one OpenAI-compatible adapter each.
///
/// <code>
/// TEOS_LLM_PROVIDERS=groq,openrouter,ollama        # which agents to run (comma-separated)
/// GROQ_API_KEY=...              # free key from console.groq.com
/// OPENROUTER_API_KEY=...        # free key from openrouter.ai (free models available)
/// OLLAMA_BASE_URL=http://localhost:11434/v1        # keyless local; model default qwen2.5:7b
/// </code>
///
/// Per-provider keys: <c>{NAME}_BASE_URL</c>, <c>{NAME}_API_KEY</c>, <c>{NAME}_MODEL</c>.
/// Known providers get sensible defaults for URL and model so only the key is required;
/// an empty API key is allowed for keyless local servers. If <c>TEOS_LLM_PROVIDERS</c> is
/// unset, a legacy <c>OPENAI_API_KEY</c> still configures a single OpenAI provider.
///
/// The evaluator returned is ALWAYS the resilient ensemble (even for one provider): provider
/// failures degrade to the neutral-50 signal instead of failing the pipeline. No providers
/// configured → <c>null</c> (the matcher's documented neutral-50 fallback).
/// </summary>
public static class SemanticProviderFactory
{
    /// <summary>Free-tier defaults per provider name (base URL / default model).</summary>
    public static readonly IReadOnlyDictionary<string, (string BaseUrl, string Model)> Defaults =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = ("https://api.openai.com/v1", "gpt-4o-mini"),
            ["groq"] = ("https://api.groq.com/openai/v1", "llama-3.3-70b-versatile"),
            ["openrouter"] = ("https://openrouter.ai/api/v1", "meta-llama/llama-3.3-70b-instruct:free"),
            ["mistral"] = ("https://api.mistral.ai/v1", "open-mistral-7b"),
            ["gemini"] = ("https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.0-flash"),
            ["github"] = ("https://models.github.ai/inference", "gpt-4o-mini"),
            ["ollama"] = ("http://localhost:11434/v1", "qwen2.5:7b")
        };

    public static SemanticEvaluatorBuild Build(
        IReadOnlyDictionary<string, string> env,
        Func<HttpClient>? httpFactory = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(env);
        httpFactory ??= () => new HttpClient();

        var warnings = new List<string>();
        string Get(string key) => env.TryGetValue(key, out var v) && v is not null ? v.Trim() : "";

        var names = Get("TEOS_LLM_PROVIDERS")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (names.Length == 0)
        {
            var legacyKey = Get("OPENAI_API_KEY");
            if (string.IsNullOrWhiteSpace(legacyKey))
                return new SemanticEvaluatorBuild(null, "neutral-50", Array.Empty<string>());

            var model = string.IsNullOrWhiteSpace(Get("OPENAI_MODEL")) ? Defaults["openai"].Model : Get("OPENAI_MODEL");
            var openai = new SemanticProvider("openai",
                new ChatCompletionsSemanticEvaluator(httpFactory(), legacyKey, model, Defaults["openai"].BaseUrl, timeout));
            return new SemanticEvaluatorBuild(new EnsembleSemanticEvaluator(new[] { openai }), "single:openai", Array.Empty<string>());
        }

        var providers = new List<SemanticProvider>();
        foreach (var name in names)
        {
            var key = name.ToUpperInvariant();
            var hasEnv = !string.IsNullOrWhiteSpace(Get($"{key}_BASE_URL")) || !string.IsNullOrWhiteSpace(Get($"{key}_MODEL"));
            var known = Defaults.TryGetValue(name, out var dflt);

            if (!known && !hasEnv)
            {
                warnings.Add($"provider '{name}' skipped: unknown, and {key}_BASE_URL / {key}_MODEL are not set");
                continue;
            }

            var baseUrl = string.IsNullOrWhiteSpace(Get($"{key}_BASE_URL")) ? (known ? dflt.BaseUrl : "") : Get($"{key}_BASE_URL");
            var model = string.IsNullOrWhiteSpace(Get($"{key}_MODEL")) ? (known ? dflt.Model : "") : Get($"{key}_MODEL");
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
            {
                warnings.Add($"provider '{name}' skipped: missing {key}_BASE_URL or {key}_MODEL");
                continue;
            }

            providers.Add(new SemanticProvider(name,
                new ChatCompletionsSemanticEvaluator(httpFactory(), Get($"{key}_API_KEY"), model, baseUrl, timeout)));
        }

        if (providers.Count == 0)
            return new SemanticEvaluatorBuild(null, "neutral-50", warnings.ToArray());

        var mode = providers.Count == 1
            ? $"single:{providers[0].Name}"
            : $"ensemble:{string.Join(",", providers.Select(p => p.Name))}";

        return new SemanticEvaluatorBuild(new EnsembleSemanticEvaluator(providers), mode, warnings.ToArray());
    }
}