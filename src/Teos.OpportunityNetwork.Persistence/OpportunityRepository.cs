using Npgsql;
using Teos.OpportunityNetwork.Core.Domain;
using Teos.OpportunityNetwork.Ingestion;

namespace Teos.OpportunityNetwork.Persistence;

/// <summary>
/// Opportunities store: read side for deduplication (IOpportunityStore) plus the upsert and
/// open-opportunity queries the notification runtime needs. Persistence only — no
/// normalization, matching or scoring logic.
/// </summary>
public sealed class OpportunityRepository : IOpportunityStore
{
    private readonly string _conn;

    public OpportunityRepository(string connectionString) =>
        _conn = connectionString ?? throw new ArgumentNullException(nameof(connectionString));

    public async Task<Opportunity?> FindBySourceExternalAsync(string source, string externalId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, source, external_id, title, company, url, description, location,
                   opportunity_type, skills_required, budget_min, budget_max, currency, budget_type,
                   posted_at, deadline, content_hash, first_seen_at, last_seen_at, status
            FROM opportunities
            WHERE source = @s AND external_id = @e
            LIMIT 1", conn);
        cmd.Parameters.AddWithValue("@s", source);
        cmd.Parameters.AddWithValue("@e", externalId);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return Read(reader);
    }

    public async Task<Opportunity?> FindByContentHashAsync(string contentHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contentHash)) return null;

        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, source, external_id, title, company, url, description, location,
                   opportunity_type, skills_required, budget_min, budget_max, currency, budget_type,
                   posted_at, deadline, content_hash, first_seen_at, last_seen_at, status
            FROM opportunities
            WHERE content_hash = @h
            LIMIT 1", conn);
        cmd.Parameters.AddWithValue("@h", contentHash);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return Read(reader);
    }

    public async Task<IReadOnlyList<Opportunity>> GetOpenAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, source, external_id, title, company, url, description, location,
                   opportunity_type, skills_required, budget_min, budget_max, currency, budget_type,
                   posted_at, deadline, content_hash, first_seen_at, last_seen_at, status
            FROM opportunities
            WHERE status = 'OPEN'
            ORDER BY first_seen_at", conn);

        var list = new List<Opportunity>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) list.Add(Read(reader));
        return list;
    }

    /// <summary>Idempotent upsert keyed on (source, external_id).</summary>
    public async Task UpsertAsync(Opportunity opportunity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(opportunity);

        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO opportunities (
                id, source, external_id, title, company, url, description, location,
                opportunity_type, skills_required, budget_min, budget_max, currency, budget_type,
                posted_at, deadline, content_hash, first_seen_at, last_seen_at, status)
            VALUES (
                @id, @s, @e, @title, @company, @url, @description, @location,
                @type, @skills, @bmin, @bmax, @currency, @btype,
                @posted, @deadline, @hash, @first, @last, @status)
            ON CONFLICT (source, external_id) DO UPDATE SET
                title = EXCLUDED.title, company = EXCLUDED.company, url = EXCLUDED.url,
                description = EXCLUDED.description, location = EXCLUDED.location,
                opportunity_type = EXCLUDED.opportunity_type, skills_required = EXCLUDED.skills_required,
                budget_min = EXCLUDED.budget_min, budget_max = EXCLUDED.budget_max,
                currency = EXCLUDED.currency, budget_type = EXCLUDED.budget_type,
                deadline = EXCLUDED.deadline, content_hash = EXCLUDED.content_hash,
                last_seen_at = EXCLUDED.last_seen_at, status = EXCLUDED.status", conn);
        cmd.Parameters.AddWithValue("@id", opportunity.Id);
        cmd.Parameters.AddWithValue("@s", opportunity.Source);
        cmd.Parameters.AddWithValue("@e", opportunity.ExternalId);
        cmd.Parameters.AddWithValue("@title", opportunity.Title);
        cmd.Parameters.AddWithValue("@company", (object?)opportunity.Company ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@url", (object?)opportunity.Url ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@description", (object?)opportunity.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@location", (object?)opportunity.Location ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@type", opportunity.OpportunityType.ToString());
        cmd.Parameters.AddWithValue("@skills", opportunity.SkillsRequired ?? Array.Empty<string>());
        cmd.Parameters.AddWithValue("@bmin", (object?)opportunity.BudgetMin ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bmax", (object?)opportunity.BudgetMax ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@currency", (object?)opportunity.Currency ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@btype", opportunity.BudgetType ?? "unknown");
        cmd.Parameters.AddWithValue("@posted", (object?)opportunity.PostedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@deadline", (object?)opportunity.Deadline ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hash", opportunity.ContentHash);
        cmd.Parameters.AddWithValue("@first", opportunity.FirstSeenAt);
        cmd.Parameters.AddWithValue("@last", opportunity.LastSeenAt);
        cmd.Parameters.AddWithValue("@status", opportunity.Status);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static Opportunity Read(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        Source = reader.GetString(1),
        ExternalId = reader.GetString(2),
        Title = reader.GetString(3),
        Company = reader.IsDBNull(4) ? null : reader.GetString(4),
        Url = reader.IsDBNull(5) ? null : reader.GetString(5),
        Description = reader.IsDBNull(6) ? null : reader.GetString(6),
        Location = reader.IsDBNull(7) ? null : reader.GetString(7),
        OpportunityType = Enum.Parse<OpportunityType>(reader.GetString(8)),
        SkillsRequired = reader.GetFieldValue<string[]>(9),
        BudgetMin = reader.IsDBNull(10) ? null : reader.GetDecimal(10),
        BudgetMax = reader.IsDBNull(11) ? null : reader.GetDecimal(11),
        Currency = reader.IsDBNull(12) ? null : reader.GetString(12),
        BudgetType = reader.GetString(13),
        PostedAt = reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
        Deadline = reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15),
        ContentHash = reader.GetString(16),
        FirstSeenAt = reader.GetFieldValue<DateTimeOffset>(17),
        LastSeenAt = reader.GetFieldValue<DateTimeOffset>(18),
        Status = reader.GetString(19)
    };
}