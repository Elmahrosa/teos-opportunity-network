using Npgsql;

namespace Teos.OpportunityNetwork.Runner;

/// <summary>
/// Applies every migrations/*.sql (in lexical order, excluding *.down.sql) to a PostgreSQL
/// database. Each script is idempotent (CREATE TABLE IF NOT EXISTS / ALTER ... IF NOT EXISTS),
/// so re-running on every scheduled scan is safe and self-heals a partially provisioned
/// database (e.g. a fresh Neon project). Never logs the connection string.
/// </summary>
public static class MigrationRunner
{
    /// <summary>Applies the schema if needed and returns the public-schema table names (sorted).</summary>
    public static async Task<string[]> MigrateAsync(string databaseUrl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseUrl);

        var files = MigrationFiles();
        await using var conn = new NpgsqlConnection(databaseUrl);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        foreach (var file in files)
        {
            var sql = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(sql, conn);
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

    /// <summary>
    /// All up-migration files (migrations/*.sql, excluding *.down.sql) in lexical order, resolved
    /// from the working directory or upward from the assembly.
    /// </summary>
    public static IReadOnlyList<string> MigrationFiles()
    {
        var dir = MigrationsDirectory();
        return Directory.GetFiles(dir, "*.sql", SearchOption.TopDirectoryOnly)
            .Where(f => !f.EndsWith(".down.sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Resolves the migrations/ directory from the working directory or upward from the assembly.</summary>
    public static string MigrationsDirectory()
    {
        var cwd = Path.Combine(Directory.GetCurrentDirectory(), "migrations");
        if (Directory.Exists(cwd)) return cwd;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "migrations");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent!;
        }
        throw new DirectoryNotFoundException("migrations/ not found above " + AppContext.BaseDirectory);
    }
}