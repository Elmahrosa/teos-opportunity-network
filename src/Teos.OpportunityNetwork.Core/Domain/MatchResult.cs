namespace Teos.OpportunityNetwork.Core.Domain;

/// <summary>
/// The persisted, immutable result of scoring one (opportunity, user) pair under one
/// (weights_version, engine_version). Component scores are 0..1; MatchScore is 0..100.
/// SemanticScore stores the raw validator relevance (0..100) actually used — replay
/// re-uses this value and never calls an LLM again.
/// </summary>
public sealed class MatchResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OpportunityId { get; set; }
    public Guid UserId { get; set; }
    public double MatchScore { get; set; }
    public MatchDecision Decision { get; set; }
    public RecommendedAction RecommendedAction { get; set; }
    public double SkillMatch { get; set; }
    public double ExperienceMatch { get; set; }
    public double BudgetMatch { get; set; }
    public double LocationMatch { get; set; }
    public double UrgencyScore { get; set; }
    public double SemanticScore { get; set; }
    public double Confidence { get; set; }
    public string[] MatchedSkills { get; set; } = Array.Empty<string>();
    public string[] MissingSkills { get; set; } = Array.Empty<string>();
    public string[] Reasons { get; set; } = Array.Empty<string>();
    public string[] Warnings { get; set; } = Array.Empty<string>();
    /// <summary>Serialized ScoringConfig used to produce this result (immutable evidence).</summary>
    public string ScoringConfig { get; set; } = "";
    public string WeightsVersion { get; set; } = "";
    public string EngineVersion { get; set; } = "";
    public Guid SnapshotSourceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
