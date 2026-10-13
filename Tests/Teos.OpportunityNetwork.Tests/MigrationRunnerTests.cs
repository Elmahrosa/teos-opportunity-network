using Teos.OpportunityNetwork.Runner;
using Xunit;

namespace Teos.OpportunityNetwork.Tests;

/// <summary>
/// Real-Postgres proof of the --migrate mode (the scanner bootstraps a fresh Neon/Supabase
/// database with it). The migration script is idempotent, so applying it twice must succeed
/// and report the same core tables.
/// </summary>
[Collection("postgres")]
public class MigrationRunnerTests
{
    private static readonly string[] CoreTables =
    {
        "opportunities",
        "user_profiles",
        "input_snapshots",
        "opportunity_matches",
        "notification_deliveries",
        "telegram_chats"
    };

    private readonly PostgresFixture _db;

    public MigrationRunnerTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task Applies_schema_idempotently_and_reports_core_tables()
    {
        // Baseline: fixture guarantees a clean public schema before the first pass.
        var first = await MigrationRunner.MigrateAsync(_db.ConnectionString);
        var second = await MigrationRunner.MigrateAsync(_db.ConnectionString); // idempotent re-run

        Assert.All(CoreTables, table => Assert.Contains(table, first));
        Assert.All(CoreTables, table => Assert.Contains(table, second));
        Assert.Equal(first.Length, second.Length);
    }
}