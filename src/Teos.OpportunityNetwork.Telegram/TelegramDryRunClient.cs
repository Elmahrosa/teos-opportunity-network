namespace Teos.OpportunityNetwork.Telegram;

/// <summary>
/// Never sends anything: records what WOULD have been sent and returns Ok, so the full
/// delivery pipeline (claim → format → mark SENT) runs end-to-end without a real bot.
/// The runtime warns before switching to it when TELEGRAM_BOT_TOKEN is unset.
/// Transport-only, like every Telegram client — no scoring/matching/dedup logic.
/// </summary>
public sealed class TelegramDryRunClient : ITelegramClient
{
    private readonly TextWriter _sink;
    private long _seq;

    public TelegramDryRunClient(TextWriter? sink = null) => _sink = sink ?? TextWriter.Null;

    public Task<TelegramSendResult> SendAsync(string chatId, string htmlText, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var id = $"dry-run-{Interlocked.Increment(ref _seq)}";
        _sink.WriteLine($"[dry-run] would send to chat {chatId}: {htmlText}");
        return Task.FromResult(new TelegramSendResult(true, id, "dry-run (no token)"));
    }
}