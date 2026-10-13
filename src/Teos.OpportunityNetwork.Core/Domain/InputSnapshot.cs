using System.Text.Json;
using System.Text.Json.Serialization;

namespace Teos.OpportunityNetwork.Core.Domain;

/// <summary>
/// Immutable input evidence for a scoring decision. Stored append-only in the database;
/// replay reads this snapshot and NEVER re-reads live, mutable opportunity/profile state
/// and NEVER calls an LLM (the semantic score is stored here).
/// </summary>
public sealed class InputSnapshot
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public Guid SnapshotSourceId { get; set; } = Guid.NewGuid();
    public string WeightsVersion { get; set; } = ScoringConfig.HybridV1;
    /// <summary>Raw semantic relevance (0..100) used at scoring time; null when not evaluated.</summary>
    public double? SemanticScore { get; set; }
    /// <summary>LLM-estimated pay range used at scoring time (null when omitted); replayed verbatim.</summary>
    public decimal? PayRangeMin { get; set; }
    public decimal? PayRangeMax { get; set; }
    public string? PayCurrency { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public Opportunity Opportunity { get; set; } = new();
    public UserProfile Profile { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static InputSnapshot FromJson(string json)
    {
        var snapshot = JsonSerializer.Deserialize<InputSnapshot>(json, JsonOptions);
        if (snapshot is null)
            throw new InvalidOperationException("input snapshot JSON deserialized to null");
        return snapshot;
    }
}
