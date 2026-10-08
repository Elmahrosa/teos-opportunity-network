using Teos.OpportunityNetwork.Core.Domain;
using Teos.OpportunityNetwork.Matching;
using Xunit;

namespace Teos.OpportunityNetwork.Tests;

public class MatchingTests
{
    private static Opportunity NewOpportunity(Action<Opportunity>? mutate = null)
    {
        var o = new Opportunity
        {
            Id = Guid.NewGuid(),
            Source = "github",
            ExternalId = "test-1",
            Title = "Build a REST API",
            Company = "acme/demo",
            Url = "https://github.com/acme/demo/issues/1",
            OpportunityType = OpportunityType.BOUNTY,
            SkillsRequired = new[] { "c#", "postgresql" },
            BudgetMin = 500,
            BudgetMax = 1000,
            Currency = "USD",
            BudgetType = "range",
            Location = "Remote",
            Status = "OPEN",
            ContentHash = new string('a', 64),
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        };
        mutate?.Invoke(o);
        return o;
    }

    private static UserProfile NewProfile(Action<UserProfile>? mutate = null)
    {
        var p = new UserProfile
        {
            Id = Guid.NewGuid(),
            DisplayName = "Test User",
            Skills = new[] { "c#", "postgresql", "docker" },
            YearsExperience = 6,
            Location = "Remote",
            PreferredTypes = new[] { OpportunityType.BOUNTY, OpportunityType.PROJECT },
            BudgetMin = 100,
            BudgetMax = 5000,
            Currency = "USD",
            Keywords = new[] { "api" }
        };
        mutate?.Invoke(p);
        return p;
    }

    private static MatchResult Score(Opportunity o, UserProfile p, ScoringConfig? cfg = null, double semantic = 80, double confidence = 0.9)
    {
        var matcher = new HybridMatcher(cfg ?? ScoringConfig.CreateHybridV1());
        var snapshot = SnapshotFactory.Capture(o, p, semantic, (cfg ?? ScoringConfig.CreateHybridV1()).WeightsVersion);
        return matcher.Score(o, p, snapshot, semantic, confidence);
    }

    // ---- weights ----------------------------------------------------------

    [Fact]
    public void HybridV1_weights_sum_to_1()
    {
        var cfg = ScoringConfig.CreateHybridV1();
        cfg.Validate();
        Assert.Equal(1.0, cfg.Weights.Values.Sum(), 9);
        Assert.Equal(0.35, cfg.Weights["skills"]);
        Assert.Equal(0.20, cfg.Weights["experience"]);
        Assert.Equal(0.15, cfg.Weights["budget"]);
        Assert.Equal(0.10, cfg.Weights["type"]);
        Assert.Equal(0.10, cfg.Weights["location"]);
        Assert.Equal(0.05, cfg.Weights["urgency"]);
        Assert.Equal(0.05, cfg.Weights["semantic"]);
        Assert.Equal(ScoringConfig.HybridV1, cfg.WeightsVersion);
    }

    [Fact]
    public void HybridV2_weights_sum_to_1_and_is_experimental_version()
    {
        var cfg = ScoringConfig.CreateHybridV2();
        cfg.Validate();
        Assert.Equal(1.0, cfg.Weights.Values.Sum(), 9);
        Assert.Equal(0.30, cfg.Weights["skills"]);
        Assert.Equal(0.10, cfg.Weights["semantic"]);
        Assert.Equal(ScoringConfig.HybridV2, cfg.WeightsVersion);
        // V1 baseline untouched
        Assert.Equal(ScoringConfig.HybridV1, ScoringConfig.CreateHybridV1().WeightsVersion);
    }

    [Fact]
    public void Invalid_weights_fail_validation()
    {
        var bad = ScoringConfig.CreateHybridV1();
        bad.Weights["skills"] = 0.50; // total now != 1.0
        Assert.Throws<ScoringConfigException>(() => bad.Validate());

        var negative = ScoringConfig.CreateHybridV1();
        negative.Weights["semantic"] = -0.05;
        Assert.Throws<ScoringConfigException>(() => negative.Validate());

        var unknown = ScoringConfig.CreateHybridV1();
        unknown.Weights["llm"] = 0.0;
        Assert.Throws<ScoringConfigException>(() => unknown.Validate());

        var missing = ScoringConfig.CreateHybridV1();
        missing.Weights.Remove("urgency");
        Assert.Throws<ScoringConfigException>(() => missing.Validate());
    }

    // ---- eligibility ------------------------------------------------------

    [Fact]
    public void Unknown_budget_bounty_remains_eligible_and_is_not_penalized()
    {
        var opp = NewOpportunity(o =>
        {
            o.BudgetMin = null; o.BudgetMax = null; o.BudgetType = "unknown";
        });
        var profile = NewProfile();

        var result = Score(opp, profile);

        Assert.Equal(0.5, result.BudgetMatch, 9); // neutral, not a negative
        // It must still be able to reach ALERT with strong components:
        var strong = Score(opp, profile, semantic: 100, confidence: 1.0);
        Assert.True(strong.MatchScore >= 85,
            $"unknown-budget bounty should remain eligible; got {strong.MatchScore}");
    }

    // ---- confidence is metadata -------------------------------------------

    [Fact]
    public void Low_confidence_does_not_lower_score()
    {
        var opp = NewOpportunity();
        var profile = NewProfile();

        var high = Score(opp, profile, confidence: 1.0);
        var low = Score(opp, profile, confidence: 0.01);

        Assert.Equal(high.MatchScore, low.MatchScore, 9);
        Assert.Equal(0.01, low.Confidence, 9);
        Assert.NotEqual(1.0, low.Confidence);
    }

    // ---- LLM output validation --------------------------------------------

    [Fact]
    public void Invalid_LLM_output_is_rejected()
    {
        Assert.Throws<SemanticValidationException>(() => SemanticValidator.Validate(null));
        Assert.Throws<SemanticValidationException>(() => SemanticValidator.Validate(""));
        Assert.Throws<SemanticValidationException>(() => SemanticValidator.Validate("not json at all"));
        Assert.Throws<SemanticValidationException>(() => SemanticValidator.Validate("{\"relevance\": \"high\"}"));
        Assert.Throws<SemanticValidationException>(() => SemanticValidator.Validate("{\"no_relevance\": 5}"));
    }

    [Fact]
    public void Semantic_output_above_100_is_rejected()
    {
        Assert.Throws<SemanticValidationException>(() => SemanticValidator.Validate("{\"relevance\": 100.1}"));
        Assert.Throws<SemanticValidationException>(() => SemanticValidator.Validate("{\"relevance\": -1}"));
        var ok = SemanticValidator.Validate("{\"relevance\": 100, \"rationale\": \"fits\", \"matched_keywords\": [\"c#\"]}");
        Assert.Equal(100, ok.Relevance);
    }

    [Fact]
    public void LLM_output_in_code_fences_is_accepted()
    {
        var ok = SemanticValidator.Validate("```json\n{\"relevance\": 72, \"rationale\": \"ok\"}\n```");
        Assert.Equal(72, ok.Relevance);
    }

    [Fact]
    public async Task Matcher_rejects_invalid_llm_output_before_scoring()
    {
        var bad = new StaticSemanticEvaluator("this is not json");
        var matcher = new HybridMatcher(ScoringConfig.CreateHybridV1(), bad);
        await Assert.ThrowsAsync<SemanticValidationException>(() =>
            matcher.MatchAsync(NewOpportunity(), NewProfile(), 0.9));
    }

    // ---- decisions & invariants --------------------------------------------

    [Theory]
    [InlineData(95, MatchDecision.ALERT, RecommendedAction.APPLY_NOW)]
    [InlineData(90, MatchDecision.ALERT, RecommendedAction.APPLY_NOW)]
    [InlineData(85, MatchDecision.ALERT, RecommendedAction.REVIEW)]
    [InlineData(84.99, MatchDecision.LOG, RecommendedAction.REVIEW)]
    [InlineData(75, MatchDecision.LOG, RecommendedAction.REVIEW)]
    [InlineData(74.99, MatchDecision.LOG, RecommendedAction.PASS)]
    [InlineData(60, MatchDecision.LOG, RecommendedAction.PASS)]
    [InlineData(59.99, MatchDecision.IGNORE, RecommendedAction.PASS)]
    [InlineData(0, MatchDecision.IGNORE, RecommendedAction.PASS)]
    public void Decision_and_action_thresholds_are_exact(double score, MatchDecision decision, RecommendedAction action)
    {
        Assert.Equal(decision, MatchDecisionRules.Decide(score));
        Assert.Equal(action, MatchDecisionRules.Recommend(score));
        MatchDecisionRules.AssertInvariants(score, decision, action);
    }

    [Fact]
    public void Score_at_least_90_cannot_be_IGNORE_or_PASS()
    {
        foreach (var score in new[] { 90.0, 95.0, 100.0 })
        {
            var ex1 = Assert.Throws<MatchInvariantException>(() =>
                MatchDecisionRules.AssertInvariants(score, MatchDecision.IGNORE, RecommendedAction.APPLY_NOW));
            Assert.Contains("cannot be IGNORE", ex1.Message);

            var ex2 = Assert.Throws<MatchInvariantException>(() =>
                MatchDecisionRules.AssertInvariants(score, MatchDecision.ALERT, RecommendedAction.PASS));
            Assert.Contains("cannot be PASS", ex2.Message);
        }
    }

    [Fact]
    public void Matcher_produces_consistent_decision_and_action()
    {
        var result = Score(NewOpportunity(), NewProfile(), semantic: 95);
        MatchDecisionRules.AssertInvariants(result.MatchScore, result.Decision, result.RecommendedAction);
        Assert.True(result.MatchScore >= 85, $"expected ALERT band, got {result.MatchScore}");
        Assert.Equal(MatchDecision.ALERT, result.Decision);
    }

    [Fact]
    public void Semantic_weight_cannot_dominate_score_llm_only_matching_is_impossible()
    {
        // Worst-case deterministic components vs best-case: semantic alone must not push to ALERT.
        var opp = NewOpportunity(o =>
        {
            o.SkillsRequired = new[] { "rust", "cobol", "verilog" }; // profile has none
            o.Location = "Antarctica";
            o.OpportunityType = OpportunityType.JOB;
            o.Deadline = DateTimeOffset.UtcNow.AddDays(365);
        });
        var profile = NewProfile(p =>
        {
            p.Skills = Array.Empty<string>();
            p.PreferredTypes = Array.Empty<OpportunityType>();
            p.Location = "Mars";
        });

        var result = Score(opp, profile, semantic: 100);
        Assert.True(result.MatchScore < 60,
            $"max semantic must not carry the score; got {result.MatchScore}");
        Assert.Equal(100, result.SemanticScore);
    }

    [Fact]
    public void Score_is_clamped_and_finite()
    {
        var result = Score(NewOpportunity(), NewProfile(), semantic: 100, confidence: 1);
        Assert.InRange(result.MatchScore, 0, 100);
        Assert.False(double.IsNaN(result.MatchScore));
    }
}
