using System.Text.Json;
using Teos.OpportunityNetwork.Collectors;
using Teos.OpportunityNetwork.Core;
using Teos.OpportunityNetwork.Ingestion;
using Teos.OpportunityNetwork.Valuation;

namespace Teos.OpportunityNetwork.Runner;

/// <summary>
/// Repo valuation CLI — the original single-purpose entry point, preserved as a first-class
/// mode of the Runner. Uses the shared Ingestion.Normalizer (no local domain redefinitions;
/// see BoundaryTests).
/// </summary>
public static class ValuationModes
{
    public static async Task<int> RunAsync(ValuationArgs args, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(args.OutputJson))
            return await JsonModeAsync(args, ct).ConfigureAwait(false);

        if (args.Smoke)
            return SmokeMode();

        return await LiveModeAsync(args, ct).ConfigureAwait(false);
    }

    /// <summary>Deterministic smoke valuation (exact output must stay stable — the action and CI rely on it).</summary>
    private static int SmokeMode()
    {
        Console.WriteLine("=== TEOS Repo Valuation Smoke Test ===");

        var rawData = new RawRepoData
        {
            Url = "https://github.com/Elmahrosa/teos-opportunity-network",
            FullName = "Elmahrosa/teos-opportunity-network",
            Name = "teos-opportunity-network",
            Stars = 12,
            SizeKb = 2500,
            LanguageCount = 2,
            UpdatedAt = DateTime.UtcNow.AddDays(-1)
        };

        Console.WriteLine($"Collected: {rawData.FullName} | Stars={rawData.Stars} | Size={rawData.SizeKb}KB | Langs={rawData.LanguageCount}");

        var engine = new RepoValuationEngine();
        var result = engine.Evaluate(rawData);

        Console.WriteLine($"Valuation: Score={result.Score}/100 | ${result.EstimatedLow:F0}-${result.EstimatedHigh:F0} | ${result.EstimatedAvg:F0} | {result.Decision}/{result.Action}");
        Console.WriteLine($"Metrics: LOC={result.Metrics.EstimatedLoc} | Activity={result.Metrics.ActivityFactor} | Rep={result.Metrics.ReputationFactor}");

        var normalized = Normalizer.NormalizeRepo(rawData, result, DateTimeOffset.UtcNow);
        Console.WriteLine($"Normalized: Type={normalized.OpportunityType} | Title={normalized.Title} | Budget=${normalized.BudgetMin:F0}-${normalized.BudgetMax:F0}");

        Console.WriteLine("=== Smoke Test PASSED ===");
        return 0;
    }

    /// <summary>Real GitHub data, real collector — no fake "live" output.</summary>
    private static async Task<int> LiveModeAsync(ValuationArgs args, CancellationToken ct)
    {
        Console.WriteLine("=== TEOS Repo Valuation ===");

        string url = args.RepoUrl ?? Environment.GetEnvironmentVariable("INPUT_REPO_URL")
                   ?? "https://github.com/Elmahrosa/teos-opportunity-network";
        string? token = Environment.GetEnvironmentVariable("TEOS_GITHUB_TOKEN")
                     ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");

        RawRepoData rawData;
        if (!string.IsNullOrEmpty(token))
        {
            var collector = new GitHubRepoCollector(new HttpClient(), token);
            try
            {
                rawData = await collector.CollectAsync(url, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"::error::Failed to collect repo data: {ex.Message}");
                return 1;
            }
            Console.WriteLine($"Live data collected for {url} (GITHUB_TOKEN present)");
        }
        else
        {
            Console.WriteLine("Mock mode (no GITHUB_TOKEN): the valuation below is synthetic, NOT live.");
            rawData = new RawRepoData
            {
                Url = url,
                FullName = "Elmahrosa/teos-opportunity-network",
                Name = "teos-opportunity-network",
                Stars = 12,
                SizeKb = 2500,
                LanguageCount = 2,
                UpdatedAt = DateTime.UtcNow.AddDays(-1)
            };
        }

        var display = string.IsNullOrEmpty(rawData.FullName) ? rawData.ExternalId : rawData.FullName;
        Console.WriteLine($"Collected: {display} | Stars={rawData.Stars} | Size={rawData.SizeKb}KB | Langs={rawData.LanguageCount}");

        var engine = new RepoValuationEngine();
        var result = engine.Evaluate(rawData);

        Console.WriteLine($"Valuation: Score={result.Score}/100 | ${result.EstimatedLow:F0}-${result.EstimatedHigh:F0} | ${result.EstimatedAvg:F0} | {result.Decision}/{result.Action}");
        Console.WriteLine($"Metrics: LOC={result.Metrics.EstimatedLoc} | Activity={result.Metrics.ActivityFactor} | Rep={result.Metrics.ReputationFactor}");

        var normalized = Normalizer.NormalizeRepo(rawData, result, DateTimeOffset.UtcNow);
        Console.WriteLine($"Normalized: Type={normalized.OpportunityType} | Title={normalized.Title} | Budget=${normalized.BudgetMin:F0}-${normalized.BudgetMax:F0}");

        Console.WriteLine("=== Valuation Complete ===");
        return 0;
    }

    /// <summary>JSON mode: real collector, valuation written to the requested file.</summary>
    private static async Task<int> JsonModeAsync(ValuationArgs args, CancellationToken ct)
    {
        string url = args.RepoUrl ?? Environment.GetEnvironmentVariable("INPUT_REPO_URL")
                   ?? "https://github.com/Elmahrosa/teos-opportunity-network";
        string? token = Environment.GetEnvironmentVariable("TEOS_GITHUB_TOKEN")
                     ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        string t = args.Tier ?? "basic";

        Console.WriteLine($"=== TEOS Repo Valuation (JSON) ===");
        Console.WriteLine($"Repo: {url}");
        Console.WriteLine($"Tier: {t}");

        var collector = new GitHubRepoCollector(new HttpClient(), token);
        RawRepoData raw;
        try
        {
            raw = await collector.CollectAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"::error::Failed to collect repo data: {ex.Message}");
            return 1;
        }

        var engine = new RepoValuationEngine();
        var result = engine.Evaluate(raw);

        var resultObj = new
        {
            score = result.Score,
            value_low = result.EstimatedLow,
            value_high = result.EstimatedHigh,
            decision = result.Decision
        };

        var json = JsonSerializer.Serialize(resultObj, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(args.OutputJson!, json, ct).ConfigureAwait(false);
        Console.WriteLine(json);
        return 0;
    }
}