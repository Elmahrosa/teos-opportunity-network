using Npgsql;
using Teos.OpportunityNetwork.Core.Domain;
using Teos.OpportunityNetwork.Persistence;
using Teos.OpportunityNetwork.Runner;
using Xunit;

namespace Teos.OpportunityNetwork.Tests;

/// <summary>
/// End-to-end proof of the Step 10 runtime: a real once-pass over a fake GitHub search
/// response with a real Postgres schema — ingest → normalize → dedup → match → persist →
/// deliver (dry-run Telegram client). Asserts the persisted opportunity, ALERT match +
/// immutable snapshot, and the SENT delivery row.
/// </summary>
[Collection("postgres")]
public class RuntimeOnceE2eTests : IAsyncLifetime
{
    private readonly PostgresFixture _db;

    public RuntimeOnceE2eTests(PostgresFixture db) => _db = db;

    public Task InitializeAsync() => _db.ApplySchemaAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private const string SearchJson = """
        {"items":[{"id":1,"node_id":"I_x","title":"Fix login bug","body":"details",
        "html_url":"https://github.com/acme/app/issues/7","created_at":"2026-09-01T00:00:00Z",
        "labels":[{"name":"bounty"},{"name":"budget:$500"},{"name":"skill:csharp"},{"name":"skill:csharp-api"}]}]}
        """;

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(SearchJson)
            });
    }

    [Fact]
    public async Task Once_pass_ingests_matches_and_delivers_end_to_end()
    {
        var profileId = Guid.NewGuid();

        // Skills mirror the normalized GitHub labels so every skill matches →
        // score 90 → ALERT/APPLY_NOW → a real delivery row.
        await new UserProfileRepository(_db.ConnectionString).UpsertAsync(new UserProfile
        {
            Id = profileId,
            DisplayName = "Tester",
            Skills = new[] { "bounty", "budget:$500", "skill:csharp", "skill:csharp-api" },
            YearsExperience = 5,
            PreferredTypes = new[] { OpportunityType.BOUNTY },
            BudgetMin = 400,
            BudgetMax = 600,
            Currency = "USD"
        });

        await using (var conn = new NpgsqlConnection(_db.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO telegram_chats (user_id, chat_id) VALUES (@u, @c)", conn);
            cmd.Parameters.AddWithValue("@u", profileId);
            cmd.Parameters.AddWithValue("@c", "123456789");
            await cmd.ExecuteNonQueryAsync();
        }

        var runtime = new NotificationRuntime(
            databaseUrl: _db.ConnectionString,
            telegramBotToken: null, // → dry-run client (nothing leaves the machine)
            githubToken: null,
            openAiApiKey: null,     // → neutral-50 semantic
            httpFactory: () => new HttpClient(new StubHandler()) { BaseAddress = new Uri("https://api.github.com") },
            log: TextWriter.Null);

        var report = await runtime.RunPipelineAsync();

        Assert.Equal(1, report.Collected);
        Assert.Equal(1, report.New);
        Assert.Equal(0, report.SourceDuplicates);
        Assert.Equal(0, report.CrossSourceCandidates);
        Assert.Equal(1, report.Profiles);
        Assert.Equal(1, report.MatchesSaved);
        Assert.Equal(1, report.DeliveriesSent);

        // ---- Persisted evidence -------------------------------------------------
        var oppRepo = new OpportunityRepository(_db.ConnectionString);
        var stored = await oppRepo.FindBySourceExternalAsync("github", "I_x");
        Assert.NotNull(stored);
        Assert.Equal(OpportunityType.BOUNTY, stored!.OpportunityType);

        var matches = new MatchRepository(_db.ConnectionString);
        var saved = await matches.FindSavedMatchAsync(stored.Id, profileId,
            ScoringConfig.HybridV1, ScoringConfig.MatcherEngineV1);
        Assert.NotNull(saved);
        Assert.True(saved!.MatchScore >= 85, $"expected ALERT score >= 85, got {saved.MatchScore:0.##}");
        Assert.Equal(MatchDecision.ALERT, saved.Decision);

        var stillPending = await matches.PendingAlertsAsync();
        Assert.DoesNotContain(stillPending, a => a.MatchId == saved.Id); // already SENT

        var deliveries = await new NotificationRepository(_db.ConnectionString).ForMatchAsync(saved.Id);
        var sentRow = Assert.Single(deliveries);
        Assert.Equal(DeliveryStatus.SENT, sentRow.Status);
        Assert.StartsWith("dry-run-", sentRow.ProviderMessageId);
    }
}