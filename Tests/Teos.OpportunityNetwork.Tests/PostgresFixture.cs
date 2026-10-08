using Npgsql;
using Xunit;

namespace Teos.OpportunityNetwork.Tests;

/// <summary>
/// Real PostgreSQL test database (authoritative per project rules — never SQLite).
/// Connection string comes from TEOS_TEST_DB; default targets the local Docker test container.
/// Each test class gets its own schema-isolated database created from template0.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string DefaultConnectionString =
        "Host=localhost;Port=54329;Username=teos;Password=teos_test;Database=postgres";

    public string AdminConnectionString { get; }
    public string DatabaseName { get; } = $"teos_test_{Guid.NewGuid():N}";
    public string ConnectionString { get; private set; } = "";

    private readonly List<string> _created = new();

    public PostgresFixture()
    {
        AdminConnectionString = Environment.GetEnvironmentVariable("TEOS_TEST_DB") ?? DefaultConnectionString;
    }

    public async Task InitializeAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString);
        var adminDb = builder.Database;
        builder.Database = "postgres";
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();

        var create = new NpgsqlCommand($"CREATE DATABASE \"{DatabaseName}\" TEMPLATE template0", admin);
        await create.ExecuteNonQueryAsync();
        _created.Add(DatabaseName);

        builder.Database = DatabaseName;
        ConnectionString = builder.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString);
            builder.Database = "postgres";
            await using var admin = new NpgsqlConnection(builder.ConnectionString);
            await admin.OpenAsync();
            await using (var kill = new NpgsqlCommand(
                $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{DatabaseName}' AND pid <> pg_backend_pid()",
                admin))
            {
                await kill.ExecuteNonQueryAsync();
            }

            await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{DatabaseName}\"", admin))
            {
                await drop.ExecuteNonQueryAsync();
            }
        }
        catch
        {
            // Best-effort cleanup; leftover DBs do not affect correctness.
        }
    }

    /// <summary>
    /// Resets the public schema and applies migrations/0001_init.sql. Called at the start of
    /// each test seed so tests stay isolated even though the fixture (and its database) is
    /// shared across the test collection.
    /// </summary>
    public async Task ApplySchemaAsync()
    {
        var sql = await File.ReadAllTextAsync(MigrationsPath());
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using (var reset = new NpgsqlCommand("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;", conn))
        {
            await reset.ExecuteNonQueryAsync();
        }

        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public static string MigrationsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "migrations", "0001_init.sql");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent!;
        }
        throw new FileNotFoundException("migrations/0001_init.sql not found above " + AppContext.BaseDirectory);
    }
}

[CollectionDefinition("postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture>
{
}
