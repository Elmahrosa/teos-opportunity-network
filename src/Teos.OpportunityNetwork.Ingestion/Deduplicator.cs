using Teos.OpportunityNetwork.Core.Domain;

namespace Teos.OpportunityNetwork.Ingestion;

/// <summary>Read-side store abstraction used by deduplication (implemented by Persistence).</summary>
public interface IOpportunityStore
{
    Task<Opportunity?> FindBySourceExternalAsync(string source, string externalId, CancellationToken ct = default);
    Task<Opportunity?> FindByContentHashAsync(string contentHash, CancellationToken ct = default);
}

public enum DedupVerdict
{
    New,
    /// <summary>Same (source, external_id) already stored — authoritative duplicate.</summary>
    SourceDuplicate,
    /// <summary>Same content hash from a different provenance — candidate only, never auto-merged.</summary>
    CrossSourceCandidate
}

public sealed class DedupResult
{
    public DedupVerdict Verdict { get; init; }
    public Guid? ExistingOpportunityId { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// Conservative deduplication:
///   1. (source, external_id) match  → SourceDuplicate (authoritative)
///   2. content-hash match           → CrossSourceCandidate (keep provenance, never auto-merge)
///   3. otherwise                    → New
/// Cross-source items are surfaced for review instead of being silently discarded.
/// </summary>
public sealed class Deduplicator
{
    private readonly IOpportunityStore _store;

    public Deduplicator(IOpportunityStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<DedupResult> EvaluateAsync(Opportunity candidate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var exact = await _store.FindBySourceExternalAsync(candidate.Source, candidate.ExternalId, ct)
            .ConfigureAwait(false);
        if (exact is not null)
        {
            return new DedupResult
            {
                Verdict = DedupVerdict.SourceDuplicate,
                ExistingOpportunityId = exact.Id,
                Reason = "authoritative duplicate: same source + external_id"
            };
        }

        var byHash = await _store.FindByContentHashAsync(candidate.ContentHash, ct).ConfigureAwait(false);
        if (byHash is not null)
        {
            return new DedupResult
            {
                Verdict = DedupVerdict.CrossSourceCandidate,
                ExistingOpportunityId = byHash.Id,
                Reason = "content hash matches an existing opportunity — cross-source candidate; provenance preserved"
            };
        }

        return new DedupResult { Verdict = DedupVerdict.New };
    }
}
