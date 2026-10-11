namespace Teos.OpportunityNetwork.Core;

using System.Collections.Generic;

/// <summary>
/// Raw repository data as collected from a source (e.g., GitHub).
/// </summary>
public sealed class RawRepoData
{
    public string Source { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int Stars { get; set; }
    public int Forks { get; set; }
    public int OpenIssues { get; set; }
    public long SizeKb { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Language { get; set; }
    public Dictionary<string, int> Languages { get; set; } = new();
    public string? License { get; set; }
}