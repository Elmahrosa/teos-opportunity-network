using Teos.OpportunityNetwork.Core.Domain;
using Teos.OpportunityNetwork.Persistence;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Teos.OpportunityNetwork.Telegram;

/// <summary>
/// Step 9 worker: pending_alerts() → claim → format → send → mark_sent/mark_failed.
/// </summary>
public sealed class TelegramWorker : IHostedService
{
    private readonly MatchRepository _matches;
    private readonly NotificationRepository _deliveries;
    private readonly ITelegramClient _client;
    private readonly Func<Guid, string> _resolveChatId;
    private readonly Options _options;
    private Timer _timer;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(30); // run every 30 seconds

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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(async _ => await RunOnceAsync(cancellationToken), null, TimeSpan.Zero, _interval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
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

    public sealed record Options(int MaxAttempts = 5);

    private static string Sanitize(string error) => TelegramBotClient.Redact(error);
}