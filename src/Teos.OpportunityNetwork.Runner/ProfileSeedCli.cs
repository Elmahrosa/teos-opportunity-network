using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Teos.OpportunityNetwork.Core.Domain;
using Teos.OpportunityNetwork.Persistence;

namespace Teos.OpportunityNetwork.Runner;

/// <summary>
/// Console entry for the --seed-profile mode: idempotently upserts one user profile so the
/// matcher has someone to match against. Persistence only — no scoring, no matching, no LLM.
/// Never logs DATABASE_URL.
/// </summary>
public static class ProfileSeedCli
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> RunSeedProfileAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Console.Error.WriteLine($"error: profile JSON file not found: {path}");
            return 1;
        }

        UserProfile profile;
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            profile = JsonSerializer.Deserialize<UserProfile>(json, JsonOptions)
                      ?? throw new InvalidOperationException("profile JSON deserialized to null");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not parse profile JSON: {ex.Message}");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            Console.Error.WriteLine("error: profile 'displayName' is required.");
            return 1;
        }

        // A stable id makes re-seeding idempotent; generate one only when omitted.
        if (profile.Id == Guid.Empty)
            profile.Id = Guid.NewGuid();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(EnvFileLoader.Load(".env"))
            .AddEnvironmentVariables()
            .Build();

        var databaseUrl = configuration["DATABASE_URL"];
        if (string.IsNullOrWhiteSpace(databaseUrl))
        {
            Console.Error.WriteLine("error: DATABASE_URL is required for --seed-profile. Set it in the environment or a .env file.");
            return 1;
        }

        await new UserProfileRepository(databaseUrl).UpsertAsync(profile, ct).ConfigureAwait(false);
        Console.WriteLine($"=== Profile seeded: {profile.Id} ({profile.DisplayName}) — {profile.Skills.Length} skills ===");
        return 0;
    }
}
