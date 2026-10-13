using System.Text.Json;

namespace Teos.OpportunityNetwork.Matching;

/// <summary>How a single provider's judgment ended up in (or out of) the fused signal.</summary>
public enum ProviderOutcomeStatus
{
    Success,
    InvalidOutput,
    Failed
}

/// <summary>Audit record for one provider in one ensemble evaluation. Contains no secrets.</summary>
public sealed record ProviderOutcome(
    string Provider,
    ProviderOutcomeStatus Status,
    double? Relevance,
    string Detail,
    string[] Keywords,
    decimal? PayRangeMin = null,
    decimal? PayRangeMax = null,
    string? PayCurrency = null);

/// <summary>A named evaluator participating in the ensemble.</summary>
public sealed record SemanticProvider(string Name, ISemanticEvaluator Evaluator);

/// <summary>
/// Multi-agent semantic scoring: runs one evaluator per configured provider (each a free
/// OpenAI-compatible model), validates every raw output through <see cref="SemanticValidator"/>,
/// and fuses the valid judgments with a DETERMINISTIC median (even counts use the mean of the
/// two middle values). Provider order is normalized, so identical inputs always yield identical
/// output.
///
/// Resilient by design: a failing or schema-non-conforming provider is skipped and never fails
/// the pipeline. If every provider fails, it returns the documented neutral-50 signal with an
/// explicit "all providers failed" rationale — evidence is never invented.
///
/// The final output is a canonical JSON object, so the matcher's schema gate still applies.
/// Replay never re-runs this evaluator: the fused value is stored in the input snapshot.
/// </summary>
public sealed class EnsembleSemanticEvaluator : ISemanticEvaluator
{
    /// <summary>The documented neutral semantic signal used when no provider contributes.</summary>
    public const double NeutralRelevance = 50;

    private readonly SemanticProvider[] _providers;

    public EnsembleSemanticEvaluator(IEnumerable<SemanticProvider> providers)
    {
        var list = providers?.ToArray() ?? throw new ArgumentNullException(nameof(providers));
        if (list.Length == 0)
            throw new ArgumentException("at least one provider is required", nameof(providers));
        _providers = list.OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Outcomes of the most recent evaluation (provider order, no secrets).</summary>
    public IReadOnlyList<ProviderOutcome> Outcomes { get; private set; } = Array.Empty<ProviderOutcome>();

    public async Task<string> EvaluateAsync(string opportunityText, string profileText, CancellationToken ct = default)
    {
        var outcomes = await Task.WhenAll(_providers.Select(async p =>
        {
            try
            {
                var raw = await p.Evaluator.EvaluateAsync(opportunityText, profileText, ct).ConfigureAwait(false);
                var judgment = SemanticValidator.Validate(raw); // schema gate per provider
                return new ProviderOutcome(p.Name, ProviderOutcomeStatus.Success, judgment.Relevance, "ok",
                    judgment.MatchedKeywords, judgment.PayRangeMin, judgment.PayRangeMax, judgment.PayCurrency);
            }
            catch (SemanticValidationException)
            {
                return new ProviderOutcome(p.Name, ProviderOutcomeStatus.InvalidOutput, null, "invalid-output", Array.Empty<string>());
            }
            catch (SemanticProviderException ex)
            {
                return new ProviderOutcome(p.Name, ProviderOutcomeStatus.Failed, null, Truncate(ex.Message, 60), Array.Empty<string>());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ProviderOutcome(p.Name, ProviderOutcomeStatus.Failed, null, ex.GetType().Name, Array.Empty<string>());
            }
        })).ConfigureAwait(false);

        Outcomes = outcomes.OrderBy(o => o.Provider, StringComparer.Ordinal).ToArray();

        var valid = Outcomes
            .Where(o => o.Status == ProviderOutcomeStatus.Success && o.Relevance.HasValue)
            .Select(o => o.Relevance!.Value)
            .OrderBy(v => v)
            .ToArray();

        var fused = valid.Length == 0 ? NeutralRelevance : Median(valid);
        var keywords = Outcomes
            .Where(o => o.Status == ProviderOutcomeStatus.Success)
            .SelectMany(o => o.Keywords)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        var rationale = fused == NeutralRelevance && valid.Length == 0
            ? "all providers failed; neutral-50 by policy"
            : string.Join(", ", Outcomes.Select(o => o.Status == ProviderOutcomeStatus.Success
                ? $"{o.Provider}={o.Relevance!.Value:0.##}"
                : $"{o.Provider}={o.Status.ToString().ToLowerInvariant()}"));

        var (payMin, payMax, payCurrency) = FusePayRange();

        // Dictionary preserves insertion order; the shape is deterministic for identical inputs.
        var payload = new Dictionary<string, object?>
        {
            ["relevance"] = Math.Round(fused, 4),
            ["rationale"] = rationale,
            ["matched_keywords"] = keywords
        };
        if (payMin is not null || payMax is not null)
            payload["pay_range"] = new PayRangeDto(payMin, payMax, payCurrency);

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// Deterministic pay-range fusion across successful providers: the median of the provided
    /// mins and the median of the provided maxes (null when none provided); the currency is the
    /// most common among contributors (ties broken by ordinal name). Never invents a range.
    /// </summary>
    private (decimal? Min, decimal? Max, string? Currency) FusePayRange()
    {
        var contributors = Outcomes
            .Where(o => o.Status == ProviderOutcomeStatus.Success &&
                        (o.PayRangeMin is not null || o.PayRangeMax is not null))
            .ToArray();
        if (contributors.Length == 0)
            return (null, null, null);

        var mins = contributors.Where(o => o.PayRangeMin is not null)
            .Select(o => o.PayRangeMin!.Value).OrderBy(v => v).ToArray();
        var maxs = contributors.Where(o => o.PayRangeMax is not null)
            .Select(o => o.PayRangeMax!.Value).OrderBy(v => v).ToArray();

        var currency = contributors
            .Where(o => !string.IsNullOrWhiteSpace(o.PayCurrency))
            .GroupBy(o => o.PayCurrency!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.First().PayCurrency)
            .FirstOrDefault();

        return (mins.Length > 0 ? MedianDecimal(mins) : null,
                maxs.Length > 0 ? MedianDecimal(maxs) : null,
                currency);
    }

    private static decimal MedianDecimal(decimal[] sortedAscending) =>
        sortedAscending.Length % 2 == 1
            ? sortedAscending[sortedAscending.Length / 2]
            : (sortedAscending[sortedAscending.Length / 2 - 1] + sortedAscending[sortedAscending.Length / 2]) / 2m;

    private sealed record PayRangeDto(decimal? min, decimal? max, string? currency);

    /// <summary>Deterministic median; even counts use the mean of the two middle values.</summary>
    internal static double Median(double[] sortedAscending) =>
        sortedAscending.Length % 2 == 1
            ? sortedAscending[sortedAscending.Length / 2]
            : (sortedAscending[sortedAscending.Length / 2 - 1] + sortedAscending[sortedAscending.Length / 2]) / 2.0;

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? "";
        return text[..max] + "…";
    }
}