using Npgsql;

namespace Teos.OpportunityNetwork.Runner;

/// <summary>
/// Applies migrations/0001_init.sql to a PostgreSQL database. The script is idempotent
/// (CREATE TABLE IF NOT EXISTS / CREATE EXTENSION IF NOT EXISTS), so re-running it on every
/// scheduled scan is safe and self-heals a partially provisioned database (e.g. a fresh Neon
/// project). Never logs the connection string.
/// </summary>
public static class MigrationRunner
{
    /// <summary>Applies the schema if needed and returns the public-schema table names (sorted).</summary>
    public static async Task<string[]> MigrateAsync(string databaseUrl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseUrl);

        var sql = await File.ReadAllTextAsync(MigrationsPath(), ct).ConfigureAwait(false);
        await using var conn = new NpgsqlConnection(databaseUrl);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var tables = new List<string>();
        await using (var cmd = new NpgsqlCommand(
                         "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' ORDER BY table_name;",
                         conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                tables.Add(reader.GetString(0));
        }
        return tables.ToArray();
    }

    /// <summary>Resolves migrations/0001_init.sql from the working directory or upward from the assembly.</summary>
    public static string MigrationsPath()
    {
        var cwd = Path.Combine(Directory.GetCurrentDirectory(), "migrations", "0001_init.sql");
        if (File.Exists(cwd)) return cwd;

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