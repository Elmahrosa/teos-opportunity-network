using Npgsql;
using Teos.OpportunityNetwork.Core.Domain;
using Teos.OpportunityNetwork.Matching;
using Teos.OpportunityNetwork.Persistence;
using Xunit;

namespace Teos.OpportunityNetwork.Tests;

[Collection("postgres")]
public class PersistenceTests
{
    private readonly PostgresFixture _db;

    public PersistenceTests(PostgresFixture db) => _db = db;

    private async Task<(Guid oppId, Guid userId)> SeedAsync()
    {
        await _db.ApplySchemaAsync();
        await using var conn = new Npgsql.NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();

        var oppId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using (var cmd = new Npgsql.NpgsqlCommand(@"
            INSERT INTO opportunities (id, source, external_id, title, company, url, budget_min, budget_max,
                currency, budget_type, opportunity_type, skills_required, posted_at, deadline, content_hash,
                first_seen_at, last_seen_at, status)
            VALUES (@id, 'github', 'ext-1', 'Build a REST API', 'acme/demo', 'https://example.com/1',
                500, 1000, 'USD', 'range', 'BOUNTY', ARRAY['c#','postgresql'], now(), now() + interval '10 days',
                @hash, now(), now(), 'OPEN')", conn))
        {
            cmd.Parameters.AddWithValue("@id", oppId);
            cmd.Parameters.AddWithValue("@hash", new string('a', 64));
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new Npgsql.NpgsqlCommand(@"
            INSERT INTO user_profiles (id, display_name, skills, years_experience, location, preferred_types,
                budget_min, budget_max, currency, keywords, notifications_paused)
            VALUES (@id, 'Test', ARRAY['c#','postgresql'], 6, 'Remote', ARRAY['BOUNTY','PROJECT'],
                100, 5000, 'USD', ARRAY['api'], FALSE)", conn))
        {
            cmd.Parameters.AddWithValue("@id", userId);
            await cmd.ExecuteNonQueryAsync();
        }

        return (oppId, userId);
    }

    private static (MatchResult match, InputSnapshot snapshot) Build(
        Guid oppId, Guid userId, ScoringConfig? config = null)
    {
        config ??= ScoringConfig.CreateHybridV1();
        var opp = new Opportunity
        {
            Id = oppId, Source = "github", ExternalId = "ext-1", Title = "Build a REST API",
            OpportunityType = OpportunityType.BOUNTY, SkillsRequired = new[] { "c#", "postgresql" },
            BudgetMin = 500, BudgetMax = 1000, Currency = "USD", BudgetType = "range",
            Location = "Remote", Status = "OPEN", ContentHash = new string('a', 64),
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow
        };
        var profile = new UserProfile
        {
            Id = userId, DisplayName = "Test", Skills = new[] { "c#", "postgresql" },
            YearsExperience = 6, Location = "Remote",
            PreferredTypes = new[] { OpportunityType.BOUNTY, OpportunityType.PROJECT },
            BudgetMin = 100, BudgetMax = 5000, Currency = "USD", Keywords = new[] { "api" }
        };
        var snapshot = SnapshotFactory.Capture(opp, profile, 80, config.WeightsVersion);
        var matcher = new HybridMatcher(config);
        return (matcher.Score(opp, profile, snapshot, 80, 0.9), snapshot);
    }

    [Fact]
    public async Task MatchResult_saves_and_loads()
    {
        var (oppId, userId) = await SeedAsync();
        var repo = new MatchRepository(_db.ConnectionString);
        var (match, snapshot) = Build(oppId, userId);

        var saved = await repo.SaveAsync(match, snapshot);

        Assert.Equal(match.Id, saved.Id);
        Assert.Equal(match.MatchScore, saved.MatchScore, 9);
        Assert.Equal(match.WeightsVersion, saved.WeightsVersion);

        var history = await repo.HistoryForBenchmarkAsync();
        Assert.Single(history);
        Assert.Equal(snapshot.SnapshotSourceId, history[0].SnapshotSourceId);
    }

    [Fact]
    public async Task Duplicate_version_save_is_idempotent_and_does_not_overwrite_evidence()
    {
        var (oppId, userId) = await SeedAsync();
        var repo = new MatchRepository(_db.ConnectionString);
        var (match, snapshot) = Build(oppId, userId);

        var first = await repo.SaveAsync(match, snapshot);

        // Same version pair, but with tampered score — must NOT overwrite the historical record.
        var tampered = Build(oppId, userId).match;
        tampered.MatchScore = 12.34;
        tampered.Id = first.Id;
        var second = await repo.SaveAsync(tampered, snapshot);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.MatchScore, second.MatchScore, 9);

        var history = await repo.HistoryForBenchmarkAsync();
        Assert.Single(history);
        Assert.Equal(first.MatchScore, history[0].MatchScore, 9);
    }

    [Fact]
    public async Task HybridV2_creates_a_separate_historical_record_and_V1_stays_unchanged()
    {
        var (oppId, userId) = await SeedAsync();
        var repo = new MatchRepository(_db.ConnectionString);

        var (v1, snap1) = Build(oppId, userId, ScoringConfig.CreateHybridV1());
        var savedV1 = await repo.SaveAsync(v1, snap1);
        var v1Score = savedV1.MatchScore;

        var (v2, snap2) = Build(oppId, userId, ScoringConfig.CreateHybridV2());
        var savedV2 = await repo.SaveAsync(v2, snap2);

        Assert.NotEqual(savedV1.Id, savedV2.Id);
        Assert.Equal(ScoringConfig.HybridV1, savedV1.WeightsVersion);
        Assert.Equal(ScoringConfig.HybridV2, savedV2.WeightsVersion);

        var v1Rows = await repo.HistoryForBenchmarkAsync(ScoringConfig.HybridV1);
        var v2Rows = await repo.HistoryForBenchmarkAsync(ScoringConfig.HybridV2);
        Assert.Single(v1Rows);
        Assert.Single(v2Rows);
        Assert.Equal(v1Score, v1Rows[0].MatchScore, 9); // historical V1 unchanged

        var dist = await repo.ScoreDistributionAsync();
        Assert.Equal(2, dist.Count);
    }

    [Fact]
    public async Task SaveNewVersion_binds_to_original_snapshot()
    {
        var (oppId, userId) = await SeedAsync();
        var repo = new MatchRepository(_db.ConnectionString);
        var (baseline, snapshot) = Build(oppId, userId);
        var saved = await repo.SaveAsync(baseline, snapshot);

        var v2 = await repo.SaveNewVersionAsync(saved.Id, ScoringConfig.CreateHybridV2(), snap =>
        {
            var matcher = new HybridMatcher(ScoringConfig.CreateHybridV2());
            return matcher.Score(snap.Opportunity, snap.Profile, snap, snap.SemanticScore ?? 50, 1.0);
        });

        Assert.Equal(ScoringConfig.HybridV2, v2.WeightsVersion);
        Assert.Equal(snapshot.SnapshotSourceId, v2.SnapshotSourceId); // original snapshot, not a reconstruction

        var v1Rows = await repo.HistoryForBenchmarkAsync(ScoringConfig.HybridV1);
        var v2Rows = await repo.HistoryForBenchmarkAsync(ScoringConfig.HybridV2);
        Assert.Single(v1Rows);
        Assert.Single(v2Rows);
        Assert.Equal(snapshot.SnapshotSourceId, v2Rows[0].SnapshotSourceId);
    }

    [Fact]
    public async Task Snapshot_is_immutable_in_the_database()
    {
        var (oppId, userId) = await SeedAsync();
        var repo = new MatchRepository(_db.ConnectionString);
        var (match, snapshot) = Build(oppId, userId);
        await repo.SaveAsync(match, snapshot);

        await using var conn = new Npgsql.NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "UPDATE input_snapshots SET payload = '{}'::jsonb WHERE snapshot_source_id = @id", conn);
        cmd.Parameters.AddWithValue("@id", snapshot.SnapshotSourceId);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Contains("append-only", ex.MessageText);
    }
}
