using System.Reflection;
using Xunit;

namespace Teos.OpportunityNetwork.Tests;

/// <summary>
/// Architecture boundary tests: Telegram ✕ Matcher, ✕ LLM, ✕ Dedup, ✕ Benchmark.
/// The Telegram assembly may reference Core (domain) + Persistence (repositories) only.
/// </summary>
public class BoundaryTests
{
    private static readonly Assembly Telegram = typeof(Telegram.TelegramWorker).Assembly;
    private static readonly Assembly Matching = typeof(Matching.HybridMatcher).Assembly;
    private static readonly Assembly Ingestion = typeof(Ingestion.Deduplicator).Assembly;

    private static void AssertAbsent(string token, string text, string context)
    {
        Assert.True(text.IndexOf(token, StringComparison.Ordinal) < 0,
            $"{context} must not contain '{token}'");
    }

    [Fact]
    public void Telegram_does_not_reference_matcher_assembly()
    {
        var referenced = Telegram.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.DoesNotContain(Matching.FullName!.Split(',')[0], referenced);
        Assert.Contains(typeof(Persistence.MatchRepository).Assembly.GetName().Name!, referenced);
        Assert.Contains(typeof(Core.Domain.MatchResult).Assembly.GetName().Name!, referenced);
    }

    [Fact]
    public void Telegram_does_not_reference_dedup_collector_assembly()
    {
        var referenced = Telegram.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.DoesNotContain(Ingestion.FullName!.Split(',')[0], referenced);
    }

    [Fact]
    public void Telegram_source_files_import_no_forbidden_namespaces()
    {
        var dir = FindSourceDir(Path.Combine("src", "Teos.OpportunityNetwork.Telegram"));
        var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        string[] forbidden =
        {
            "Teos.OpportunityNetwork.Matching",
            "Teos.OpportunityNetwork.Ingestion",
            "OpenAI", "Anthropic"
        };

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var ns in forbidden)
            {
                AssertAbsent($"using {ns}", text, $"Telegram file {Path.GetFileName(file)}");
            }
        }
    }

    [Fact]
    public void Telegram_source_contains_no_scoring_llm_or_dedup_logic()
    {
        var dir = FindSourceDir(Path.Combine("src", "Teos.OpportunityNetwork.Telegram"));
        foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            AssertAbsent("SemanticValidator", text, name);
            AssertAbsent("ISemanticEvaluator", text, name);
            AssertAbsent("ReplayEngine", text, name);
            AssertAbsent("BenchmarkMetrics", text, name);
            AssertAbsent("Deduplicator", text, name);
            AssertAbsent("ScoringConfig.Create", text, name);
            // No LLM API endpoints in Telegram transport
            AssertAbsent("api.openai.com", text, name);
        }
    }

    [Fact]
    public void Persistence_source_contains_no_telegram_logic()
    {
        var dir = FindSourceDir(Path.Combine("src", "Teos.OpportunityNetwork.Persistence"));
        foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            AssertAbsent("api.telegram.org", text, name);
            AssertAbsent("SendMessage", text, name);
            AssertAbsent("TelegramFormatter", text, name);
            AssertAbsent("ScoringConfig.Create", text, name); // no scoring here either
        }
    }

    [Fact]
    public void Matching_assembly_does_not_reference_telegram()
    {
        var referenced = Matching.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.DoesNotContain(Telegram.FullName!.Split(',')[0], referenced);
        Assert.DoesNotContain(typeof(Telegram.TelegramWorker).Assembly.GetName().Name!, referenced);
    }

    [Fact]
    public void No_source_file_hardcodes_secrets()
    {
        string[] roots = { FindSourceDir("src"), FindSourceDir("Tests"), FindSourceDir("migrations") };
        string[] patterns = { "TELEGRAM_BOT_TOKEN=", "GITHUB_TOKEN=", "OPENAI_API_KEY=", "sk-proj-", "ghp_" };

        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var file in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".example")) continue;
                var text = File.ReadAllText(file);
                foreach (var pattern in patterns)
                {
                    // Only flag assignments with a non-placeholder value.
                    var idx = text.IndexOf(pattern, StringComparison.Ordinal);
                    if (idx < 0) continue;
                    var after = text[(idx + pattern.Length)..];
                    var value = after.Split('"', '\'', ' ', '\r', '\n')[0];
                    var isPlaceholder = value.Length == 0 ||
                                        value.Contains("<") || value.Contains("your") ||
                                        value.Contains("PLACEHOLDER");
                    Assert.True(isPlaceholder,
                        $"{Path.GetFileName(file)} appears to hardcode secret pattern '{pattern}'");
                }
            }
        }
    }

    private static string FindSourceDir(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent!;
        }
        throw new DirectoryNotFoundException(string.Join("/", parts));
    }
}
