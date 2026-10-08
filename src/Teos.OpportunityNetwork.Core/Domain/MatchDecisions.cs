namespace Teos.OpportunityNetwork.Core.Domain;

public enum MatchDecision
{
    ALERT,
    LOG,
    IGNORE
}

public enum RecommendedAction
{
    APPLY_NOW,
    REVIEW,
    PASS
}

/// <summary>Thrown when a (score, decision, action) triple violates a documented invariant.</summary>
public sealed class MatchInvariantException : Exception
{
    public MatchInvariantException(string message) : base(message) { }
}

/// <summary>
/// Canonical decision/action thresholds — the single source of truth used by code,
/// enforced in the database by CHECK constraints (see migrations/0001_init.sql).
///
///   score ≥ 85 → ALERT   |  ≥ 60 → LOG   |  &lt; 60 → IGNORE
///   score ≥ 90 → APPLY_NOW  |  ≥ 75 → REVIEW  |  &lt; 75 → PASS
///   score ≥ 90 can NEVER be IGNORE or PASS.
/// </summary>
public static class MatchDecisionRules
{
    public const double AlertThreshold = 85;
    public const double LogThreshold = 60;
    public const double ApplyNowThreshold = 90;
    public const double ReviewThreshold = 75;

    public static MatchDecision Decide(double score) =>
        score >= AlertThreshold ? MatchDecision.ALERT :
        score >= LogThreshold ? MatchDecision.LOG :
        MatchDecision.IGNORE;

    public static RecommendedAction Recommend(double score) =>
        score >= ApplyNowThreshold ? RecommendedAction.APPLY_NOW :
        score >= ReviewThreshold ? RecommendedAction.REVIEW :
        RecommendedAction.PASS;

    /// <summary>
    /// Validates the decision/action invariants. Hard invariants (score ≥ 90 must never be
    /// IGNORE / PASS) are checked first so their messages are stable, then full threshold
    /// consistency is enforced.
    /// </summary>
    public static void AssertInvariants(double score, MatchDecision decision, RecommendedAction action)
    {
        if (decision == MatchDecision.IGNORE && score >= ApplyNowThreshold)
            throw new MatchInvariantException(
                $"score {score:0.##} cannot be IGNORE: scores >= {ApplyNowThreshold} must be ALERT.");

        if (action == RecommendedAction.PASS && score >= ApplyNowThreshold)
            throw new MatchInvariantException(
                $"score {score:0.##} cannot be PASS: scores >= {ApplyNowThreshold} must be APPLY_NOW.");

        var expectedDecision = Decide(score);
        if (decision != expectedDecision)
            throw new MatchInvariantException(
                $"decision {decision} violates decision thresholds for score {score:0.##} (expected {expectedDecision}).");

        var expectedAction = Recommend(score);
        if (action != expectedAction)
            throw new MatchInvariantException(
                $"action {action} violates action thresholds for score {score:0.##} (expected {expectedAction}).");
    }
}
