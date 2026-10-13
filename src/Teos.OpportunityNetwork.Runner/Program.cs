using Teos.OpportunityNetwork.Runner;

// TEOS Opportunity Network — single entry point, one dispatch.
//   --valuation  repo valuation CLI (legacy: `smoke`)
//   --once       one notification pipeline pass (ingest → match → persist → deliver)
//   --loop       notification pipeline on an interval until Ctrl+C
//   --migrate    idempotently apply migrations/0001_init.sql to DATABASE_URL
//   --help       usage
// The notification composition root lives in NotificationRuntime (see BoundaryTests:
// the Runner orchestrates; scoring/normalization/dedup logic stays in its owning library).

RunnerMode mode;
try
{
    mode = RunnerArgs.Parse(args);
}
catch (RunnerArgs.UsageException ux)
{
    Console.Error.WriteLine($"error: {ux.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(RunnerArgs.HelpText);
    return 1;
}

switch (mode.Kind)
{
    case RunnerModeKind.Help:
        Console.WriteLine(RunnerArgs.HelpText);
        return 0;

    case RunnerModeKind.Valuation:
        return await ValuationModes.RunAsync(mode.Valuation ?? ValuationArgs.SmokeMode);

    case RunnerModeKind.Once:
        return await NotificationCli.RunOnceAsync();

    case RunnerModeKind.Loop:
        return await NotificationCli.RunLoopAsync(mode.LoopInterval);

    case RunnerModeKind.Migrate:
        return await MigrationCli.RunMigrateAsync();

    default:
        Console.Error.WriteLine("error: unreachable mode.");
        return 1;
}