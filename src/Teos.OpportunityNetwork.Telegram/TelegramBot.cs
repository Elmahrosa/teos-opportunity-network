using Npgsql;
using Teos.OpportunityNetwork.Persistence;

namespace Teos.OpportunityNetwork.Telegram;

/// <summary>
/// Minimal bot surface: registers chats (/start), reads/pauses the persisted
/// notification preference (/pause, /resume), and reports per-chat state (/status).
/// /help lists every command. Only commands that fit the actual user/profile model
/// are implemented — no invented functionality.
/// </summary>
public sealed class TelegramBot
{
    private static readonly string HelpText =
        "<b>Available commands</b>\n" +
        "/start – link this chat to your TEOS profile and enable ALERT notifications\n" +
        "/pause – pause notifications\n" +
        "/resume – resume notifications\n" +
        "/status – link state and alerts delivered recently\n" +
        "/help – this message";

    private readonly string _conn;
    private readonly ITelegramClient _client;

    public TelegramBot(string connectionString, ITelegramClient client)
    {
        _conn = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task HandleUpdateAsync(long chatId, string? text, Guid? knownUserId = null, CancellationToken ct = default)
    {
        var command = (text ?? "").Trim().Split(' ')[0].ToLowerInvariant();
        switch (command)
        {
            case "/start":
            {
                var linked = await RegisterChatAsync(chatId, knownUserId, ct).ConfigureAwait(false);
                // Only confirm when the chat was actually linked — never claim a false success.
                await _client.SendAsync(chatId.ToString(),
                    linked
                        ? "<b>TEOS Opportunity Network</b>\nYour chat is linked. You will receive ALERT-level match notifications."
                        : "This chat is not linked to a TEOS profile yet. Link it before enabling notifications.",
                    ct).ConfigureAwait(false);
                break;
            }

            case "/pause":
                await SetPausedAsync(chatId, true, ct).ConfigureAwait(false);
                await _client.SendAsync(chatId.ToString(), "Notifications paused.", ct).ConfigureAwait(false);
                break;

            case "/resume":
                await SetPausedAsync(chatId, false, ct).ConfigureAwait(false);
                await _client.SendAsync(chatId.ToString(), "Notifications resumed.", ct).ConfigureAwait(false);
                break;

            case "/help":
                await _client.SendAsync(chatId.ToString(), HelpText, ct).ConfigureAwait(false);
                break;

            case "/status":
                await SendStatusAsync(chatId, ct).ConfigureAwait(false);
                break;

            default:
                await _client.SendAsync(chatId.ToString(),
                    "Unknown command. Send /help to list available commands.", ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task<bool> RegisterChatAsync(long chatId, Guid? userId, CancellationToken ct)
    {
        if (userId is null) return false; // cannot link without an authenticated user mapping
        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO telegram_chats (user_id, chat_id, paused, created_at, updated_at)
            VALUES (@u, @c, FALSE, now(), now())
            ON CONFLICT (user_id) DO UPDATE SET chat_id = EXCLUDED.chat_id, paused = FALSE, updated_at = now()",
            conn, tx);
        cmd.Parameters.AddWithValue("@u", userId.Value);
        cmd.Parameters.AddWithValue("@c", chatId.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await using var resume = new NpgsqlCommand(
            "UPDATE user_profiles SET notifications_paused = FALSE, updated_at = now() WHERE id = @u", conn, tx);
        resume.Parameters.AddWithValue("@u", userId.Value);
        await resume.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private async Task SetPausedAsync(long chatId, bool paused, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "UPDATE telegram_chats SET paused = @p, updated_at = now() WHERE chat_id = @c", conn, tx);
        cmd.Parameters.AddWithValue("@p", paused);
        cmd.Parameters.AddWithValue("@c", chatId.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await using var profile = new NpgsqlCommand(@"
            UPDATE user_profiles SET notifications_paused = @p, updated_at = now()
            WHERE id IN (SELECT user_id FROM telegram_chats WHERE chat_id = @c)", conn, tx);
        profile.Parameters.AddWithValue("@p", paused);
        profile.Parameters.AddWithValue("@c", chatId.ToString());
        await profile.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports persisted per-chat state: linked profile, paused flag, and how many
    /// ALERT notifications were actually delivered (SENT) in the last 7 days.
    /// Never invents a score or re-evaluates a match — reads deliveries only.
    /// </summary>
    private async Task SendStatusAsync(long chatId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        Guid? userId = null;
        string? displayName = null;
        var paused = false;

        await using (var cmd = new NpgsqlCommand(@"
            SELECT u.id, u.display_name, u.notifications_paused, t.paused
            FROM telegram_chats t
            JOIN user_profiles u ON u.id = t.user_id
            WHERE t.chat_id = @c", conn))
        {
            cmd.Parameters.AddWithValue("@c", chatId.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                userId = reader.GetGuid(0);
                displayName = reader.GetString(1);
                paused = reader.GetBoolean(2) || reader.GetBoolean(3);
            }
        }

        if (userId is null)
        {
            await _client.SendAsync(chatId.ToString(),
                "This chat is not linked to a TEOS profile yet. Send /start to link it and enable ALERT notifications.",
                ct).ConfigureAwait(false);
            return;
        }

        long deliveredLast7Days;
        await using (var count = new NpgsqlCommand(@"
            SELECT count(*) FROM notification_deliveries
            WHERE user_id = @u AND status = 'SENT' AND sent_at >= now() - interval '7 days'", conn))
        {
            count.Parameters.AddWithValue("@u", userId.Value);
            deliveredLast7Days = (long)(await count.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        var state = paused ? "PAUSED" : "active";
        await _client.SendAsync(chatId.ToString(),
            $"<b>TEOS status</b>\nLinked profile: <b>{Escape(displayName ?? "")}</b>\n" +
            $"Notifications: {state}\nAlerts delivered (last 7 days): {deliveredLast7Days}",
            ct).ConfigureAwait(false);
    }

    /// <summary>HTML-escapes user-supplied text before it appears in a bot message.</summary>
    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Resolves the Telegram chat id for a user (throws when unregistered).</summary>
    public static Func<Guid, string> ChatResolver(string connectionString) => userId =>
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT chat_id FROM telegram_chats WHERE user_id = @u AND paused = FALSE", conn);
        cmd.Parameters.AddWithValue("@u", userId);
        var chatId = cmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(chatId))
            throw new InvalidOperationException($"no Telegram chat registered for user {userId}");
        return chatId;
    };
}
