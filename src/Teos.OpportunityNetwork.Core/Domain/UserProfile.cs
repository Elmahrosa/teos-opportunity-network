namespace Teos.OpportunityNetwork.Core.Domain;

/// <summary>A member profile used for matching. Notifications can be paused globally.</summary>
public sealed class UserProfile
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string[] Skills { get; set; } = Array.Empty<string>();
    public int YearsExperience { get; set; }
    public string? Location { get; set; }
    public OpportunityType[] PreferredTypes { get; set; } = Array.Empty<OpportunityType>();
    public decimal? BudgetMin { get; set; }
    public decimal? BudgetMax { get; set; }
    public string? Currency { get; set; }
    public string[] Keywords { get; set; } = Array.Empty<string>();
    public bool NotificationsPaused { get; set; }
}
