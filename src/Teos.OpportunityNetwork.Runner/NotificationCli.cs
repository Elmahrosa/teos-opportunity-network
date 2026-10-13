using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Teos.OpportunityNetwork.Runner;

/// <summary>Console entry helpers for the --once / --loop notification runtime.</summary>
public static class NotificationCli
{
    public static async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        try
        {
            var runtime = BuildRuntime();
            var report = await runtime.RunPipelineAsync(ct).ConfigureAwait(false);
            WriteReport(report);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::notification pass failed: {CliRedact(ex.Message)}");
            return 1;
        }
    }

    public static async Task<int> RunLoopAsync(TimeSpan? cliInterval, CancellationToken ct = default)
    {
        try
        {
            var runtime = BuildRuntime();
            var interval = cliInterval ?? ParseSeconds(Environment.GetEnvironmentVariable("TEOS_LOOP_SECONDS"), 300);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ConsoleCancelEventHandler handler = (_, e) =>
            {
                e.Cancel = true; // allow graceful stop of the loop
                cts.Cancel();
            };
            Console.CancelKeyPress += handler;
            try
            {
                await runtime.RunLoopAsync(interval, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                Console.CancelKeyPress -= handler;
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0; // graceful Ctrl+C
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::loop failed: {CliRedact(ex.Message)}");
            return 1;
        }
    }

    /// <summary>Redacts every secret visible in the process environment before printing (world-readable public Actions logs).</summary>
    internal static string CliRedact(string message) =>
        SecretRedactor.RedactSecrets(message, SecretRedactor.FromCurrentEnvironment());

    private static NotificationRuntime BuildRuntime()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(EnvFileLoader.Load(".env"))
            .AddEnvironmentVariables()
            .Build();
        return NotificationRuntime.FromConfiguration(configuration);
    }

    private static void WriteReport(PipelineReport report)
    {
        Console.WriteLine("=== TEOS Notification Pipeline (--once) ===");
        Console.WriteLine($"Ingested: {report.Collected} | New opportunities: {report.New} | " +
                          $"Source duplicates: {report.SourceDuplicates} | Cross-source candidates: {report.CrossSourceCandidates}");
        Console.WriteLine($"Profiles: {report.Profiles} | New matches persisted: {report.MatchesSaved} | " +
                          $"Deliveries sent: {report.DeliveriesSent}");
        Console.WriteLine("=== Pass complete ===");
    }

    private static TimeSpan ParseSeconds(string? raw, double fallback)
    {
        if (raw is not null &&
            double.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);
        return TimeSpan.FromSeconds(fallback);
    }
}