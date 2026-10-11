using System;
using Teos.OpportunityNetwork.Core;

namespace Teos.OpportunityNetwork.Valuation
{
    /// <summary>
    /// Metrics used in valuation calculation.
    /// </summary>
    public sealed class RepoMetrics
    {
        public int EstimatedLoc { get; init; }
        public double ActivityFactor { get; init; }
        public double ReputationFactor { get; init; }
        public int DaysSinceUpdate { get; init; }
    }

    /// <summary>
    /// Result of repository valuation.
    /// </summary>
    public sealed class RepoValuationResult
    {
        public int Score { get; init; }
        public double EstimatedLow { get; init; }
        public double EstimatedHigh { get; init; }
        public double EstimatedAvg => (EstimatedLow + EstimatedHigh) / 2.0;
        public string Decision { get; init; } = "";
        public string Action { get; init; } = "";
        public RepoMetrics Metrics { get; init; } = new RepoMetrics();
    }

    /// <summary>
    /// Deterministic engine for valuing raw repository data.
    /// </summary>
    public sealed class RepoValuationEngine
    {
        /// <summary>
        /// Evaluates the given raw repository data and returns a valuation result.
        /// </summary>
        /// <param name="raw">The raw repository data to evaluate.</param>
        /// <returns>A valuation result containing score, estimated value range, decision, action, and metrics.</returns>
        public RepoValuationResult Evaluate(RawRepoData raw)
        {
            // Calculate days since update
            var daysSinceUpdate = (int)(DateTime.UtcNow - raw.UpdatedAt).TotalDays;

            // Activity factor based on days since update
            double activityFactor;
            if (daysSinceUpdate < 7)
                activityFactor = 1.5;
            else if (daysSinceUpdate < 30)
                activityFactor = 1.2;
            else if (daysSinceUpdate < 90)
                activityFactor = 1.0;
            else if (daysSinceUpdate < 180)
                activityFactor = 0.7;
            else
                activityFactor = 0.5;

            // Reputation factor: 0.8 + min(stars/100.0, 1.0)
            double reputationFactor = 0.8 + Math.Min(raw.Stars / 100.0, 1.0);

            // Estimated lines of code
            int estimatedLoc = (int)(raw.SizeKb * 5);

            // Base value
            double baseValue = (estimatedLoc * 0.02) + (raw.Languages.Count * 15);

            // License penalty
            if (string.IsNullOrWhiteSpace(raw.License) ||
                raw.License.Equals("NOASSERTION", StringComparison.OrdinalIgnoreCase))
            {
                baseValue *= 0.8;
            }

            // Final value range
            double finalLow = baseValue * activityFactor * reputationFactor * 0.7;
            double finalHigh = baseValue * activityFactor * reputationFactor * 1.6;

            // Score calculation
            double scoreRaw = activityFactor * 20 +
                              reputationFactor * 20 +
                              Math.Min(estimatedLoc / 200.0, 30) +
                              Math.Min(raw.Stars, 20) +
                              raw.Languages.Count * 2;
            int score = (int)Math.Min(95, scoreRaw);

            // Determine decision and action based on score
            string decision = score >= 85 ? "ALERT" : (score >= 60 ? "LOG" : "IGNORE");
            string action = score >= 90 ? "APPLY_NOW" : (score >= 75 ? "REVIEW" : "PASS");

            // Enforce invariants
            if (score >= 90)
            {
                if (decision == "IGNORE")
                    decision = "ALERT";
                if (action == "PASS")
                    action = "REVIEW";
            }

            // Build and return result
            return new RepoValuationResult
            {
                Score = score,
                EstimatedLow = finalLow,
                EstimatedHigh = finalHigh,
                Decision = decision,
                Action = action,
                Metrics = new RepoMetrics
                {
                    EstimatedLoc = estimatedLoc,
                    ActivityFactor = activityFactor,
                    ReputationFactor = reputationFactor,
                    DaysSinceUpdate = daysSinceUpdate
                }
            };
        }
    }
}