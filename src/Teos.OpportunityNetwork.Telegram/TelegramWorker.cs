using Teos.OpportunityNetwork.Core.Domain;
using Teos.OpportunityNetwork.Persistence;

namespace Teos.OpportunityNetwork.Telegram;

/// <summary>
/// Step 9 worker: pending_alerts() → claim → format → send → mark_sent/mark_failed.
///
/// Boundary guarantees (enforced by boundary tests):
///   - No matching, scoring, LLM, dedup, weights, or benchmark code is referenced.
///   - Reads persisted MatchResult data only via MatchRepository.PendingAlertsAsync.
///   - Never mutates opportunity_matches.
///
/// Delivery guarantee: at most one SENT record per (match, user, channel) — enforced by
/// the partial unique index + already_alerted double-check. Telegram itself cannot
/// guarantee exactly-once: a crash after the API accepts the message but before mark_sent
/// can produce one duplicate on retry. Application-level delivery is idempotent; this
/// system does not claim exactly-once.
/// </summary>
public sealed class TelegramWorker
{
    public sealed record Options(int MaxAttempts = 5);

    private readonly MatchRepository _matches;
    private readonly NotificationRepository _deliveries;
    private readonly ITelegramClient _client;
    private readonly Func<Guid, string> _resolveChatId;
    private readonly Options _options;

    /// <param name="resolveChatId">Maps user id → Telegram chat id (persisted chat identity lookup).</param>
    public TelegramWorker(
        MatchRepository matches,
        NotificationRepository deliveries,
        ITelegramClient client,
        Func<Guid, string> resolveChatId,
        Options? options = null)
    {
        _matches = matches ?? throw new ArgumentNullException(nameof(matches));
        _deliveries = deliveries ?? throw new ArgumentNullException(nameof(deliveries));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _resolveChatId = resolveChatId ?? throw new ArgumentNullException(nameof(resolveChatId));
        _options = options ?? new Options();
    }

    /// <summary>
    /// Runs one full pass over pending alerts (single-pass mode for tests/cron).
    /// Returns the number of successful deliveries.
    /// Safe to restart: state machine is persisted; claims use FOR UPDATE SKIP LOCKED.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var sent = 0;
        var pending = await _matches.PendingAlertsAsync(NotificationRepository.TelegramChannel, ct).ConfigureAwait(false);

        foreach (var alert in pending)
        {
            ct.ThrowIfCancellationRequested();

            // Idempotency double-check (the query anti-join already excluded SENT rows).
            if (await _matches.AlreadyAlertedAsync(alert.MatchId, alert.UserId, NotificationRepository.TelegramChannel, ct).ConfigureAwait(false))
                continue;

            // Integrity gate: never alert on a decision the domain does not recognize.
            // PendingAlertsAsync already filters ALERT; this is defense-in-depth against
            // corrupt rows and guarantees the notification layer never invents decisions.
            if (!Enum.TryParse<MatchDecision>(alert.Decision, ignoreCase: true, out var decision) ||
                decision != MatchDecision.ALERT)
                continue;

            // Reuse/create the delivery row, then claim it atomically (PENDING/FAILED → SENDING).
            var deliveryId = await _deliveries.CreateDeliveryAsync(alert.MatchId, alert.UserId, NotificationRepository.TelegramChannel, ct).ConfigureAwait(false);
            var claimed = await _deliveries.ClaimForSendAsync(
                _options.MaxAttempts,
                channel: NotificationRepository.TelegramChannel,
                ct: ct,
                deliveryId: deliveryId).ConfigureAwait(false);
            if (claimed is null) continue;

            // Format from persisted data only — never computes a score.
            string message;
            try
            {
                message = TelegramFormatter.Format(alert);
            }
            catch (Exception ex)
            {
                await _deliveries.MarkFailedAsync(claimed.Id, Sanitize(ex.Message), retryable: false, ct: ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                var chatId = _resolveChatId(alert.UserId);
                var result = await _client.SendAsync(chatId, message, ct).ConfigureAwait(false);
                if (result.Ok)
                {
                    await _deliveries.MarkSentAsync(claimed.Id,
                        result.ProviderMessageId ?? $"local-{Guid.NewGuid()}", ct).ConfigureAwait(false);
                    sent++;
                }
                else
                {
                    await _deliveries.MarkFailedAsync(claimed.Id, Sanitize(result.Description ?? "send failed"),
                        retryable: false, ct: ct).ConfigureAwait(false);
                }
            }
            catch (TelegramApiException ex)
            {
                await _deliveries.MarkFailedAsync(claimed.Id, Sanitize(ex.Message), ex.Retryable,
                    _options.MaxAttempts, ct: ct).ConfigureAwait(false);
            }
            catch (TelegramConfigException ex)
            {
                // Missing/misconfigured token: fail permanently, never leak config details.
                await _deliveries.MarkFailedAsync(claimed.Id, Sanitize(ex.Message), retryable: false, ct: ct).ConfigureAwait(false);
                break; // config error affects every send — stop the pass
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("no Telegram chat", StringComparison.Ordinal))
            {
                await _deliveries.MarkFailedAsync(claimed.Id, Sanitize(ex.Message), retryable: false, ct: ct).ConfigureAwait(false);
            }
        }

        return sent;
    }

    private static string Sanitize(string error) => TelegramBotClient.Redact(error);
}
