namespace Teos.OpportunityNetwork.Runner;

/// <summary>Argument parsing for the TEOS Runner (single entry point, single dispatch).</summary>
public static class RunnerArgs
{
    public const string HelpText = """
        TEOS Opportunity Network — Runner
        Usage: teos-runner <mode> [options]

        Modes:
          --valuation                Repo valuation CLI (existing behavior).
                [--repo-url URL]     GitHub repository to value (default: this repo).
                [--output-json FILE] Write valuation JSON to FILE instead of printing a summary.
                [--tier TIER]        Valuation tier hint.
                [--smoke]            Deterministic valuation on mock data (legacy alias: `smoke`).
          --once                     Run a single notification pipeline pass and exit:
                                     ingest → normalize → dedup → match → persist → notify (Telegram).
          --loop [--interval SECS]   Run the notification pipeline on an interval until stopped
                                     (Ctrl+C). Interval overrides TEOS_LOOP_SECONDS (default 300).
          --migrate                  Idempotently apply migrations/0001_init.sql to DATABASE_URL
                                     (safe to run on every scan; CREATE TABLE IF NOT EXISTS).
          --help                     Show this help.

        Environment (once/loop):
          DATABASE_URL        PostgreSQL connection string (required for --once / --loop).
          TELEGRAM_BOT_TOKEN  Telegram bot token; when empty the runtime runs in DRY-RUN mode.
          GITHUB_TOKEN        Optional token for the GitHub search collector.
          OPENAI_API_KEY      Optional; the matcher falls back to neutral-50 without it.
          TEOS_LOOP_SECONDS   Loop interval in seconds (default 300).
          TEOS_CONFIDENCE     Confidence stored with each match result (default 0.9).
        """;

    /// <summary>Thrown for unknown flags / malformed options; implies help + exit code 1.</summary>
    public sealed class UsageException : Exception
    {
        public UsageException(string message) : base(message) { }
    }

    public static RunnerMode Parse(string[] args)
    {
        var list = args ?? Array.Empty<string>();
        var first = list.FirstOrDefault()?.ToLowerInvariant();

        switch (first)
        {
            case null or "" or "--loop" or "loop":
                return new RunnerMode(RunnerModeKind.Loop, LoopInterval: ParseInterval(list, fallback: null));
            case "--once" or "once":
                return new RunnerMode(RunnerModeKind.Once, LoopInterval: ParseInterval(list, fallback: null));
            case "--migrate" or "migrate":
                return new RunnerMode(RunnerModeKind.Migrate);
            case "--valuation" or "valuation":
                return ValuationMode(list.Skip(1).ToArray());
            case "smoke" or "--smoke":
                return new RunnerMode(RunnerModeKind.Valuation, ValuationArgs.SmokeMode);
            case "--help" or "help" or "-h":
                return new RunnerMode(RunnerModeKind.Help);
        }

        // Legacy calling convention: valuation flags without a leading mode keyword.
        if (list.Any(a => a.StartsWith("--repo-url", StringComparison.OrdinalIgnoreCase) ||
                          a.StartsWith("--output-json", StringComparison.OrdinalIgnoreCase) ||
                          a.StartsWith("--tier", StringComparison.OrdinalIgnoreCase)))
            return ValuationMode(list);

        throw new UsageException($"Unknown argument '{list[0]}'.");
    }

    private static RunnerMode ValuationMode(string[] args)
    {
        bool smoke = args.Any(a => a.Equals("smoke", StringComparison.OrdinalIgnoreCase) ||
                                   a.Equals("--smoke", StringComparison.OrdinalIgnoreCase));
        return new RunnerMode(RunnerModeKind.Valuation, new ValuationArgs(
            Smoke: smoke,
            RepoUrl: ArgValue(args, "--repo-url"),
            OutputJson: ArgValue(args, "--output-json"),
            Tier: ArgValue(args, "--tier")));
    }

    private static TimeSpan? ParseInterval(string[] args, TimeSpan? fallback)
    {
        var raw = ArgValue(args, "--interval");
        if (raw is null) return fallback;
        if (double.TryParse(raw, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);
        throw new UsageException($"Invalid --interval value '{raw}' (expected positive seconds).");
    }

    internal static string? ArgValue(string[] args, string flag)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith(flag, StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Length > flag.Length)
                    return arg[flag.Length..].TrimStart('=', ' ');
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    return args[i + 1];
            }
        }
        return null;
    }
}

public enum RunnerModeKind
{
    Help,
    Valuation,
    Once,
    Loop,
    Migrate
}

public sealed record RunnerMode(RunnerModeKind Kind, ValuationArgs? Valuation = null, TimeSpan? LoopInterval = null);

public sealed record ValuationArgs(bool Smoke, string? RepoUrl = null, string? OutputJson = null, string? Tier = null)
{
    public static ValuationArgs SmokeMode { get; } = new(true);
}