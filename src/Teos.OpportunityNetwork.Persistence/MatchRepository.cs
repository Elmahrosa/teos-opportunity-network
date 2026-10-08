using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Teos.OpportunityNetwork.Core.Domain;

namespace Teos.OpportunityNetwork.Persistence;

/// <summary>
/// Step 5/6 — MatchRepository is the authoritative reader/writer for match persistence.
/// Contains NO Telegram calls, NO notification formatting, NO scoring, NO matching.
/// </summary>
public sealed class MatchRepository
{
    private readonly string _conn;

    public MatchRepository(string connectionString) =>
        _conn = connectionString ?? throw new ArgumentNullException(nameof(connectionString));

    /// <summary>
    /// Idempotent save keyed by (opportunity_id, user_id, weights_version, engine_version).
    /// Re-saving the same version pair returns the existing record without overwriting
    /// historical evidence. A new scoring version creates a new historical record.
    /// </summary>
    public async Task<MatchResult> SaveAsync(MatchResult match, InputSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(snapshot);

        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var existing = await FindVersionAsync(conn, tx, match.OpportunityId, match.UserId,
            match.WeightsVersion, match.EngineVersion, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return existing; // idempotent: never overwrite historical evidence
        }

        // Snapshot must already exist and be immutable; insert if this is a first-time save.
        await EnsureSnapshotAsync(conn, tx, snapshot, match.OpportunityId, match.UserId, ct).ConfigureAwait(false);

        await using var insert = new NpgsqlCommand(@"
            INSERT INTO opportunity_matches (
                id, opportunity_id, user_id, match_score, decision, recommended_action,
                skill_match, experience_match, budget_match, location_match, urgency_score,
                semantic_score, confidence, matched_skills, missing_skills, reasons, warnings,
                scoring_config, weights_version, engine_version, snapshot_source_id, created_at)
            VALUES (
                @id, @opportunity_id, @user_id, @match_score, @decision, @recommended_action,
                @skill_match, @experience_match, @budget_match, @location_match, @urgency_score,
                @semantic_score, @confidence, @matched_skills, @missing_skills, @reasons, @warnings,
                @scoring_config, @weights_version, @engine_version, @snapshot_source_id, @created_at)
            ON CONFLICT (opportunity_id, user_id, weights_version, engine_version) DO NOTHING",
            conn, tx);

        AddMatchParams(insert, match);
        var affected = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (affected == 0)
        {
            // Lost a race — return the winner.
            var winner = await FindVersionAsync(conn, tx, match.OpportunityId, match.UserId,
                match.WeightsVersion, match.EngineVersion, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return winner ?? match;
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return match;
    }

    /// <summary>Semantics preserved: has a successful delivery been recorded for this match/user/channel?</summary>
    public async Task<bool> AlreadyAlertedAsync(Guid matchId, Guid userId, string channel = "telegram", CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(@"
            SELECT EXISTS (
                SELECT 1 FROM notification_deliveries
                WHERE match_id = @match_id AND user_id = @user_id AND channel = @channel AND status = 'SENT')",
            conn);
        cmd.Parameters.AddWithValue("@match_id", matchId);
        cmd.Parameters.AddWithValue("@user_id", userId);
        cmd.Parameters.AddWithValue("@channel", channel);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is true;
    }

    /// <summary>
    /// Read-only feed of ALERT matches that still need delivery.
    /// Excludes matches with a SENT delivery (anti-join) and paused users.
    /// Performs NO writes and NO Telegram calls.
    /// </summary>
    public async Task<IReadOnlyList<PendingAlert>> PendingAlertsAsync(string channel = "telegram", CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(@"
            SELECT m.id, m.opportunity_id, m.user_id, m.match_score, m.decision, m.recommended_action,
                   m.matched_skills, m.missing_skills, m.reasons, m.semantic_score, m.confidence,
                   m.weights_version, m.engine_version, m.scoring_config, m.snapshot_source_id, m.created_at,
                   o.title, o.company, o.url, o.opportunity_type, o.budget_min, o.budget_max, o.currency,
                   o.deadline, o.source, o.skills_required
            FROM opportunity_matches m
            JOIN opportunities o ON o.id = m.opportunity_id
            JOIN user_profiles p ON p.id = m.user_id
            WHERE m.decision = 'ALERT'
              AND p.notifications_paused = FALSE
              AND NOT EXISTS (
                  SELECT 1 FROM notification_deliveries nd
                  WHERE nd.match_id = m.id AND nd.user_id = m.user_id
                    AND nd.channel = @channel AND nd.status = 'SENT')
            ORDER BY m.created_at", conn);
        cmd.Parameters.AddWithValue("@channel", channel);

        var list = new List<PendingAlert>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new PendingAlert
            {
                MatchId = reader.GetGuid(0),
                OpportunityId = reader.GetGuid(1),
                UserId = reader.GetGuid(2),
                MatchScore = reader.GetDouble(3),
                Decision = reader.GetString(4),
                RecommendedAction = reader.GetString(5),
                MatchedSkills = reader.GetFieldValue<string[]>(6),
                MissingSkills = reader.GetFieldValue<string[]>(7),
                Reasons = JsonSerializer.Deserialize<string[]>(reader.GetFieldValue<string>(8)) ?? Array.Empty<string>(),
                SemanticScore = reader.GetDouble(9),
                Confidence = reader.GetDouble(10),
                WeightsVersion = reader.GetString(11),
                EngineVersion = reader.GetString(12),
                ScoringConfigJson = reader.GetFieldValue<string>(13),
                SnapshotSourceId = reader.GetGuid(14),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(15),
                Title = reader.GetString(16),
                Company = reader.IsDBNull(17) ? null : reader.GetString(17),
                Url = reader.IsDBNull(18) ? null : reader.GetString(18),
                OpportunityType = reader.GetString(19),
                BudgetMin = reader.IsDBNull(20) ? null : reader.GetDecimal(20),
                BudgetMax = reader.IsDBNull(21) ? null : reader.GetDecimal(21),
                Currency = reader.IsDBNull(22) ? null : reader.GetString(22),
                Deadline = reader.IsDBNull(23) ? null : reader.GetFieldValue<DateTimeOffset>(23),
                Source = reader.GetString(24),
                SkillsRequired = reader.GetFieldValue<string[]>(25)
            });
        }
        return list;
    }

    /// <summary>Benchmark baseline rows — every row exposes its immutable snapshot reference.</summary>
    public async Task<IReadOnlyList<BaselineRow>> HistoryForBenchmarkAsync(string? weightsVersion = null, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, opportunity_id, user_id, match_score, decision, weights_version, engine_version,
                   scoring_config, snapshot_source_id, created_at
            FROM opportunity_matches
            WHERE (@weights_version IS NULL OR weights_version = @weights_version)
            ORDER BY created_at", conn);
        cmd.Parameters.AddWithValue("@weights_version", NpgsqlDbType.Text, (object?)weightsVersion ?? DBNull.Value);

        var rows = new List<BaselineRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new BaselineRow
            {
                Id = reader.GetGuid(0),
                OpportunityId = reader.GetGuid(1),
                UserId = reader.GetGuid(2),
                MatchScore = reader.GetDouble(3),
                Decision = reader.GetString(4),
                WeightsVersion = reader.GetString(5),
                EngineVersion = reader.GetString(6),
                ScoringConfigJson = reader.GetFieldValue<string>(7),
                SnapshotSourceId = reader.GetGuid(8),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(9)
            });
        }
        return rows;
    }

    /// <summary>
    /// Persists a NEW version of a match for an existing baseline row, bound to that row's
    /// ORIGINAL immutable snapshot. Never reconstructs a snapshot from current mutable data.
    /// Returns the existing new-version record when the version pair already exists.
    /// </summary>
    public async Task<MatchResult> SaveNewVersionAsync(Guid baselineRowId, ScoringConfig newConfig, Func<InputSnapshot, MatchResult> rescore, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var baseline = await GetBaselineAsync(conn, baselineRowId, ct).ConfigureAwait(false);
        var snapshot = await GetSnapshotAsync(conn, baseline.SnapshotSourceId, ct).ConfigureAwait(false);

        // Replay from the FROZEN snapshot with the new config (caller supplies pure scoring).
        var newMatch = rescore(snapshot);
        newMatch.OpportunityId = baseline.OpportunityId;
        newMatch.UserId = baseline.UserId;
        newMatch.WeightsVersion = newConfig.WeightsVersion;
        newMatch.EngineVersion = newConfig.EngineVersion;
        newMatch.ScoringConfig = newConfig.ToJson();
        newMatch.SnapshotSourceId = snapshot.SnapshotSourceId;

        return await SaveAsync(newMatch, snapshot, ct).ConfigureAwait(false);
    }

    /// <summary>Score distribution over a weights version (benchmark support).</summary>
    public async Task<ScoreDistribution> ScoreDistributionAsync(string? weightsVersion = null, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(@"
            SELECT count(*), coalesce(min(match_score),0), coalesce(max(match_score),0),
                   coalesce(avg(match_score),0), coalesce(stddev_samp(match_score),0)
            FROM opportunity_matches
            WHERE (@weights_version IS NULL OR weights_version = @weights_version)", conn);
        cmd.Parameters.AddWithValue("@weights_version", NpgsqlDbType.Text, (object?)weightsVersion ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return new ScoreDistribution
        {
            Count = reader.GetInt64(0),
            Min = reader.GetDouble(1),
            Max = reader.GetDouble(2),
            Mean = reader.GetDouble(3),
            StdDev = reader.GetDouble(4)
        };
    }

    // ---- helpers -----------------------------------------------------------

    private static void AddMatchParams(NpgsqlCommand cmd, MatchResult m)
    {
        cmd.Parameters.AddWithValue("@id", m.Id);
        cmd.Parameters.AddWithValue("@opportunity_id", m.OpportunityId);
        cmd.Parameters.AddWithValue("@user_id", m.UserId);
        cmd.Parameters.AddWithValue("@match_score", m.MatchScore);
        cmd.Parameters.AddWithValue("@decision", m.Decision.ToString());
        cmd.Parameters.AddWithValue("@recommended_action", m.RecommendedAction.ToString());
        cmd.Parameters.AddWithValue("@skill_match", m.SkillMatch);
        cmd.Parameters.AddWithValue("@experience_match", m.ExperienceMatch);
        cmd.Parameters.AddWithValue("@budget_match", m.BudgetMatch);
        cmd.Parameters.AddWithValue("@location_match", m.LocationMatch);
        cmd.Parameters.AddWithValue("@urgency_score", m.UrgencyScore);
        cmd.Parameters.AddWithValue("@semantic_score", m.SemanticScore);
        cmd.Parameters.AddWithValue("@confidence", m.Confidence);
        cmd.Parameters.AddWithValue("@matched_skills", m.MatchedSkills);
        cmd.Parameters.AddWithValue("@missing_skills", m.MissingSkills);
        cmd.Parameters.AddWithValue("@reasons", JsonSerializer.Serialize(m.Reasons));
        cmd.Parameters.AddWithValue("@warnings", JsonSerializer.Serialize(m.Warnings));
        cmd.Parameters.AddWithValue("@scoring_config", m.ScoringConfig);
        cmd.Parameters.AddWithValue("@weights_version", m.WeightsVersion);
        cmd.Parameters.AddWithValue("@engine_version", m.EngineVersion);
        cmd.Parameters.AddWithValue("@snapshot_source_id", m.SnapshotSourceId);
        cmd.Parameters.AddWithValue("@created_at", m.CreatedAt);
    }

    private static async Task<MatchResult?> FindVersionAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        Guid opportunityId, Guid userId, string weightsVersion, string engineVersion, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, opportunity_id, user_id, match_score, decision, recommended_action,
                   skill_match, experience_match, budget_match, location_match, urgency_score,
                   semantic_score, confidence, matched_skills, missing_skills, reasons, warnings,
                   scoring_config, weights_version, engine_version, snapshot_source_id, created_at
            FROM opportunity_matches
            WHERE opportunity_id = @o AND user_id = @u
              AND weights_version = @w AND engine_version = @e", conn, tx);
        cmd.Parameters.AddWithValue("@o", opportunityId);
        cmd.Parameters.AddWithValue("@u", userId);
        cmd.Parameters.AddWithValue("@w", weightsVersion);
        cmd.Parameters.AddWithValue("@e", engineVersion);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return ReadMatch(reader);
    }

    private static MatchResult ReadMatch(NpgsqlDataReader reader)
    {
        var scoringJson = reader.GetFieldValue<string>(17);
        return new MatchResult
        {
            Id = reader.GetGuid(0),
            OpportunityId = reader.GetGuid(1),
            UserId = reader.GetGuid(2),
            MatchScore = reader.GetDouble(3),
            Decision = Enum.Parse<MatchDecision>(reader.GetString(4)),
            RecommendedAction = Enum.Parse<RecommendedAction>(reader.GetString(5)),
            SkillMatch = reader.GetDouble(6),
            ExperienceMatch = reader.GetDouble(7),
            BudgetMatch = reader.GetDouble(8),
            LocationMatch = reader.GetDouble(9),
            UrgencyScore = reader.GetDouble(10),
            SemanticScore = reader.GetDouble(11),
            Confidence = reader.GetDouble(12),
            MatchedSkills = reader.GetFieldValue<string[]>(13),
            MissingSkills = reader.GetFieldValue<string[]>(14),
            Reasons = JsonSerializer.Deserialize<string[]>(reader.GetFieldValue<string>(15)) ?? Array.Empty<string>(),
            Warnings = JsonSerializer.Deserialize<string[]>(reader.GetFieldValue<string>(16)) ?? Array.Empty<string>(),
            ScoringConfig = scoringJson,
            WeightsVersion = reader.GetString(18),
            EngineVersion = reader.GetString(19),
            SnapshotSourceId = reader.GetGuid(20),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(21)
        };
    }

    private static async Task EnsureSnapshotAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        InputSnapshot snapshot, Guid opportunityId, Guid userId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO input_snapshots (snapshot_source_id, opportunity_id, user_id, payload, weights_version, created_at)
            VALUES (@id, @o, @u, @payload::jsonb, @w, @created_at)
            ON CONFLICT (snapshot_source_id) DO NOTHING", conn, tx);
        cmd.Parameters.AddWithValue("@id", snapshot.SnapshotSourceId);
        cmd.Parameters.AddWithValue("@o", opportunityId);
        cmd.Parameters.AddWithValue("@u", userId);
        cmd.Parameters.AddWithValue("@payload", snapshot.ToJson());
        cmd.Parameters.AddWithValue("@w", snapshot.WeightsVersion);
        cmd.Parameters.AddWithValue("@created_at", snapshot.CapturedAt);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<BaselineRow> GetBaselineAsync(NpgsqlConnection conn, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, opportunity_id, user_id, match_score, decision, weights_version, engine_version,
                   scoring_config, snapshot_source_id, created_at
            FROM opportunity_matches WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException($"Baseline row {id} not found.");
        return new BaselineRow
        {
            Id = reader.GetGuid(0),
            OpportunityId = reader.GetGuid(1),
            UserId = reader.GetGuid(2),
            MatchScore = reader.GetDouble(3),
            Decision = reader.GetString(4),
            WeightsVersion = reader.GetString(5),
            EngineVersion = reader.GetString(6),
            ScoringConfigJson = reader.GetFieldValue<string>(7),
            SnapshotSourceId = reader.GetGuid(8),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(9)
        };
    }

    internal async Task<InputSnapshot> GetSnapshotAsync(NpgsqlConnection conn, Guid snapshotId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT payload::text FROM input_snapshots WHERE snapshot_source_id = @id", conn);
        cmd.Parameters.AddWithValue("@id", snapshotId);
        var json = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string
                   ?? throw new InvalidOperationException($"Snapshot {snapshotId} not found.");
        return InputSnapshot.FromJson(json);
    }
}

public sealed class PendingAlert
{
    public Guid MatchId { get; set; }
    public Guid OpportunityId { get; set; }
    public Guid UserId { get; set; }
    public double MatchScore { get; set; }
    public string Decision { get; set; } = "";
    public string RecommendedAction { get; set; } = "";
    public string[] MatchedSkills { get; set; } = Array.Empty<string>();
    public string[] MissingSkills { get; set; } = Array.Empty<string>();
    public string[] Reasons { get; set; } = Array.Empty<string>();
    public double SemanticScore { get; set; }
    public double Confidence { get; set; }
    public string WeightsVersion { get; set; } = "";
    public string EngineVersion { get; set; } = "";
    public string ScoringConfigJson { get; set; } = "";
    public Guid SnapshotSourceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    // opportunity join fields (persisted data only)
    public string Title { get; set; } = "";
    public string? Company { get; set; }
    public string? Url { get; set; }
    public string OpportunityType { get; set; } = "";
    public decimal? BudgetMin { get; set; }
    public decimal? BudgetMax { get; set; }
    public string? Currency { get; set; }
    public DateTimeOffset? Deadline { get; set; }
    public string Source { get; set; } = "";
    public string[] SkillsRequired { get; set; } = Array.Empty<string>();
}

public sealed class BaselineRow
{
    public Guid Id { get; set; }
    public Guid OpportunityId { get; set; }
    public Guid UserId { get; set; }
    public double MatchScore { get; set; }
    public string Decision { get; set; } = "";
    public string WeightsVersion { get; set; } = "";
    public string EngineVersion { get; set; } = "";
    public string ScoringConfigJson { get; set; } = "";
    public Guid SnapshotSourceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ScoreDistribution
{
    public long Count { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public double Mean { get; set; }
    public double StdDev { get; set; }
}
