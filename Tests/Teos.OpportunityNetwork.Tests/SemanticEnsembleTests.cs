using System.Net;
using System.Text.Json;
using Teos.OpportunityNetwork.Matching;
using Xunit;

namespace Teos.OpportunityNetwork.Tests;

/// <summary>Pure unit tests — no PostgreSQL, no real network (the one HTTP test uses an in-memory handler).</summary>
public class SemanticEnsembleTests
{
    private static SemanticProvider P(string name, string rawJson) =>
        new(name, new StaticSemanticEvaluator(rawJson));

    private static SemanticProvider Throwing(string name, Exception ex) =>
        new(name, new ThrowingEvaluator(ex));

    private static string J(double relevance, params string[] keywords) =>
        "{\"relevance\": " + relevance.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        ", \"rationale\": \"r\", \"matched_keywords\": [\"" + string.Join("\", \"", keywords) + "\"]}";

    private static string JRange(double relevance, decimal min, decimal max, string currency = "USD") =>
        "{\"relevance\": " + relevance.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        ", \"rationale\": \"r\", \"pay_range\": {\"min\": " +
        min.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", \"max\": " +
        max.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", \"currency\": \"" + currency + "\"}}";

    private static (double Relevance, string Rationale, string[] Keywords) Parse(string output)
    {
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        return (
            root.GetProperty("relevance").GetDouble(),
            root.GetProperty("rationale").GetString()!,
            root.GetProperty("matched_keywords").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    private sealed class ThrowingEvaluator : ISemanticEvaluator
    {
        private readonly Exception _ex;
        public ThrowingEvaluator(Exception ex) => _ex = ex;
        public Task<string> EvaluateAsync(string opportunityText, string profileText, CancellationToken ct = default) =>
            throw _ex;
    }

    // ---- deterministic fusion -------------------------------------------------

    [Fact]
    public async Task Fuses_median_of_valid_relevance_odd_count()
    {
        var ensemble = new EnsembleSemanticEvaluator(new[] { P("alpha", J(40)), P("beta", J(70)), P("gamma", J(55)) });
        var output = await ensemble.EvaluateAsync("o", "p");
        var parsed = Parse(output);
        Assert.Equal(55, parsed.Relevance, 4);
        Assert.All(ensemble.Outcomes, o => Assert.Equal(ProviderOutcomeStatus.Success, o.Status));
    }

    [Fact]
    public async Task Fuses_median_of_valid_relevance_even_count()
    {
        var ensemble = new EnsembleSemanticEvaluator(new[] { P("alpha", J(40)), P("beta", J(70)) });
        var output = await ensemble.EvaluateAsync("o", "p");
        Assert.Equal(55, Parse(output).Relevance, 4); // mean of the two middle values
    }

    [Fact]
    public async Task Fuses_median_on_single_provider()
    {
        var ensemble = new EnsembleSemanticEvaluator(new[] { P("alpha", J(42)) });
        var output = await ensemble.EvaluateAsync("o", "p");
        Assert.Equal(42, Parse(output).Relevance, 4);
    }

    // ---- resilience -----------------------------------------------------------

    [Fact]
    public async Task Skips_invalid_output_and_fuses_the_rest()
    {
        var ensemble = new EnsembleSemanticEvaluator(new[]
        {
            P("alpha", "this is not json"),
            P("beta", J(70)),
            P("gamma", J(55))
        });

        var output = await ensemble.EvaluateAsync("o", "p");

        Assert.Equal(62.5, Parse(output).Relevance, 4); // median of [55, 70] → mean of the two middle values
        var alpha = Assert.Single(ensemble.Outcomes, o => o.Provider == "alpha");
        Assert.Equal(ProviderOutcomeStatus.InvalidOutput, alpha.Status);
        Assert.Equal(ProviderOutcomeStatus.Success, Assert.Single(ensemble.Outcomes, o => o.Provider == "beta").Status);
    }

    [Fact]
    public async Task Skips_failing_provider_and_fuses_the_rest()
    {
        var ensemble = new EnsembleSemanticEvaluator(new[]
        {
            Throwing("alpha", new SemanticProviderException("HTTP error 401")),
            P("beta", J(100)),
            P("gamma", J(70))
        });

        var output = await ensemble.EvaluateAsync("o", "p");

        Assert.Equal(85, Parse(output).Relevance, 4); // median of 70,100
        var alpha = Assert.Single(ensemble.Outcomes, o => o.Provider == "alpha");
        Assert.Equal(ProviderOutcomeStatus.Failed, alpha.Status);
    }

    [Fact]
    public async Task All_providers_fail_returns_neutral_with_explicit_rationale()
    {
        var ensemble = new EnsembleSemanticEvaluator(new[]
        {
            Throwing("alpha", new SemanticProviderException("HTTP error 429")),
            Throwing("beta", new InvalidOperationException("boom"))
        });

        var output = await ensemble.EvaluateAsync("o", "p");
        var parsed = Parse(output);

        Assert.Equal(EnsembleSemanticEvaluator.NeutralRelevance, parsed.Relevance, 4);
        Assert.Contains("neutral-50", parsed.Rationale);
        Assert.All(ensemble.Outcomes, o => Assert.Equal(ProviderOutcomeStatus.Failed, o.Status));
    }

    // ---- determinism + schema gate ---------------------------------------------

    [Fact]
    public async Task Output_is_deterministic_across_runs()
    {
        var ensemble = new EnsembleSemanticEvaluator(new[] { P("beta", J(70)), P("alpha", J(40)) });
        var first = await ensemble.EvaluateAsync("o", "p");
        var second = await ensemble.EvaluateAsync("o", "p");
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Fused_output_passes_the_matcher_schema_gate()
    {
        var ensemble = new EnsembleSemanticEvaluator(new[] { P("alpha", J(88)), P("beta", J(92)) });
        var output = await ensemble.EvaluateAsync("o", "p");
        var judgment = SemanticValidator.Validate(output); // must not throw
        Assert.Equal(90, judgment.Relevance, 4);
    }

    [Fact]
    public async Task Fuses_pay_range_median_across_providers()
    {
        var ensemble = new EnsembleSemanticEvaluator(new[]
        {
            P("alpha", JRange(40, 100, 200)),
            P("beta", JRange(70, 300, 400)),
            P("gamma", JRange(55, 500, 600))
        });

        var judgment = SemanticValidator.Validate(await ensemble.EvaluateAsync("o", "p"));

        Assert.Equal(300m, judgment.PayRangeMin); // median of 100,300,500
        Assert.Equal(400m, judgment.PayRangeMax); // median of 200,400,600
        Assert.Equal("USD", judgment.PayCurrency);
    }

    [Fact]
    public async Task Absent_pay_range_is_not_invented_by_the_ensemble()
    {
        var ensemble = new EnsembleSemanticEvaluator(new[] { P("alpha", J(70)), P("beta", J(80)) });
        var judgment = SemanticValidator.Validate(await ensemble.EvaluateAsync("o", "p"));

        Assert.Null(judgment.PayRangeMin);
        Assert.Null(judgment.PayRangeMax);
        Assert.Null(judgment.PayCurrency);
    }

    [Fact]
    public async Task Pay_range_fusion_is_deterministic_across_runs()
    {
        var ensemble = new EnsembleSemanticEvaluator(new[]
        {
            P("beta", JRange(70, 300, 400)),
            P("alpha", JRange(40, 100, 200))
        });
        var first = await ensemble.EvaluateAsync("o", "p");
        var second = await ensemble.EvaluateAsync("o", "p");
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Keywords_are_union_sorted_and_deduplicated()
    {
        var ensemble = new EnsembleSemanticEvaluator(new[]
        {
            P("alpha", J(50, "c#", "python")),
            P("beta", J(60, "python", "go"))
        });

        var (_, _, keywords) = Parse(await ensemble.EvaluateAsync("o", "p"));
        Assert.Equal(new[] { "c#", "go", "python" }, keywords);
    }

    [Fact]
    public async Task Rationale_lists_providers_in_name_order()
    {
        var ensemble = new EnsembleSemanticEvaluator(new[]
        {
            Throwing("zeta", new SemanticProviderException("HTTP error 401")),
            P("alpha", J(50))
        });

        var (_, rationale, _) = Parse(await ensemble.EvaluateAsync("o", "p"));
        var alphaIndex = rationale.IndexOf("alpha", StringComparison.Ordinal);
        var zetaIndex = rationale.IndexOf("zeta", StringComparison.Ordinal);
        Assert.True(alphaIndex >= 0 && zetaIndex > alphaIndex, $"alpha must precede zeta in: '{rationale}'");
    }

    // ---- factory / configuration -----------------------------------------------

    [Fact]
    public void Factory_legacy_openai_key_builds_single_openai()
    {
        var build = SemanticProviderFactory.Build(new Dictionary<string, string> { ["OPENAI_API_KEY"] = "sk-test" });
        Assert.NotNull(build.Evaluator);
        Assert.IsType<EnsembleSemanticEvaluator>(build.Evaluator);
        Assert.Equal("single:openai", build.Mode);
    }

    [Fact]
    public void Factory_builds_ensemble_from_provider_list()
    {
        var build = SemanticProviderFactory.Build(new Dictionary<string, string>
        {
            ["TEOS_LLM_PROVIDERS"] = "groq, openrouter",
            ["GROQ_API_KEY"] = "g",
            ["OPENROUTER_API_KEY"] = "o"
        });

        var ensemble = Assert.IsType<EnsembleSemanticEvaluator>(build.Evaluator);
        Assert.Equal("ensemble:groq,openrouter", build.Mode);
        Assert.Empty(build.Warnings);
    }

    [Fact]
    public void Factory_ollama_is_keyless_and_built_from_defaults()
    {
        var build = SemanticProviderFactory.Build(new Dictionary<string, string> { ["TEOS_LLM_PROVIDERS"] = "ollama" });
        Assert.IsType<EnsembleSemanticEvaluator>(build.Evaluator);
        Assert.Equal("single:ollama", build.Mode);
        Assert.Empty(build.Warnings);
    }

    [Fact]
    public void Factory_skips_unknown_provider_without_config()
    {
        var build = SemanticProviderFactory.Build(new Dictionary<string, string> { ["TEOS_LLM_PROVIDERS"] = "nonsense" });
        Assert.Null(build.Evaluator);
        Assert.Equal("neutral-50", build.Mode);
        Assert.Contains(build.Warnings, w => w.Contains("nonsense", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Factory_no_config_returns_null_neutral()
    {
        var build = SemanticProviderFactory.Build(new Dictionary<string, string>());
        Assert.Null(build.Evaluator);
        Assert.Equal("neutral-50", build.Mode);
    }

    // ---- adapter transport (in-memory handler, never a real network call) --------

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            const string body = """{"choices":[{"message":{"content":"{\"relevance\": 50, \"rationale\": \"ok\"}"}}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Adapter_uses_configured_endpoint_and_omits_auth_when_keyless()
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler);
        var evaluator = new ChatCompletionsSemanticEvaluator(http, apiKey: null, model: "qwen2.5:7b", baseUrl: "http://localhost:11434/v1");

        var raw = await evaluator.EvaluateAsync("o", "p");

        Assert.Equal("""{"relevance": 50, "rationale": "ok"}""", raw);
        Assert.Equal("http://localhost:11434/v1/chat/completions", handler.Last!.RequestUri!.ToString());
        Assert.False(handler.Last.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task Adapter_adds_bearer_header_when_key_present()
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler);
        var evaluator = new ChatCompletionsSemanticEvaluator(http, apiKey: "secret-test-key", model: "gpt-4o-mini", baseUrl: "https://api.groq.com/openai/v1");

        await evaluator.EvaluateAsync("o", "p");

        var auth = Assert.Single(handler.Last!.Headers.GetValues("Authorization"));
        Assert.Equal("Bearer secret-test-key", auth);
        Assert.Equal("https://api.groq.com/openai/v1/chat/completions", handler.Last.RequestUri!.ToString());
    }
}