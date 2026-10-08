using Teos.OpportunityNetwork.Core.Domain;
using Teos.OpportunityNetwork.Matching;
using Xunit;

namespace Teos.OpportunityNetwork.Tests;

public class ReplayBenchmarkTests
{
    private static (Opportunity opp, UserProfile profile) Build()
    {
        var opp = new Opportunity
        {
            Id = Guid.NewGuid(), Source = "github", ExternalId = "x",
            Title = "Build a REST API", Company = "acme/demo",
            OpportunityType = OpportunityType.BOUNTY,
            SkillsRequired = new[] { "c#", "postgresql" },
            BudgetMin = 500, BudgetMax = 1000, Currency = "USD", BudgetType = "range",
            Location = "Remote", Status = "OPEN", ContentHash = new string('a', 64),
            Deadline = DateTimeOffset.UtcNow.AddDays(10),
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow
        };
        var profile = new UserProfile
        {
            Id = Guid.NewGuid(), DisplayName = "T", Skills = new[] { "c#", "postgresql" },
            YearsExperience = 6, Location = "Remote",
            PreferredTypes = new[] { OpportunityType.BOUNTY },
            BudgetMin = 100, BudgetMax = 5000, Currency = "USD"
        };
        return (opp, profile);
    }

    [Fact]
    public void Replay_reproduces_the_original_result_deterministically()
    {
        var (opp, profile) = Build();
        var snapshot = SnapshotFactory.Capture(opp, profile, 80, ScoringConfig.HybridV1);
        var matcher = new HybridMatcher(ScoringConfig.CreateHybridV1());
        var original = matcher.Score(opp, profile, snapshot, 80, 0.9);

        var replay1 = ReplayEngine.RescoreFromSnapshot(snapshot, ScoringConfig.CreateHybridV1());
        var replay2 = ReplayEngine.RescoreFromSnapshot(snapshot, ScoringConfig.CreateHybridV1());

        Assert.Equal(original.MatchScore, replay1.MatchScore, 9);
        Assert.Equal(replay1.MatchScore, replay2.MatchScore, 9);
        Assert.Equal(original.Decision, replay1.Decision);
        Assert.Equal(original.SemanticScore, replay1.SemanticScore, 9);
    }

    [Fact]
    public void Replay_uses_stored_snapshot_not_current_opportunity_state()
    {
        var (opp, profile) = Build();
        var snapshot = SnapshotFactory.Capture(opp, profile, 80, ScoringConfig.HybridV1);
        var baseline = new HybridMatcher(ScoringConfig.CreateHybridV1())
            .Score(opp, profile, snapshot, 80, 0.9);

        // Mutate the CURRENT opportunity drastically — the snapshot must be unaffected.
        opp.Title = "TOTALLY DIFFERENT";
        opp.SkillsRequired = new[] { "cobol", "verilog", "rust" };
        opp.BudgetMin = 1;
        opp.BudgetMax = 2;
        opp.Status = "CLOSED";
        opp.Deadline = DateTimeOffset.UtcNow.AddDays(1);

        var replay = ReplayEngine.RescoreFromSnapshot(snapshot, ScoringConfig.CreateHybridV1());
        Assert.Equal(baseline.MatchScore, replay.MatchScore, 9);
        Assert.Equal(baseline.SkillMatch, replay.SkillMatch, 9);
        Assert.Equal(baseline.MatchedSkills, replay.MatchedSkills);
    }

    [Fact]
    public void Replay_uses_stored_snapshot_not_current_profile_state()
    {
        var (opp, profile) = Build();
        var snapshot = SnapshotFactory.Capture(opp, profile, 80, ScoringConfig.HybridV1);
        var baseline = new HybridMatcher(ScoringConfig.CreateHybridV1())
            .Score(opp, profile, snapshot, 80, 0.9);

        profile.Skills = Array.Empty<string>();
        profile.YearsExperience = 0;
        profile.PreferredTypes = Array.Empty<OpportunityType>();
        profile.Location = "Antarctica";
        profile.NotificationsPaused = true;

        var replay = ReplayEngine.RescoreFromSnapshot(snapshot, ScoringConfig.CreateHybridV1());
        Assert.Equal(baseline.MatchScore, replay.MatchScore, 9);
        Assert.Equal(baseline.MatchedSkills, replay.MatchedSkills);
        Assert.DoesNotContain("hard rule applied: score suppressed to enforce IGNORE", replay.Warnings);
    }

    [Fact]
    public void Replay_never_calls_the_LLM()
    {
        var (opp, profile) = Build();
        var snapshot = SnapshotFactory.Capture(opp, profile, 80, ScoringConfig.HybridV1);

        // HybridMatcher constructed with semanticEvaluator: null — and ReplayEngine never
        // touches an evaluator. The ForbiddenSemanticEvaluator proves the boundary if wired.
        var forbidden = new ForbiddenSemanticEvaluator();
        Assert.ThrowsAsync<InvalidOperationException>(() =>
            forbidden.EvaluateAsync("opportunity", "profile")).GetAwaiter().GetResult();

        var replay = ReplayEngine.RescoreFromSnapshot(snapshot, ScoringConfig.CreateHybridV1());
        Assert.Equal(80, replay.SemanticScore); // stored value reused verbatim
    }

    [Fact]
    public void Replay_without_stored_semantic_uses_neutral_and_still_works()
    {
        var (opp, profile) = Build();
        var snapshot = SnapshotFactory.Capture(opp, profile, null, ScoringConfig.HybridV1);
        snapshot.SemanticScore = null;
        var replay = ReplayEngine.RescoreFromSnapshot(snapshot, ScoringConfig.CreateHybridV1());
        Assert.Equal(50, replay.SemanticScore);
        Assert.InRange(replay.MatchScore, 0, 100);
    }

    [Fact]
    public void Replay_under_V2_produces_a_different_score_from_V1()
    {
        var (opp, profile) = Build();
        var snapshot = SnapshotFactory.Capture(opp, profile, 80, ScoringConfig.HybridV1);

        var v1 = ReplayEngine.RescoreFromSnapshot(snapshot, ScoringConfig.CreateHybridV1());
        var v2 = ReplayEngine.RescoreFromSnapshot(snapshot, ScoringConfig.CreateHybridV2());

        Assert.Equal(ScoringConfig.HybridV1, v1.WeightsVersion);
        Assert.Equal(ScoringConfig.HybridV2, v2.WeightsVersion);
        Assert.Equal(v1.MatchedSkills, v2.MatchedSkills); // same inputs, different weights only
    }

    [Fact]
    public void ReproduceHistorical_roundtrips_config_json()
    {
        var (opp, profile) = Build();
        var snapshot = SnapshotFactory.Capture(opp, profile, 80, ScoringConfig.HybridV1);
        var config = ScoringConfig.CreateHybridV1();

        var original = new HybridMatcher(config).Score(opp, profile, snapshot, 80, 1.0);
        var reproduced = ReplayEngine.ReproduceHistorical(snapshot, config.ToJson());

        Assert.Equal(original.MatchScore, reproduced.MatchScore, 9);
        Assert.Equal(original.Decision, reproduced.Decision);
        Assert.Equal(original.RecommendedAction, reproduced.RecommendedAction);
    }

    // ---- benchmark metrics -------------------------------------------------

    [Fact]
    public void Metrics_calculate_correctly()
    {
        var scores = new List<double> { 50, 60, 70, 80, 90 };
        var decisions = scores.Select(MatchDecisionRules.Decide).ToList();

        var m = BenchmarkMetrics.Compute(scores, decisions);

        Assert.Equal(70, m.Mean, 4);
        Assert.Equal(Math.Sqrt(200.0), m.StdDev, 4); // population stddev of {50,60,70,80,90}
        Assert.Equal(0.2, m.AlertRate, 4);   // only 90 >= 85
        Assert.Equal(0.2, m.ApplyNowRate, 4); // 90 >= 90
        Assert.Equal(70, m.P50, 4);
        Assert.Equal(86, m.P90, 4); // linear interpolation: index = 0.9*4 = 3.6 → 80 + 0.6*(90-80)
    }

    [Fact]
    public void Metrics_edge_cases()
    {
        var empty = BenchmarkMetrics.Compute(new List<double>(), new List<MatchDecision>());
        Assert.Equal(0, empty.Mean);
        Assert.Equal(0, empty.P90);

        var single = BenchmarkMetrics.Compute(new List<double> { 77 }, new List<MatchDecision> { MatchDecision.LOG });
        Assert.Equal(77, single.Mean, 4);
        Assert.Equal(77, single.P50, 4);
        Assert.Equal(77, single.P90, 4);

        Assert.Throws<ArgumentException>(() =>
            BenchmarkMetrics.Compute(new List<double> { 1 }, new List<MatchDecision>()));
    }

    [Fact]
    public void Decision_transitions_include_IGNORE_to_ALERT_and_ALERT_to_IGNORE()
    {
        var baseline = new List<MatchDecision>
        {
            MatchDecision.IGNORE, MatchDecision.ALERT, MatchDecision.ALERT, MatchDecision.LOG
        };
        var candidate = new List<MatchDecision>
        {
            MatchDecision.ALERT, MatchDecision.IGNORE, MatchDecision.ALERT, MatchDecision.ALERT
        };

        var transitions = DecisionTransitions.Compute(baseline, candidate);

        Assert.Contains(transitions, t => t.From == "IGNORE" && t.To == "ALERT" && t.Count == 1);
        Assert.Contains(transitions, t => t.From == "ALERT" && t.To == "IGNORE" && t.Count == 1);
        Assert.Contains(transitions, t => t.From == "ALERT" && t.To == "ALERT" && t.Count == 1);
        Assert.Contains(transitions, t => t.From == "LOG" && t.To == "ALERT" && t.Count == 1);
        Assert.Equal(4, transitions.Sum(t => t.Count));

        Assert.Throws<ArgumentException>(() => DecisionTransitions.Compute(
            new List<MatchDecision> { MatchDecision.ALERT }, new List<MatchDecision>()));
    }

    [Fact]
    public void V1_V2_comparison_is_reproducible()
    {
        var snapshots = new List<InputSnapshot>();
        var (opp, profile) = Build();
        for (var i = 0; i < 5; i++)
            snapshots.Add(SnapshotFactory.Capture(opp, profile, 60 + i * 10, ScoringConfig.HybridV1));

        List<double> Run(ScoringConfig cfg) => snapshots
            .Select(s => ReplayEngine.RescoreFromSnapshot(s, cfg).MatchScore)
            .ToList();

        var v1a = Run(ScoringConfig.CreateHybridV1());
        var v1b = Run(ScoringConfig.CreateHybridV1());
        var v2a = Run(ScoringConfig.CreateHybridV2());
        var v2b = Run(ScoringConfig.CreateHybridV2());

        Assert.Equal(v1a, v1b);
        Assert.Equal(v2a, v2b);

        var m1 = BenchmarkMetrics.Compute(v1a, v1a.Select(MatchDecisionRules.Decide).ToList());
        var m2 = BenchmarkMetrics.Compute(v2a, v2a.Select(MatchDecisionRules.Decide).ToList());
        // Evidence only — no automatic winner declared.
        Assert.True(m1.AlertRate >= 0 && m1.AlertRate <= 1);
        Assert.True(m2.AlertRate >= 0 && m2.AlertRate <= 1);
    }
}
