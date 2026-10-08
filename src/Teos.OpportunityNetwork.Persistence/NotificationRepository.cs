using Npgsql;

namespace Teos.OpportunityNetwork.Persistence;

public enum DeliveryStatus
{
    PENDING,
    SENDING,
    SENT,
    FAILED
}

public sealed class NotificationDelivery
{
    public Guid Id { get; set; }
    public Guid MatchId { get; set; }
    public Guid UserId { get; set; }
    public string Channel { get; set; } = "telegram";
    public DeliveryStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Dedicated notification delivery persistence (Step 9).
/// Delivery state lives ONLY here — never in opportunity_matches.
/// Retry convention (documented): a FAILED row is reused on retry; attempt_count
/// is incremented on every send attempt; the same row moves PENDING/FAILED → SENDING → SENT/FAILED.
/// </summary>
public sealed class NotificationRepository
{
    public const string TelegramChannel = "telegram";
    private readonly string _conn;

    public NotificationRepository(string connectionString) =>
        _conn = connectionString ?? throw new ArgumentNullException(nameof(connectionString));

    /// <summary>True when a SENT delivery exists for this (match, user, channel).</summary>
    public async Task<bool> HasSuccessfulDeliveryAsync(Guid matchId, Guid userId, string channel = TelegramChannel, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(@"
            SELECT EXISTS (
                SELECT 1 FROM notification_deliveries
                WHERE match_id = @m AND user_id = @u AND channel = @c AND status = 'SENT')", conn);
        cmd.Parameters.AddWithValue("@m", matchId);
        cmd.Parameters.AddWithValue("@u", userId);
        cmd.Parameters.AddWithValue("@c", channel);
        return (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? false);
    }

    /// <summary>
    /// Creates the PENDING row if none exists (or reuses an existing non-SENT row).
    /// Returns the delivery id. Idempotent: never creates a second row while SENT exists.
    /// </summary>
    public async Task<Guid> CreateDeliveryAsync(Guid matchId, Guid userId, string channel = TelegramChannel, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);

        await using var select = new NpgsqlCommand(@"
            SELECT id FROM notification_deliveries
            WHERE match_id = @m AND user_id = @u AND channel = @c
            ORDER BY created_at
            FOR UPDATE
            LIMIT 1", conn, tx);
        select.Parameters.AddWithValue("@m", matchId);
        select.Parameters.AddWithValue("@u", userId);
        select.Parameters.AddWithValue("@c", channel);

        var existing = await select.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (existing is Guid existingId)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return existingId;
        }

        await using var insert = new NpgsqlCommand(@"
            INSERT INTO notification_deliveries (match_id, user_id, channel, status, attempt_count, created_at, updated_at)
            VALUES (@m, @u, @c, 'PENDING', 0, now(), now())
            RETURNING id", conn, tx);
        insert.Parameters.AddWithValue("@m", matchId);
        insert.Parameters.AddWithValue("@u", userId);
        insert.Parameters.AddWithValue("@c", channel);
        var id = (Guid)(await insert.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>
    /// Atomically claims one PENDING or retry-eligible FAILED row: moves it to SENDING
    /// and increments attempt_count in a single transaction using FOR UPDATE SKIP LOCKED
    /// so concurrent workers never claim the same row. Returns null when nothing is claimable.
    /// </summary>
    /// <param name="maxAttempts">Retry cap; FAILED rows at/above this are never claimed.</param>
    /// <param name="staleSendingCutoff">SENDING rows older than this are reclaimed (crashed worker).</param>
    public async Task<NotificationDelivery?> ClaimForSendAsync(
        int maxAttempts = 5,
        DateTimeOffset? staleSendingCutoff = null,
        string channel = TelegramChannel,
        CancellationToken ct = default,
        Guid? deliveryId = null)
    {
        var cutoff = staleSendingCutoff ?? DateTimeOffset.UtcNow.AddMinutes(-5);
        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(@"
            UPDATE notification_deliveries
            SET status = 'SENDING', attempt_count = attempt_count + 1, updated_at = now()
    WHERE id = (
        SELECT id FROM notification_deliveries
        WHERE channel = @c
          AND (@id IS NULL OR id = @id)
          AND (
                (status = 'PENDING')
                OR (status = 'FAILED' AND attempt_count < @max
                    AND (next_attempt_at IS NULL OR next_attempt_at <= now()))
                OR (status = 'SENDING' AND updated_at < @stale)
              )
        ORDER BY created_at
        FOR UPDATE SKIP LOCKED
        LIMIT 1)
    RETURNING id, match_id, user_id, channel, status, attempt_count,
              provider_message_id, last_error, next_attempt_at, created_at, sent_at, updated_at",
            conn, tx);
        cmd.Parameters.AddWithValue("@c", channel);
        cmd.Parameters.AddWithValue("@max", maxAttempts);
        cmd.Parameters.AddWithValue("@stale", cutoff);
        cmd.Parameters.AddWithValue("@id", (object?)deliveryId ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return null;
        }

        var claimed = ReadDelivery(reader);
        await reader.DisposeAsync().ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return claimed;
    }

    /// <summary>Records a successful send. The partial unique index enforces one SENT row per (match, user, channel).</summary>
    public async Task MarkSentAsync(Guid deliveryId, string providerMessageId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerMessageId))
            throw new ArgumentException("provider_message_id is required for SENT.", nameof(providerMessageId));

        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE notification_deliveries
            SET status = 'SENT', provider_message_id = @p, sent_at = now(),
                last_error = NULL, next_attempt_at = NULL, updated_at = now()
            WHERE id = @id AND status <> 'SENT'", conn);
        cmd.Parameters.AddWithValue("@p", providerMessageId);
        cmd.Parameters.AddWithValue("@id", deliveryId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a failed attempt with a sanitized error (callers sanitize before invoking).
    /// When retryable and attempts remain, next_attempt_at schedules the retry (exponential
    /// backoff: base 30s, doubling, capped at 15 minutes — default policy, configurable by caller).
    /// Non-retryable/permanent errors leave next_attempt_at NULL so the row is terminal.
    /// FAILED never blocks retry: claim_for_send picks these rows up again.
    /// </summary>
    public async Task MarkFailedAsync(Guid deliveryId, string sanitizedError, bool retryable, int maxAttempts = 5, int backoffBaseSeconds = 30, int backoffCapSeconds = 900, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var read = new NpgsqlCommand(
            "SELECT attempt_count FROM notification_deliveries WHERE id = @id FOR UPDATE", conn);
        read.Parameters.AddWithValue("@id", deliveryId);
        var attempts = await read.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (attempts is null) return;
        var attempt = (int)attempts;

        var canRetry = retryable && attempt < maxAttempts;
        DateTimeOffset? nextAttempt = null;
        if (canRetry)
        {
            var backoff = Math.Min(backoffBaseSeconds * Math.Pow(2, Math.Max(0, attempt - 1)), backoffCapSeconds);
            nextAttempt = DateTimeOffset.UtcNow.AddSeconds(backoff);
        }

        await using var cmd = new NpgsqlCommand(@"
            UPDATE notification_deliveries
            SET status = 'FAILED', last_error = @e, next_attempt_at = @next, updated_at = now()
            WHERE id = @id AND status <> 'SENT'", conn);
        cmd.Parameters.AddWithValue("@e", Truncate(sanitizedError, 2000));
        cmd.Parameters.AddWithValue("@next", (object?)nextAttempt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", deliveryId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reclaims SENDING rows stranded by a crashed worker so they can be retried.</summary>
    public async Task<int> ReclaimStaleSendingAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE notification_deliveries
            SET status = 'FAILED', last_error = coalesce(last_error, 'worker lease expired'),
                next_attempt_at = now(), updated_at = now()
            WHERE status = 'SENDING' AND updated_at < @cutoff", conn);
        cmd.Parameters.AddWithValue("@cutoff", olderThan);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// FAILED rows that are still retry-eligible: a retry was scheduled (next_attempt_at is set)
    /// and the attempt budget is not exhausted. Terminal rows (non-retryable, or attempts
    /// exhausted) have next_attempt_at NULL and are excluded. The backoff timestamp is advisory;
    /// ClaimForSendAsync is the authoritative gate.
    /// </summary>
    public async Task<IReadOnlyList<NotificationDelivery>> RetryableFailuresAsync(int maxAttempts = 5, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, match_id, user_id, channel, status, attempt_count,
                   provider_message_id, last_error, next_attempt_at, created_at, sent_at, updated_at
            FROM notification_deliveries
            WHERE status = 'FAILED' AND attempt_count < @max
              AND next_attempt_at IS NOT NULL
            ORDER BY created_at", conn);
        cmd.Parameters.AddWithValue("@max", maxAttempts);

        var rows = new List<NotificationDelivery>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add(ReadDelivery(reader));
        return rows;
    }

    /// <summary>All deliveries for a match (observability/tests).</summary>
    public async Task<IReadOnlyList<NotificationDelivery>> ForMatchAsync(Guid matchId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, match_id, user_id, channel, status, attempt_count,
                   provider_message_id, last_error, next_attempt_at, created_at, sent_at, updated_at
            FROM notification_deliveries WHERE match_id = @m ORDER BY created_at", conn);
        cmd.Parameters.AddWithValue("@m", matchId);

        var rows = new List<NotificationDelivery>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add(ReadDelivery(reader));
        return rows;
    }

    private static NotificationDelivery ReadDelivery(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        MatchId = reader.GetGuid(1),
        UserId = reader.GetGuid(2),
        Channel = reader.GetString(3),
        Status = Enum.Parse<DeliveryStatus>(reader.GetString(4)),
        AttemptCount = reader.GetInt32(5),
        ProviderMessageId = reader.IsDBNull(6) ? null : reader.GetString(6),
        LastError = reader.IsDBNull(7) ? null : reader.GetString(7),
        NextAttemptAt = reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(9),
        SentAt = reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
        UpdatedAt = reader.GetFieldValue<DateTimeOffset>(11)
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
