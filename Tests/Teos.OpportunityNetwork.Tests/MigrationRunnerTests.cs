using Npgsql;
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

    [Fact]
    public async Task Migration_applies_every_up_script_and_adds_pay_range_columns()
    {
        // 0001 (create) + 0002 (pay range) must both be applied, in order.
        var files = MigrationRunner.MigrationFiles();
        Assert.Contains(files, f => Path.GetFileName(f) == "0001_init.sql");
        Assert.Contains(files, f => Path.GetFileName(f) == "0002_match_pay_range.sql");
        Assert.DoesNotContain(files, f => f.EndsWith(".down.sql", StringComparison.OrdinalIgnoreCase));

        await MigrationRunner.MigrateAsync(_db.ConnectionString);

        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_name = 'opportunity_matches'", conn);

        var columns = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));

        Assert.Contains("pay_range_min", columns);
        Assert.Contains("pay_range_max", columns);
        Assert.Contains("pay_currency", columns);
    }
}