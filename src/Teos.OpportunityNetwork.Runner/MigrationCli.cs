using Microsoft.Extensions.Configuration;

namespace Teos.OpportunityNetwork.Runner;

/// <summary>Console entry for the --migrate mode: idempotently applies the schema to DATABASE_URL.</summary>
public static class MigrationCli
{
    public static async Task<int> RunMigrateAsync(CancellationToken ct = default)
    {
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(EnvFileLoader.Load(".env"))
                .AddEnvironmentVariables()
                .Build();

            var databaseUrl = configuration["DATABASE_URL"];
            if (string.IsNullOrWhiteSpace(databaseUrl))
            {
                Console.Error.WriteLine("error: DATABASE_URL is required for --migrate. Set it in the environment or a .env file.");
                return 1;
            }

            var tables = await MigrationRunner.MigrateAsync(databaseUrl, ct).ConfigureAwait(false);
            Console.WriteLine($"=== Schema ready: {tables.Length} tables ===");
            Console.WriteLine(string.Join(", ", tables));
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::migrate failed: {NotificationCli.CliRedact(ex.Message)}");
            return 1;
        }
    }
}