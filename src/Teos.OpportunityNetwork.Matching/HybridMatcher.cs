using System.Text.Json;
using System.Text.Json.Serialization;
using Teos.OpportunityNetwork.Core.Domain;

namespace Teos.OpportunityNetwork.Matching;

/// <summary>
/// HYBRID_V1 matcher. Pure and deterministic: identical inputs and config always yield the
/// identical score. Confidence is recorded as metadata and never modifies the score.
///
/// The semantic component comes from a validated LLM judgment in the async path, or from a
/// stored snapshot value during replay. The synchronous <see cref="Score"/> path never calls
/// an LLM.
/// </summary>
public sealed class HybridMatcher
{
    private readonly ScoringConfig _config;
    private readonly ISemanticEvaluator? _evaluator;

    public HybridMatcher(ScoringConfig config, ISemanticEvaluator? semanticEvaluator = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _config.Validate();
        _evaluator = semanticEvaluator;
    }

    /// <summary>Deterministic scoring from a frozen snapshot. No I/O, no LLM.</summary>
    public MatchResult Score(Opportunity opportunity, UserProfile profile, InputSnapshot snapshot, double semanticScore, double confidence)
    {
        ArgumentNullException.ThrowIfNull(opportunity);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(snapshot);
        return MatchScoring.Build(_config, opportunity, profile, snapshot, semanticScore, confidence);
    }

    /// <summary>
    /// Full pipeline: evaluate semantics (if configured) → validate schema → capture snapshot →
    /// score. Invalid LLM output throws <see cref="SemanticValidationException"/> before scoring.
    /// </summary>
    public async Task<MatchResult> MatchAsync(Opportunity opportunity, UserProfile profile, double confidence = 0.9, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(opportunity);
        ArgumentNullException.ThrowIfNull(profile);

        double semantic;
        if (_evaluator is null)
        {
            semantic = MatchScoring.NeutralSemantic; // no LLM configured → neutral, never invented
        }
        else
        {
            var raw = await _evaluator.EvaluateAsync(Describe(opportunity), Describe(profile), ct).ConfigureAwait(false);
            var judgment = SemanticValidator.Validate(raw); // gate BEFORE scoring
            semantic = judgment.Relevance;
        }

        var snapshot = SnapshotFactory.Capture(opportunity, profile, semantic, _config.WeightsVersion);
        return Score(opportunity, profile, snapshot, semantic, confidence);
    }

    private static string Describe(Opportunity o) =>
        $"{o.Title} | type={o.OpportunityType} | skills={string.Join(",", o.SkillsRequired)} | location={o.Location} | budget={o.BudgetMin}-{o.BudgetMax} {o.Currency}";

    private static string Describe(UserProfile p) =>
        $"{p.DisplayName} | skills={string.Join(",", p.Skills)} | experience={p.YearsExperience}y | location={p.Location} | types={string.Join(",", p.PreferredTypes)}";
}

/// <summary>
/// Captures immutable input evidence. The live opportunity/profile are deep-copied through
/// JSON so later mutation cannot leak into a snapshot used for replay.
/// </summary>
public static class SnapshotFactory
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static InputSnapshot Capture(Opportunity opportunity, UserProfile profile, double? semanticScore, string weightsVersion)
    {
        ArgumentNullException.ThrowIfNull(opportunity);
        ArgumentNullException.ThrowIfNull(profile);

        var opp = JsonSerializer.Deserialize<Opportunity>(JsonSerializer.Serialize(opportunity, Options), Options)!;
        var prof = JsonSerializer.Deserialize<UserProfile>(JsonSerializer.Serialize(profile, Options), Options)!;

        return new InputSnapshot
        {
            SnapshotSourceId = Guid.NewGuid(),
            WeightsVersion = weightsVersion,
            SemanticScore = semanticScore,
            CapturedAt = DateTimeOffset.UtcNow,
            Opportunity = opp,
            Profile = prof
        };
    }
}

/// <summary>
/// Shared pure scoring core used by both live matching and deterministic replay.
/// Component scores are 0..1; the final score is 0..100.
/// </summary>
internal static class MatchScoring
{
    public const double NeutralSemantic = 50;

    public static MatchResult Build(
        ScoringConfig config,
        Opportunity opp,
        UserProfile profile,
        InputSnapshot snapshot,
        double semantic,
        double confidence)
    {
        var (skillMatch, matched, missing) = Skills(opp, profile);
        var experience = Experience(profile);
        var budget = Budget(opp, profile);
        var type = TypeFit(opp, profile);
        var location = Location(opp, profile);
        var urgency = Urgency(opp);
        var semanticNorm = Math.Clamp(semantic / 100.0, 0.0, 1.0);

        var raw = 100.0 * (
            config.Weights["skills"] * skillMatch +
            config.Weights["experience"] * experience +
            config.Weights["budget"] * budget +
            config.Weights["type"] * type +
            config.Weights["location"] * location +
            config.Weights["urgency"] * urgency +
            config.Weights["semantic"] * semanticNorm);

        if (double.IsNaN(raw) || double.IsInfinity(raw))
            raw = 0;
        var score = Math.Clamp(raw, 0, 100);

        var warnings = new List<string>();
        if (profile.NotificationsPaused)
        {
            // Hard rule: paused users must never receive an ALERT decision.
            score = 0;
            warnings.Add("hard rule applied: score suppressed to enforce IGNORE");
        }

        var decision = MatchDecisionRules.Decide(score);
        var action = MatchDecisionRules.Recommend(score);
        MatchDecisionRules.AssertInvariants(score, decision, action);

        var reasons = new List<string>
        {
            $"skills: {matched.Length}/{Math.Max(1, matched.Length + missing.Length)} matched",
            $"experience: {experience:0.00}",
            $"budget: {budget:0.00}",
            $"type fit: {type:0.00}",
            $"location: {location:0.00}",
            $"urgency: {urgency:0.00}",
            $"semantic: {semantic:0.##}"
        };

        return new MatchResult
        {
            Id = Guid.NewGuid(),
            OpportunityId = opp.Id,
            UserId = profile.Id,
            MatchScore = score,
            Decision = decision,
            RecommendedAction = action,
            SkillMatch = skillMatch,
            ExperienceMatch = experience,
            BudgetMatch = budget,
            LocationMatch = location,
            UrgencyScore = urgency,
            SemanticScore = semantic,
            Confidence = confidence,
            MatchedSkills = matched,
            MissingSkills = missing,
            Reasons = reasons.ToArray(),
            Warnings = warnings.ToArray(),
            ScoringConfig = config.ToJson(),
            WeightsVersion = config.WeightsVersion,
            EngineVersion = config.EngineVersion,
            SnapshotSourceId = snapshot.SnapshotSourceId,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static string Norm(string value) => value.Trim().ToLowerInvariant();

    private static (double score, string[] matched, string[] missing) Skills(Opportunity opp, UserProfile profile)
    {
        var required = (opp.SkillsRequired ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(Norm)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (required.Length == 0)
            return (0.5, Array.Empty<string>(), Array.Empty<string>());

        var have = new HashSet<string>(
            (profile.Skills ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(Norm),
            StringComparer.Ordinal);

        var matched = required.Where(have.Contains).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var missing = required.Where(s => !have.Contains(s)).OrderBy(s => s, StringComparer.Ordinal).ToArray();

        return ((double)matched.Length / required.Length, matched, missing);
    }

    private static double Experience(UserProfile profile)
    {
        if (profile.YearsExperience <= 0)
            return 0;
        return Math.Clamp(profile.YearsExperience / 5.0, 0, 1);
    }

    private static double Budget(Opportunity opp, UserProfile profile)
    {
        // Unknown budget on either side is neutral — never a penalty.
        if (opp.BudgetMin is null && opp.BudgetMax is null) return 0.5;
        if (profile.BudgetMin is null && profile.BudgetMax is null) return 0.5;

        var oMin = opp.BudgetMin ?? opp.BudgetMax!.Value;
        var oMax = opp.BudgetMax ?? opp.BudgetMin!.Value;
        var pMin = profile.BudgetMin ?? profile.BudgetMax!.Value;
        var pMax = profile.BudgetMax ?? profile.BudgetMin!.Value;

        if (oMin > oMax) (oMin, oMax) = (oMax, oMin);
        if (pMin > pMax) (pMin, pMax) = (pMax, pMin);

        var span = oMax - oMin;
        if (span == 0)
            return oMin >= pMin && oMin <= pMax ? 1.0 : 0.0;

        var overlap = Math.Max(0m, Math.Min(oMax, pMax) - Math.Max(oMin, pMin));
        return (double)Math.Clamp(overlap / span, 0m, 1m);
    }

    private static double TypeFit(Opportunity opp, UserProfile profile)
    {
        var preferred = profile.PreferredTypes ?? Array.Empty<OpportunityType>();
        if (preferred.Length == 0) return 0.0;
        return preferred.Contains(opp.OpportunityType) ? 1.0 : 0.0;
    }

    private static double Location(Opportunity opp, UserProfile profile)
    {
        var a = string.IsNullOrWhiteSpace(opp.Location) ? null : Norm(opp.Location);
        var b = string.IsNullOrWhiteSpace(profile.Location) ? null : Norm(profile.Location);

        if (a is null && b is null) return 0.5;
        if (a is not null && b is not null && a == b) return 1.0;
        if (a?.Contains("remote", StringComparison.Ordinal) == true ||
            b?.Contains("remote", StringComparison.Ordinal) == true) return 0.8;
        if (a is null || b is null) return 0.3;
        return 0.0;
    }

    private static double Urgency(Opportunity opp)
    {
        if (opp.Deadline is null) return 0.5;
        var days = (opp.Deadline.Value - DateTimeOffset.UtcNow).TotalDays;
        if (days < 0) return 0.0;
        if (days <= 7) return 1.0;
        if (days <= 30) return 0.8;
        if (days <= 90) return 0.5;
        if (days <= 180) return 0.3;
        return 0.1;
    }
}
