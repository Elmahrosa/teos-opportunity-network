using System.Text.Json;

namespace Teos.OpportunityNetwork.Core.Domain;

/// <summary>Thrown when a scoring configuration is internally inconsistent.</summary>
public sealed class ScoringConfigException : Exception
{
    public ScoringConfigException(string message) : base(message) { }
}

/// <summary>
/// Weighting configuration. HYBRID_V1 is authoritative for production scoring;
/// HYBRID_V2 is experimental and must be selected explicitly.
///
/// HYBRID_V1 weights (must total exactly 1.0):
///   skills .35, experience .20, budget .15, type .10, location .10, urgency .05, semantic .05
/// </summary>
public sealed class ScoringConfig
{
    public const string HybridV1 = "hybrid-v1";
    public const string HybridV2 = "hybrid-v2";
    public const string MatcherEngineV1 = "matcher-v1";

    private static readonly string[] RequiredComponents =
        { "skills", "experience", "budget", "type", "location", "urgency", "semantic" };

    public string WeightsVersion { get; set; } = HybridV1;
    public string EngineVersion { get; set; } = MatcherEngineV1;
    public Dictionary<string, double> Weights { get; set; } = new();

    public static ScoringConfig CreateHybridV1() => new()
    {
        WeightsVersion = HybridV1,
        EngineVersion = MatcherEngineV1,
        Weights = new Dictionary<string, double>
        {
            ["skills"] = 0.35,
            ["experience"] = 0.20,
            ["budget"] = 0.15,
            ["type"] = 0.10,
            ["location"] = 0.10,
            ["urgency"] = 0.05,
            ["semantic"] = 0.05
        }
    };

    public static ScoringConfig CreateHybridV2() => new()
    {
        WeightsVersion = HybridV2,
        EngineVersion = MatcherEngineV1,
        Weights = new Dictionary<string, double>
        {
            ["skills"] = 0.30,
            ["experience"] = 0.20,
            ["budget"] = 0.15,
            ["type"] = 0.10,
            ["location"] = 0.10,
            ["urgency"] = 0.05,
            ["semantic"] = 0.10
        }
    };

    /// <summary>
    /// Enforces: every known component present, no unknown components, non-negative,
    /// and totals exactly 1.0 (within 1e-9).
    /// </summary>
    public void Validate()
    {
        foreach (var key in RequiredComponents)
        {
            if (!Weights.ContainsKey(key))
                throw new ScoringConfigException($"missing weight '{key}' in {WeightsVersion}");
        }

        foreach (var (key, value) in Weights)
        {
            if (!RequiredComponents.Contains(key))
                throw new ScoringConfigException($"unknown weight '{key}' in {WeightsVersion}");
            if (value < 0)
                throw new ScoringConfigException($"negative weight '{key}' in {WeightsVersion}");
        }

        var total = Weights.Values.Sum();
        if (Math.Abs(total - 1.0) > 1e-9)
            throw new ScoringConfigException($"weights must total 1.0, got {total:0.######} for {WeightsVersion}");
    }

    public string ToJson() => JsonSerializer.Serialize(new ConfigDto
    {
        WeightsVersion = WeightsVersion,
        EngineVersion = EngineVersion,
        Weights = Weights
    });

    public static ScoringConfig FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ScoringConfigException("scoring config JSON is empty");

        ConfigDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ConfigDto>(json);
        }
        catch (JsonException ex)
        {
            throw new ScoringConfigException($"scoring config JSON is invalid: {ex.Message}");
        }

        if (dto is null)
            throw new ScoringConfigException("scoring config JSON deserialized to null");

        var config = new ScoringConfig
        {
            WeightsVersion = dto.WeightsVersion,
            EngineVersion = dto.EngineVersion,
            Weights = dto.Weights ?? new Dictionary<string, double>()
        };
        config.Validate();
        return config;
    }

    private sealed class ConfigDto
    {
        public string WeightsVersion { get; set; } = HybridV1;
        public string EngineVersion { get; set; } = MatcherEngineV1;
        public Dictionary<string, double>? Weights { get; set; }
    }
}
