using Npgsql;
using Teos.OpportunityNetwork.Core.Domain;

namespace Teos.OpportunityNetwork.Persistence;

/// <summary>User profiles store — read + seed. Persistence only.</summary>
public sealed class UserProfileRepository
{
    private readonly string _conn;

    public UserProfileRepository(string connectionString) =>
        _conn = connectionString ?? throw new ArgumentNullException(nameof(connectionString));

    public async Task<IReadOnlyList<UserProfile>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, display_name, skills, years_experience, location, preferred_types,
                   budget_min, budget_max, currency, keywords, notifications_paused
            FROM user_profiles
            ORDER BY created_at", conn);

        var list = new List<UserProfile>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new UserProfile
            {
                Id = reader.GetGuid(0),
                DisplayName = reader.GetString(1),
                Skills = reader.GetFieldValue<string[]>(2),
                YearsExperience = reader.GetInt32(3),
                Location = reader.IsDBNull(4) ? null : reader.GetString(4),
                PreferredTypes = ReadTypes(reader, 5),
                BudgetMin = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                BudgetMax = reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                Currency = reader.IsDBNull(8) ? null : reader.GetString(8),
                Keywords = reader.GetFieldValue<string[]>(9),
                NotificationsPaused = reader.GetBoolean(10)
            });
        }
        return list;
    }

    /// <summary>Idempotent upsert (used for seeding / dev).</summary>
    public async Task UpsertAsync(UserProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO user_profiles (
                id, display_name, skills, years_experience, location, preferred_types,
                budget_min, budget_max, currency, keywords, notifications_paused, updated_at)
            VALUES (
                @id, @name, @skills, @years, @location, @types,
                @bmin, @bmax, @currency, @keywords, @paused, now())
            ON CONFLICT (id) DO UPDATE SET
                display_name = EXCLUDED.display_name, skills = EXCLUDED.skills,
                years_experience = EXCLUDED.years_experience, location = EXCLUDED.location,
                preferred_types = EXCLUDED.preferred_types, budget_min = EXCLUDED.budget_min,
                budget_max = EXCLUDED.budget_max, currency = EXCLUDED.currency,
                keywords = EXCLUDED.keywords, notifications_paused = EXCLUDED.notifications_paused,
                updated_at = now()", conn);
        cmd.Parameters.AddWithValue("@id", profile.Id);
        cmd.Parameters.AddWithValue("@name", profile.DisplayName);
        cmd.Parameters.AddWithValue("@skills", profile.Skills ?? Array.Empty<string>());
        cmd.Parameters.AddWithValue("@years", profile.YearsExperience);
        cmd.Parameters.AddWithValue("@location", (object?)profile.Location ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@types", (profile.PreferredTypes ?? Array.Empty<OpportunityType>())
            .Select(t => t.ToString()).ToArray());
        cmd.Parameters.AddWithValue("@bmin", (object?)profile.BudgetMin ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bmax", (object?)profile.BudgetMax ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@currency", (object?)profile.Currency ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@keywords", profile.Keywords ?? Array.Empty<string>());
        cmd.Parameters.AddWithValue("@paused", profile.NotificationsPaused);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static OpportunityType[] ReadTypes(NpgsqlDataReader reader, int ordinal)
    {
        var values = reader.GetFieldValue<string[]>(ordinal);
        if (values.Length == 0) return Array.Empty<OpportunityType>();
        return values
            .Select(v => Enum.TryParse<OpportunityType>(v, ignoreCase: true, out var t) ? t : OpportunityType.PROJECT)
            .ToArray();
    }
}