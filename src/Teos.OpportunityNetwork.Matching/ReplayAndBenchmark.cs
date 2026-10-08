using Teos.OpportunityNetwork.Core.Domain;

namespace Teos.OpportunityNetwork.Matching;

/// <summary>
/// Deterministic replay. Reads ONLY the immutable input snapshot — never live mutable state —
/// and never calls an LLM. The stored semantic score is reused verbatim (neutral 50 when absent).
/// </summary>
public static class ReplayEngine
{
    public static MatchResult RescoreFromSnapshot(InputSnapshot snapshot, ScoringConfig config)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();

        var semantic = snapshot.SemanticScore ?? MatchScoring.NeutralSemantic;
        return MatchScoring.Build(config, snapshot.Opportunity, snapshot.Profile, snapshot, semantic, confidence: 1.0);
    }

    /// <summary>Reproduces a historical run from the exact serialized config stored with the match.</summary>
    public static MatchResult ReproduceHistorical(InputSnapshot snapshot, string scoringConfigJson) =>
        RescoreFromSnapshot(snapshot, ScoringConfig.FromJson(scoringConfigJson));
}

/// <summary>Aggregate score metrics over a benchmark set. Fully deterministic.</summary>
public sealed class BenchmarkMetrics
{
    public int Count { get; init; }
    public double Mean { get; init; }
    /// <summary>Population standard deviation of the scores.</summary>
    public double StdDev { get; init; }
    /// <summary>Share of scores at or above the ALERT threshold (85).</summary>
    public double AlertRate { get; init; }
    /// <summary>Share of scores at or above the APPLY_NOW threshold (90).</summary>
    public double ApplyNowRate { get; init; }
    public double P50 { get; init; }
    public double P90 { get; init; }

    public static BenchmarkMetrics Compute(IReadOnlyList<double> scores, IReadOnlyList<MatchDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentNullException.ThrowIfNull(decisions);

        if (scores.Count != decisions.Count)
            throw new ArgumentException("scores and decisions must have the same number of elements");

        if (scores.Count == 0)
            return new BenchmarkMetrics();

        var sorted = scores.OrderBy(s => s).ToArray();
        var mean = scores.Average();
        var variance = scores.Average(s => (s - mean) * (s - mean));

        var alerts = scores.Count(s => s >= MatchDecisionRules.AlertThreshold);
        var applies = scores.Count(s => s >= MatchDecisionRules.ApplyNowThreshold);

        return new BenchmarkMetrics
        {
            Count = scores.Count,
            Mean = mean,
            StdDev = Math.Sqrt(variance),
            AlertRate = (double)alerts / scores.Count,
            ApplyNowRate = (double)applies / scores.Count,
            P50 = Percentile(sorted, 0.50),
            P90 = Percentile(sorted, 0.90)
        };
    }

    /// <summary>Linear-interpolation percentile (matches the common "index = p*(n-1)" definition).</summary>
    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length == 1) return sorted[0];

        var index = p * (sorted.Length - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper) return sorted[lower];

        return sorted[lower] + (index - lower) * (sorted[upper] - sorted[lower]);
    }
}

public sealed class DecisionTransition
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public int Count { get; init; }
}

/// <summary>Aggregates baseline → candidate decision movements for V1/V2 comparison.</summary>
public static class DecisionTransitions
{
    public static IReadOnlyList<DecisionTransition> Compute(
        IReadOnlyList<MatchDecision> baseline,
        IReadOnlyList<MatchDecision> candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        if (baseline.Count != candidate.Count)
            throw new ArgumentException("baseline and candidate must have the same number of elements");

        return baseline
            .Zip(candidate, (b, c) => (From: b.ToString(), To: c.ToString()))
            .GroupBy(x => x)
            .Select(g => new DecisionTransition { From = g.Key.From, To = g.Key.To, Count = g.Count() })
            .OrderBy(t => t.From, StringComparer.Ordinal)
            .ThenBy(t => t.To, StringComparer.Ordinal)
            .ToList();
    }
}
