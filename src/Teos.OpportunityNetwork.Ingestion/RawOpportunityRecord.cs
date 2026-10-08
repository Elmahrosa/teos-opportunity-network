namespace Teos.OpportunityNetwork.Ingestion;

/// <summary>Raw, un-normalized opportunity as scraped from a source.</summary>
public sealed class RawOpportunityRecord
{
    public string Source { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Company { get; set; }
    public string? Url { get; set; }
    public string? Description { get; set; }
    public string? Location { get; set; }
    /// <summary>Free-text budget, e.g. "$500", "$1,000 - $2,500 USD", "competitive".</summary>
    public string? BudgetText { get; set; }
    /// <summary>Raw type hint, e.g. "BOUNTY".</summary>
    public string? Type { get; set; }
    public string[]? Skills { get; set; }
    public string? PostedAt { get; set; }
    public string? Deadline { get; set; }
}
