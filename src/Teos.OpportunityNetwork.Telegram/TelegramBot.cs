using Npgsql;
using Teos.OpportunityNetwork.Persistence;

namespace Teos.OpportunityNetwork.Telegram;

/// <summary>
/// Minimal bot surface: registers chats (/start) and reads/pauses the persisted
/// notification preference (/pause, /resume). Only commands that fit the actual
/// user/profile model are implemented — no invented functionality.
/// </summary>
public sealed class TelegramBot
{
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
                await RegisterChatAsync(chatId, knownUserId, ct).ConfigureAwait(false);
                await _client.SendAsync(chatId.ToString(),
                    "<b>TEOS Opportunity Network</b>\nYour chat is linked. You will receive ALERT-level match notifications.",
                    ct).ConfigureAwait(false);
                break;

            case "/pause":
                await SetPausedAsync(chatId, true, ct).ConfigureAwait(false);
                await _client.SendAsync(chatId.ToString(), "Notifications paused.", ct).ConfigureAwait(false);
                break;

            case "/resume":
                await SetPausedAsync(chatId, false, ct).ConfigureAwait(false);
                await _client.SendAsync(chatId.ToString(), "Notifications resumed.", ct).ConfigureAwait(false);
                break;

            default:
                await _client.SendAsync(chatId.ToString(),
                    "Available commands: /start, /pause, /resume", ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task RegisterChatAsync(long chatId, Guid? userId, CancellationToken ct)
    {
        if (userId is null) return; // cannot link without an authenticated user mapping
        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO telegram_chats (user_id, chat_id, paused, created_at, updated_at)
            VALUES (@u, @c, FALSE, now(), now())
            ON CONFLICT (user_id) DO UPDATE SET chat_id = EXCLUDED.chat_id, paused = FALSE, updated_at = now()",
            conn);
        cmd.Parameters.AddWithValue("@u", userId.Value);
        cmd.Parameters.AddWithValue("@c", chatId.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task SetPausedAsync(long chatId, bool paused, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "UPDATE telegram_chats SET paused = @p, updated_at = now() WHERE chat_id = @c", conn);
        cmd.Parameters.AddWithValue("@p", paused);
        cmd.Parameters.AddWithValue("@c", chatId.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

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
