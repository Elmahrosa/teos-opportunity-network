namespace Teos.OpportunityNetwork.Core.Domain;

/// <summary>A normalized opportunity from any source (bounty, job, project, …).</summary>
public sealed class Opportunity
{
    public Guid Id { get; set; }
    public string Source { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Company { get; set; }
    public string? Url { get; set; }
    public string? Description { get; set; }
    public string? Location { get; set; }
    public OpportunityType OpportunityType { get; set; }
    public string[] SkillsRequired { get; set; } = Array.Empty<string>();
    public decimal? BudgetMin { get; set; }
    public decimal? BudgetMax { get; set; }
    public string? Currency { get; set; }
    /// <summary>"fixed" | "range" | "unknown" — unknown budgets are valid and never penalized.</summary>
    public string? BudgetType { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public DateTimeOffset? Deadline { get; set; }
    public string Status { get; set; } = "OPEN";
    /// <summary>SHA-256 (64 hex chars) over normalized title|company|url|type.</summary>
    public string ContentHash { get; set; } = "";
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
