using Npgsql;
using Teos.OpportunityNetwork.Telegram;
using Xunit;

namespace Teos.OpportunityNetwork.Tests;

[Collection("postgres")]
public class TelegramBotTests
{
    private readonly PostgresFixture _db;

    public TelegramBotTests(PostgresFixture db) => _db = db;

    /// <summary>Scriptable fake — no network, no tokens.</summary>
    private sealed class FakeTelegramClient : ITelegramClient
    {
        public List<(string ChatId, string Text)> Sent { get; } = new();

        public Task<TelegramSendResult> SendAsync(string chatId, string htmlText, CancellationToken ct = default)
        {
            Sent.Add((chatId, htmlText));
            return Task.FromResult(new TelegramSendResult(true, $"msg-{Sent.Count}", "ok"));
        }
    }

    private async Task<Guid> SeedProfileAsync(string chatId = "555666777", bool linkChat = false)
    {
        await _db.ApplySchemaAsync();
        var userId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using (var cmd = new NpgsqlCommand(@"
            INSERT INTO user_profiles (id, display_name, skills, years_experience, preferred_types, notifications_paused)
            VALUES (@id, 'Test User', ARRAY['c#'], 5, ARRAY['BOUNTY'], FALSE)", conn))
        {
            cmd.Parameters.AddWithValue("@id", userId);
            await cmd.ExecuteNonQueryAsync();
        }

        if (linkChat)
        {
            await using var link = new NpgsqlCommand(@"
                INSERT INTO telegram_chats (user_id, chat_id, paused) VALUES (@u, @c, FALSE)", conn);
            link.Parameters.AddWithValue("@u", userId);
            link.Parameters.AddWithValue("@c", chatId);
            await link.ExecuteNonQueryAsync();
        }

        return userId;
    }

    private async Task SeedSentDeliveryAsync(Guid userId, string providerMessageId = "m1")
    {
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO notification_deliveries (match_id, user_id, channel, status, attempt_count,
                provider_message_id, sent_at)
            VALUES (@m, @u, 'telegram', 'SENT', 1, @p, now())", conn);
        cmd.Parameters.AddWithValue("@m", Guid.NewGuid());
        cmd.Parameters.AddWithValue("@u", userId);
        cmd.Parameters.AddWithValue("@p", providerMessageId);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Start_links_chat_and_confirm_is_sent()
    {
        var userId = await SeedProfileAsync(linkChat: false);
        var client = new FakeTelegramClient();
        var bot = new TelegramBot(_db.ConnectionString, client);

        await bot.HandleUpdateAsync(555666777, "/start", userId);

        var sent = Assert.Single(client.Sent);
        Assert.Contains("linked", sent.Text, StringComparison.OrdinalIgnoreCase);

        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT chat_id, paused FROM telegram_chats WHERE user_id = @u", conn);
        cmd.Parameters.AddWithValue("@u", userId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("555666777", reader.GetString(0));
        Assert.False(reader.GetBoolean(1));
    }

    [Fact]
    public async Task Start_without_known_user_does_not_claim_linkage()
    {
        await _db.ApplySchemaAsync();
        var client = new FakeTelegramClient();
        var bot = new TelegramBot(_db.ConnectionString, client);

        await bot.HandleUpdateAsync(555666777, "/start", knownUserId: null);

        var sent = Assert.Single(client.Sent);
        Assert.Contains("not linked", sent.Text, StringComparison.OrdinalIgnoreCase);

        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM telegram_chats WHERE chat_id = @c", conn);
        cmd.Parameters.AddWithValue("@c", "555666777");
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Pause_and_resume_are_persisted_on_chat_and_profile()
    {
        var userId = await SeedProfileAsync(linkChat: true);
        var client = new FakeTelegramClient();
        var bot = new TelegramBot(_db.ConnectionString, client);

        await bot.HandleUpdateAsync(555666777, "/pause");
        await bot.HandleUpdateAsync(555666777, "/resume");

        Assert.Equal("Notifications paused.", client.Sent[0].Text);
        Assert.Equal("Notifications resumed.", client.Sent[1].Text);

        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT t.paused, u.notifications_paused
            FROM telegram_chats t JOIN user_profiles u ON u.id = t.user_id
            WHERE t.user_id = @u", conn);
        cmd.Parameters.AddWithValue("@u", userId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(reader.GetBoolean(0)); // chat resumed
        Assert.False(reader.GetBoolean(1)); // profile resumed
    }

    [Fact]
    public async Task Help_lists_every_command()
    {
        var client = new FakeTelegramClient();
        var bot = new TelegramBot(_db.ConnectionString, client);

        await bot.HandleUpdateAsync(555666777, "/help");

        var sent = Assert.Single(client.Sent);
        Assert.Contains("/start", sent.Text);
        Assert.Contains("/pause", sent.Text);
        Assert.Contains("/resume", sent.Text);
        Assert.Contains("/status", sent.Text);
        Assert.Contains("/help", sent.Text);
    }

    [Fact]
    public async Task Status_reports_link_state_and_recent_alert_count()
    {
        var userId = await SeedProfileAsync(linkChat: true);
        await SeedSentDeliveryAsync(userId, "m1");
        await SeedSentDeliveryAsync(userId, "m2");
        var client = new FakeTelegramClient();
        var bot = new TelegramBot(_db.ConnectionString, client);

        await bot.HandleUpdateAsync(555666777, "/status");

        var sent = Assert.Single(client.Sent);
        Assert.Contains("Test User", sent.Text);
        Assert.Contains("active", sent.Text);
        Assert.Contains("Alerts delivered (last 7 days): 2", sent.Text);
    }

    [Fact]
    public async Task Status_for_unlinked_chat_explains_start()
    {
        await SeedProfileAsync(linkChat: false); // profile exists, chat does not
        var client = new FakeTelegramClient();
        var bot = new TelegramBot(_db.ConnectionString, client);

        await bot.HandleUpdateAsync(555666777, "/status");

        var sent = Assert.Single(client.Sent);
        Assert.Contains("not linked", sent.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/start", sent.Text);
    }

    [Fact]
    public async Task Unknown_command_offers_help()
    {
        var client = new FakeTelegramClient();
        var bot = new TelegramBot(_db.ConnectionString, client);

        await bot.HandleUpdateAsync(555666777, "/nonsense");

        var sent = Assert.Single(client.Sent);
        Assert.Contains("/help", sent.Text);
    }
}