using Teos.OpportunityNetwork.Runner;
using Xunit;

namespace Teos.OpportunityNetwork.Tests;

public class RunnerArgsTests
{
    [Fact]
    public void Parse_help()
    {
        Assert.Equal(RunnerModeKind.Help, RunnerArgs.Parse(new[] { "--help" }).Kind);
        Assert.Equal(RunnerModeKind.Help, RunnerArgs.Parse(new[] { "-h" }).Kind);
        Assert.Equal(RunnerModeKind.Help, RunnerArgs.Parse(new[] { "help" }).Kind);
    }

    [Fact]
    public void Parse_smoke_legacy_alias()
    {
        var mode = RunnerArgs.Parse(new[] { "smoke" });
        Assert.Equal(RunnerModeKind.Valuation, mode.Kind);
        Assert.True(mode.Valuation!.Smoke);

        Assert.True(RunnerArgs.Parse(new[] { "--smoke" }).Valuation!.Smoke);
    }

    [Fact]
    public void Parse_valuation_with_flags()
    {
        var mode = RunnerArgs.Parse(new[]
        {
            "--valuation", "--repo-url", "https://github.com/acme/app",
            "--output-json", "out.json", "--tier", "pro"
        });
        Assert.Equal(RunnerModeKind.Valuation, mode.Kind);
        Assert.Equal("https://github.com/acme/app", mode.Valuation!.RepoUrl);
        Assert.Equal("out.json", mode.Valuation.OutputJson);
        Assert.Equal("pro", mode.Valuation.Tier);
        Assert.False(mode.Valuation.Smoke);
    }

    [Fact]
    public void Parse_legacy_valuation_flags_without_keyword()
    {
        var mode = RunnerArgs.Parse(new[] { "--repo-url=https://github.com/acme/app" });
        Assert.Equal(RunnerModeKind.Valuation, mode.Kind);
        Assert.Equal("https://github.com/acme/app", mode.Valuation!.RepoUrl);
    }

    [Fact]
    public void Parse_once()
    {
        Assert.Equal(RunnerModeKind.Once, RunnerArgs.Parse(new[] { "--once" }).Kind);
    }

    [Fact]
    public void Parse_once_with_interval()
    {
        var mode = RunnerArgs.Parse(new[] { "--once", "--interval", "60" });
        Assert.Equal(RunnerModeKind.Once, mode.Kind);
        Assert.Equal(TimeSpan.FromSeconds(60), mode.LoopInterval);
    }

    [Fact]
    public void No_args_defaults_to_loop()
    {
        Assert.Equal(RunnerModeKind.Loop, RunnerArgs.Parse(Array.Empty<string>()).Kind);
    }

    [Fact]
    public void Parse_loop_interval_overrides_default()
    {
        var mode = RunnerArgs.Parse(new[] { "--loop", "--interval", "120" });
        Assert.Equal(RunnerModeKind.Loop, mode.Kind);
        Assert.Equal(TimeSpan.FromSeconds(120), mode.LoopInterval);
    }

    [Fact]
    public void Parse_seed_profile()
    {
        var mode = RunnerArgs.Parse(new[] { "--seed-profile", "profile.json" });
        Assert.Equal(RunnerModeKind.SeedProfile, mode.Kind);
        Assert.Equal("profile.json", mode.SeedProfilePath);
    }

    [Fact]
    public void Parse_seed_profile_equals_form()
    {
        var mode = RunnerArgs.Parse(new[] { "--seed-profile=profile.json" });
        Assert.Equal(RunnerModeKind.SeedProfile, mode.Kind);
        Assert.Equal("profile.json", mode.SeedProfilePath);
    }

    [Fact]
    public void Seed_profile_without_path_throws_usage_exception()
    {
        Assert.Throws<RunnerArgs.UsageException>(() => RunnerArgs.Parse(new[] { "--seed-profile" }));
    }

    [Fact]
    public void Unknown_flag_throws_usage_exception()
    {
        Assert.Throws<RunnerArgs.UsageException>(() => RunnerArgs.Parse(new[] { "--bogus" }));
    }

    [Fact]
    public void Invalid_interval_throws_usage_exception()
    {
        Assert.Throws<RunnerArgs.UsageException>(() => RunnerArgs.Parse(new[] { "--loop", "--interval", "abc" }));
    }
}