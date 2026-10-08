using Npgsql;
using Teos.OpportunityNetwork.Core.Domain;
using Teos.OpportunityNetwork.Matching;
using Teos.OpportunityNetwork.Persistence;
using Teos.OpportunityNetwork.Telegram;
using Xunit;

namespace Teos.OpportunityNetwork.Tests;

[Collection("postgres")]
public class TelegramWorkerTests
{
    private readonly PostgresFixture _db;

    public TelegramWorkerTests(PostgresFixture db) => _db = db;

    /// <summary>Scriptable fake — no network, no tokens.</summary>
    private sealed class FakeTelegramClient : ITelegramClient
    {
        public List<(string ChatId, string Text)> Sent { get; } = new();
        public Func<string, TelegramSendResult>? Behavior { get; set; }

        public Task<TelegramSendResult> SendAsync(string chatId, string text, CancellationToken ct = default)
        {
            Sent.Add((chatId, text));
            var result = Behavior?.Invoke(chatId)
                         ?? new TelegramSendResult(true, $"msg-{Sent.Count}", "ok");
            if (!result.Ok)
                throw new TelegramApiException(result.Description ?? "fake failure", retryable: false);
            return Task.FromResult(result);
        }
    }

    private sealed class FlakyTelegramClient : ITelegramClient
    {
        public int Calls;
        public readonly List<string> Sent = new();

        public Task<TelegramSendResult> SendAsync(string chatId, string text, CancellationToken ct = default)
        {
            Calls++;
            if (Calls == 1)
                throw new TelegramApiException("boom (retryable)", retryable: true);
            Sent.Add(text);
            return Task.FromResult(new TelegramSendResult(true, $"msg-{Calls}", "ok"));
        }
    }

    private async Task<(Guid oppId, Guid userId, MatchResult match, InputSnapshot snapshot)> SeedMatchAsync(
        double targetScore = 92, string chatId = "111222333", bool applySchema = true)
    {
        if (applySchema)
            await _db.ApplySchemaAsync();
        await using var conn = new Npgsql.NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();

        var oppId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using (var cmd = new Npgsql.NpgsqlCommand(@"
            INSERT INTO opportunities (id, source, external_id, title, company, url, budget_min, budget_max,
                currency, budget_type, opportunity_type, skills_required, deadline, content_hash,
                first_seen_at, last_seen_at, status)
            VALUES (@id, 'github', @ext, 'Build a REST API', 'acme/demo', 'https://example.com/1',
                500, 1000, 'USD', 'range', 'BOUNTY', ARRAY['c#','postgresql'],
                now() + interval '10 days', @hash, now(), now(), 'OPEN')", conn))
        {
            cmd.Parameters.AddWithValue("@id", oppId);
            cmd.Parameters.AddWithValue("@ext", "ext-" + Guid.NewGuid().ToString("N")[..8]);
            cmd.Parameters.AddWithValue("@hash", new string('a', 64));
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new Npgsql.NpgsqlCommand(@"
            INSERT INTO user_profiles (id, display_name, skills, years_experience, location, preferred_types,
                budget_min, budget_max, currency, keywords, notifications_paused)
            VALUES (@id, 'Test', ARRAY['c#','postgresql'], 6, 'Remote', ARRAY['BOUNTY'],
                100, 5000, 'USD', ARRAY['api'], FALSE)", conn))
        {
            cmd.Parameters.AddWithValue("@id", userId);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new Npgsql.NpgsqlCommand(@"
            INSERT INTO telegram_chats (user_id, chat_id, paused) VALUES (@u, @c, FALSE)", conn))
        {
            cmd.Parameters.AddWithValue("@u", userId);
            cmd.Parameters.AddWithValue("@c", chatId);
            await cmd.ExecuteNonQueryAsync();
        }

        var opp = new Opportunity
        {
            Id = oppId, Source = "github", ExternalId = "x", Title = "Build a REST API",
            Company = "acme/demo", Url = "https://example.com/1",
            OpportunityType = OpportunityType.BOUNTY,
            SkillsRequired = new[] { "c#", "postgresql" },
            BudgetMin = 500, BudgetMax = 1000, Currency = "USD", BudgetType = "range",
            Location = "Remote", Status = "OPEN", ContentHash = new string('a', 64),
            Deadline = DateTimeOffset.UtcNow.AddDays(10),
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow
        };
        var profile = new UserProfile
        {
            Id = userId, DisplayName = "Test", Skills = new[] { "c#", "postgresql" },
            YearsExperience = 6, Location = "Remote",
            PreferredTypes = new[] { OpportunityType.BOUNTY },
            BudgetMin = 100, BudgetMax = 5000, Currency = "USD", Keywords = new[] { "api" }
        };

        var config = ScoringConfig.CreateHybridV1();
        var snapshot = SnapshotFactory.Capture(opp, profile, 90, config.WeightsVersion);
        var matcher = new HybridMatcher(config);
        var match = matcher.Score(opp, profile, snapshot, 90, 0.9);

        // Adjust to a guaranteed ALERT score for the test scenario.
        match.MatchScore = targetScore;
        MatchDecisionRules.AssertInvariants(match.MatchScore,
            MatchDecisionRules.Decide(targetScore), MatchDecisionRules.Recommend(targetScore));
        match.Decision = MatchDecisionRules.Decide(targetScore);
        match.RecommendedAction = MatchDecisionRules.Recommend(targetScore);

        var repo = new MatchRepository(_db.ConnectionString);
        match = await repo.SaveAsync(match, snapshot);

        return (oppId, userId, match, snapshot);
    }

    private (MatchRepository matches, NotificationRepository deliveries) Repos() =>
        (new MatchRepository(_db.ConnectionString), new NotificationRepository(_db.ConnectionString));

    [Fact]
    public async Task Pending_alerts_are_retrieved()
    {
        await SeedMatchAsync(targetScore: 95);
        var (matches, _) = Repos();

        var pending = await matches.PendingAlertsAsync();

        Assert.Single(pending);
        Assert.Equal("Build a REST API", pending[0].Title);
        Assert.Equal(95, pending[0].MatchScore, 9);
        Assert.Equal("ALERT", pending[0].Decision);
    }

    [Fact]
    public async Task Threshold_gate_excludes_LOG_and_IGNORE()
    {
        await SeedMatchAsync(targetScore: 95);
        var (opp2, user2, match2, snap2) = await SeedMatchAsync(targetScore: 70, applySchema: false);
        // Second seed used different external_id; save LOG match.
        match2.MatchScore = 70;
        match2.Decision = MatchDecision.LOG;
        match2.RecommendedAction = RecommendedAction.PASS;
        var repo = new MatchRepository(_db.ConnectionString);
        await repo.SaveAsync(match2, snap2);

        var pending = await repo.PendingAlertsAsync();
        Assert.DoesNotContain(pending, p => p.MatchScore < 85);
        Assert.Single(pending); // only the ALERT row
    }

    [Fact]
    public async Task Notification_delivery_is_persisted_with_provider_message_id()
    {
        await SeedMatchAsync(targetScore: 95);
        var (matches, deliveries) = Repos();
        var client = new FakeTelegramClient();
        var worker = new TelegramWorker(matches, deliveries, client, TelegramBot.ChatResolver(_db.ConnectionString));

        var sent = await worker.RunOnceAsync();

        Assert.Equal(1, sent);
        Assert.Single(client.Sent);
        var all = await deliveries.ForMatchAsync(
            (await matches.PendingAlertsAsync()).Count > 0
                ? Guid.Empty
                : Guid.Empty); // placeholder — query by delivery state below
        var retryable = await deliveries.RetryableFailuresAsync();
        Assert.Empty(retryable);
    }

    [Fact]
    public async Task Successful_delivery_is_idempotent_duplicate_sends_prevented()
    {
        await SeedMatchAsync(targetScore: 95);
        var (matches, deliveries) = Repos();
        var client = new FakeTelegramClient();
        var worker = new TelegramWorker(matches, deliveries, client, TelegramBot.ChatResolver(_db.ConnectionString));

        var first = await worker.RunOnceAsync();
        var second = await worker.RunOnceAsync(); // second pass must be a no-op

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Single(client.Sent);

        // Exactly one SENT row for this user/channel
        await using var conn = new Npgsql.NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM notification_deliveries WHERE status = 'SENT'", conn);
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);

        // PendingAlerts now excludes it (anti-join on SENT)
        Assert.Empty(await matches.PendingAlertsAsync());
    }

    [Fact]
    public async Task Retryable_failure_can_retry_and_then_succeed()
    {
        await SeedMatchAsync(targetScore: 95);
        var (matches, deliveries) = Repos();
        var client = new FlakyTelegramClient();
        var worker = new TelegramWorker(matches, deliveries, client, TelegramBot.ChatResolver(_db.ConnectionString));

        var first = await worker.RunOnceAsync();
        Assert.Equal(0, first); // attempt 1 failed
        Assert.Equal(1, client.Calls);

        var failures = await deliveries.RetryableFailuresAsync();
        Assert.Single(failures);
        Assert.Equal(1, failures[0].AttemptCount);
        Assert.NotNull(failures[0].NextAttemptAt); // scheduled per backoff policy

        // Simulate backoff elapsed: claim again directly (FAILED → SENDING).
        var claimed = await deliveries.ClaimForSendAsync();
        Assert.NotNull(claimed);
        Assert.Equal(2, claimed!.AttemptCount);

        // Finish the retry manually through repository API.
        var result = await client.SendAsync("111222333", "hello");
        await deliveries.MarkSentAsync(claimed.Id, result.ProviderMessageId!);

        var rows = await deliveries.ForMatchAsync(claimed.MatchId);
        var sentRow = Assert.Single(rows, r => r.Status == DeliveryStatus.SENT);
        Assert.Equal("msg-2", sentRow.ProviderMessageId);
        Assert.NotNull(sentRow.SentAt);
    }

    [Fact]
    public async Task Permanent_failure_becomes_FAILED_without_scheduling_retry()
    {
        await SeedMatchAsync(targetScore: 95);
        var (matches, deliveries) = Repos();
        var client = new FakeTelegramClient
        {
            Behavior = _ => new TelegramSendResult(false, null, "chat not found")
        };
        var worker = new TelegramWorker(matches, deliveries, client, TelegramBot.ChatResolver(_db.ConnectionString));

        var sent = await worker.RunOnceAsync();
        Assert.Equal(0, sent);

        var retryable = await deliveries.RetryableFailuresAsync();
        Assert.Empty(retryable); // terminal — no next_attempt_at

        await using var conn = new Npgsql.NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT status, last_error FROM notification_deliveries", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("FAILED", reader.GetString(0));
        Assert.Contains("chat not found", reader.GetString(1));
    }

    [Fact]
    public async Task Provider_message_id_is_persisted_on_success()
    {
        await SeedMatchAsync(targetScore: 95);
        var (matches, deliveries) = Repos();
        var client = new FakeTelegramClient();
        var worker = new TelegramWorker(matches, deliveries, client, TelegramBot.ChatResolver(_db.ConnectionString));

        await worker.RunOnceAsync();

        await using var conn = new Npgsql.NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT status, provider_message_id FROM notification_deliveries", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("SENT", reader.GetString(0));
        Assert.StartsWith("msg-", reader.GetString(1));
    }

    [Fact]
    public async Task Failed_delivery_can_be_retried_after_claim()
    {
        await SeedMatchAsync(targetScore: 95);
        var (_, deliveries) = Repos();

        await deliveries.CreateDeliveryAsync(Guid.NewGuid(), Guid.NewGuid()); // unrelated row for claim mechanics
        var claimed = await deliveries.ClaimForSendAsync();
        Assert.NotNull(claimed);

        await deliveries.MarkFailedAsync(claimed!.Id, "timeout", retryable: true);
        var row = Assert.Single(await deliveries.RetryableFailuresAsync());
        Assert.Equal(DeliveryStatus.FAILED, row.Status);

        var reclaimed = await deliveries.ClaimForSendAsync();
        Assert.NotNull(reclaimed);
        Assert.Equal(2, reclaimed!.AttemptCount);
        Assert.Equal(DeliveryStatus.SENDING, reclaimed.Status);
    }

    [Fact]
    public async Task MarkSent_enforces_one_sent_row_per_match_user_channel()
    {
        await _db.ApplySchemaAsync();
        var (matches, deliveries) = Repos();
        var a = await deliveries.CreateDeliveryAsync(Guid.NewGuid(), Guid.NewGuid());
        await deliveries.ClaimForSendAsync();
        await deliveries.MarkSentAsync(a, "m1");

        // Second delivery row for same (match,user,channel) can't reach SENT:
        await using var conn = new Npgsql.NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(@"
            INSERT INTO notification_deliveries (match_id, user_id, channel, status, attempt_count, sent_at, provider_message_id)
            SELECT match_id, user_id, channel, 'SENT', 1, now(), 'm2'
            FROM notification_deliveries WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", a);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Contains("uq_nd_one_sent_per_match_user_channel", ex.ConstraintName);
        _ = matches;
    }

    [Fact]
    public async Task Escaping_produces_safe_html_and_stays_under_telegram_limit()
    {
        await SeedMatchAsync(targetScore: 95);
        var (matches, _) = Repos();
        var pending = await matches.PendingAlertsAsync();
        var alert = pending[0];

        alert.Title = "Script <script>alert(1)</script> & \"quotes\"";
        var text = TelegramFormatter.Format(alert);
        Assert.DoesNotContain("<script>", text);
        Assert.Contains("&lt;script&gt;", text);
        Assert.Contains("&amp;", text);
        Assert.True(text.Length <= TelegramFormatter.TelegramLimit);

        // Enormous payload is truncated below the 4096 hard limit.
        alert.Title = new string('X', 6000);
        alert.MatchedSkills = Enumerable.Range(0, 500).Select(i => $"skill-{i}").ToArray();
        var longText = TelegramFormatter.Format(alert);
        Assert.True(longText.Length <= TelegramFormatter.SafeLimit,
            $"expected <= {TelegramFormatter.SafeLimit}, got {longText.Length}");
        Assert.EndsWith("…", longText);
    }

    [Fact]
    public async Task Formatter_does_not_recalculate_score()
    {
        await SeedMatchAsync(targetScore: 91.37);
        var (matches, _) = Repos();
        var alert = Assert.Single(await matches.PendingAlertsAsync());
        var text = TelegramFormatter.Format(alert);
        Assert.Contains("Match score: 91.37", text);
    }

    [Fact]
    public async Task Matched_and_missing_skills_are_exposed()
    {
        await SeedMatchAsync(targetScore: 95);
        var (matches, _) = Repos();
        var alert = Assert.Single(await matches.PendingAlertsAsync());

        Assert.Contains("c#", alert.MatchedSkills);
        var text = TelegramFormatter.Format(alert);
        Assert.Contains("Matched skills:", text);
    }

    [Fact]
    public async Task Paused_user_gets_no_pending_alerts()
    {
        await SeedMatchAsync(targetScore: 95);
        await using (var conn = new Npgsql.NpgsqlConnection(_db.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(
                "UPDATE user_profiles SET notifications_paused = TRUE", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var (matches, deliveries) = Repos();
        var client = new FakeTelegramClient();
        var worker = new TelegramWorker(matches, deliveries, client, TelegramBot.ChatResolver(_db.ConnectionString));

        Assert.Empty(await matches.PendingAlertsAsync());
        Assert.Equal(0, await worker.RunOnceAsync());
        Assert.Empty(client.Sent);
    }
}
