using System.Globalization;
using Microsoft.Extensions.Configuration;
using Teos.OpportunityNetwork.Core.Domain;
using Teos.OpportunityNetwork.Ingestion;
using Teos.OpportunityNetwork.Matching;
using Teos.OpportunityNetwork.Persistence;
using Teos.OpportunityNetwork.Telegram;

namespace Teos.OpportunityNetwork.Runner;

public sealed record PipelineReport(
    int Collected,
    int New,
    int SourceDuplicates,
    int CrossSourceCandidates,
    int MatchesSaved,
    int DeliveriesSent,
    int Profiles);

/// <summary>
/// Step 10 composition root: Ingest → Normalize → Dedup → Match → Persist → Notify (Telegram).
///
/// Pure orchestration only — no scoring, normalization or dedup logic lives here; those stay
/// in their owning libraries (enforced by BoundaryTests). Telegram runs in DRY-RUN mode when
/// no bot token is configured, so the full delivery pass can be exercised without a real bot.
/// </summary>
public sealed class NotificationRuntime
{
    private readonly string _databaseUrl;
    private readonly string? _telegramBotToken;
    private readonly string? _githubToken;
    private readonly double _confidence;
    private readonly Func<HttpClient> _httpFactory;
    private readonly ITelegramClient? _clientOverride;
    private readonly ISemanticEvaluator? _semanticEvaluator;
    private readonly string _semanticMode;
    private readonly string[] _semanticWarnings;
    private readonly string[] _redactionSecrets;
    private readonly TextWriter _log;

    public NotificationRuntime(
        string databaseUrl,
        string? telegramBotToken = null,
        string? githubToken = null,
        string? openAiApiKey = null,
        double confidence = 0.9,
        Func<HttpClient>? httpFactory = null,
        ITelegramClient? clientOverride = null,
        TextWriter? log = null,
        IReadOnlyDictionary<string, string>? llmEnv = null)
    {
        _databaseUrl = databaseUrl ?? throw new ArgumentNullException(nameof(databaseUrl));
        _telegramBotToken = telegramBotToken;
        _githubToken = githubToken;
        _confidence = confidence;
        _httpFactory = httpFactory ?? (() => new HttpClient());
        _clientOverride = clientOverride;
        _log = log ?? Console.Out;

        var build = BuildSemantic(openAiApiKey, llmEnv, _httpFactory);
        _semanticEvaluator = build.Evaluator;
        _semanticMode = build.Mode;
        _semanticWarnings = build.Warnings ?? Array.Empty<string>();

        var seeds = new List<string?> { databaseUrl, telegramBotToken, githubToken };
        if (llmEnv is not null)
            seeds.AddRange(llmEnv
                .Where(kv => kv.Key.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Value));
        _redactionSecrets = seeds.Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToArray();
    }

    /// <summary>
    /// Composes the semantic evaluator: environment-configured ensemble first (free providers
    /// via <c>TEOS_LLM_PROVIDERS</c>), legacy single OpenAI provider when only
    /// <c>OPENAI_API_KEY</c> is present, or no evaluator (matcher falls back to neutral-50).
    /// </summary>
    private static SemanticEvaluatorBuild BuildSemantic(
        string? legacyOpenAiKey,
        IReadOnlyDictionary<string, string>? llmEnv,
        Func<HttpClient> httpFactory)
    {
        if (llmEnv is { Count: > 0 })
            return SemanticProviderFactory.Build(llmEnv, httpFactory);

        if (string.IsNullOrWhiteSpace(legacyOpenAiKey))
            return new SemanticEvaluatorBuild(null, "neutral-50", Array.Empty<string>());

        var openai = new SemanticProvider("openai", new ChatCompletionsSemanticEvaluator(httpFactory(), legacyOpenAiKey));
        return new SemanticEvaluatorBuild(new EnsembleSemanticEvaluator(new[] { openai }), "single:openai", Array.Empty<string>());
    }

    /// <summary>Builds the runtime from configuration (environment + optional .env).</summary>
    public static NotificationRuntime FromConfiguration(IConfiguration config, TextWriter? log = null)
    {
        var databaseUrl = config["DATABASE_URL"];
        if (string.IsNullOrWhiteSpace(databaseUrl))
            throw new InvalidOperationException(
                "DATABASE_URL is required for --once / --loop. Set it in the environment or a .env file.");

        string? Get(params string[] keys)
        {
            foreach (var key in keys)
            {
                var value = config[key];
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return null;
        }

        double confidence = 0.9;
        var confidenceRaw = config["TEOS_CONFIDENCE"];
        if (confidenceRaw is not null &&
            double.TryParse(confidenceRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedConfidence))
            confidence = parsedConfidence;

        // Semantic ensemble config lives in the same environment/.env namespace.
        var llmEnv = config.AsEnumerable()
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value) && IsLlmConfigKey(kv.Key))
            .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Value!, StringComparer.OrdinalIgnoreCase);

        return new NotificationRuntime(
            databaseUrl: databaseUrl,
            telegramBotToken: Get("TELEGRAM_BOT_TOKEN", "INPUT_TELEGRAM_BOT_TOKEN"),
            githubToken: Get("TEOS_GITHUB_TOKEN", "GITHUB_TOKEN"),
            openAiApiKey: Get("OPENAI_API_KEY"),
            confidence: confidence,
            log: log,
            llmEnv: llmEnv);
    }

    /// <summary>Keys the semantic provider factory understands (anything provider-shaped).</summary>
    private static bool IsLlmConfigKey(string key)
    {
        var k = key.ToUpperInvariant();
        return k == "TEOS_LLM_PROVIDERS" || k == "OPENAI_API_KEY" || k == "OPENAI_MODEL"
               || k.EndsWith("_BASE_URL", StringComparison.Ordinal)
               || k.EndsWith("_API_KEY", StringComparison.Ordinal)
               || k.EndsWith("_MODEL", StringComparison.Ordinal);
    }

    /// <summary>One full pipeline pass. Never catches exceptions — the caller decides exit codes.</summary>
    public async Task<PipelineReport> RunPipelineAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var http = _httpFactory();
        _log.WriteLine($"[runtime] pipeline start (t={now:O})");

        // ---- 1. Ingestion ---------------------------------------------------------
        IReadOnlyList<RawOpportunityRecord> raw;
        try
        {
            raw = await new GitHubBountyCollector(http, _githubToken).CollectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"ingestion (GitHub search) failed: {Sanitize(ex.Message)}", ex);
        }
        _log.WriteLine($"[runtime] ingested {raw.Count} raw records");

        // ---- 2. Normalize → Dedup → Persist opportunities --------------------------
        var oppRepo = new OpportunityRepository(_databaseUrl);
        var dedup = new Deduplicator(oppRepo);
        int fresh = 0, sourceDup = 0, cross = 0;
        foreach (var record in raw)
        {
            var candidate = Normalizer.Normalize(record, now);
            var verdict = await dedup.EvaluateAsync(candidate, ct).ConfigureAwait(false);
            switch (verdict.Verdict)
            {
                case DedupVerdict.New:
                    await oppRepo.UpsertAsync(candidate, ct).ConfigureAwait(false);
                    fresh++;
                    break;
                case DedupVerdict.SourceDuplicate:
                    sourceDup++;
                    break;
                case DedupVerdict.CrossSourceCandidate:
                    cross++;
                    _log.WriteLine($"[runtime] cross-source candidate needs review: {candidate.Source}/{candidate.ExternalId}");
                    break;
            }
        }
        _log.WriteLine($"[runtime] opportunities: {fresh} new, {sourceDup} source duplicates, {cross} cross-source candidates");

        // ---- 3. Match (HYBRID_V1; ensemble semantic from free providers, neutral-50 fallback) -------
        _log.WriteLine($"[runtime] semantic evaluator: {_semanticMode}");
        foreach (var warning in _semanticWarnings)
            _log.WriteLine($"[runtime] semantic config: {warning}");
        var profiles = await new UserProfileRepository(_databaseUrl).GetAllAsync(ct).ConfigureAwait(false);
        if (profiles.Count == 0)
            _log.WriteLine("[runtime] no user profiles found — skipping matching (seed profiles to enable it)");

        var matchesRepo = new MatchRepository(_databaseUrl);
        var matcher = new HybridMatcher(ScoringConfig.CreateHybridV1(), BuildEvaluator());
        int saved = 0;
        foreach (var opportunity in await oppRepo.GetOpenAsync(ct).ConfigureAwait(false))
        {
            foreach (var profile in profiles)
            {
                ct.ThrowIfCancellationRequested();

                // Version pair already scored → evidence is immutable, never re-scored.
                if (await matchesRepo.HasMatchAsync(opportunity.Id, profile.Id,
                        ScoringConfig.HybridV1, ScoringConfig.MatcherEngineV1, ct).ConfigureAwait(false))
                    continue;

                var (match, snapshot) = await matcher.MatchWithSnapshotAsync(opportunity, profile, _confidence, ct)
                    .ConfigureAwait(false);
                await matchesRepo.SaveAsync(match, snapshot, ct).ConfigureAwait(false);
                saved++;
            }
        }
        _log.WriteLine($"[runtime] matches persisted: {saved} new (profiles: {profiles.Count})");

        // ---- 4. Telegram delivery (dry-run without a token) --------------------------
        var deliveries = new NotificationRepository(_databaseUrl);
        var client = _clientOverride ?? BuildTelegramClient();
        var worker = new TelegramWorker(matchesRepo, deliveries, client, TelegramBot.ChatResolver(_databaseUrl));
        int sent;
        try
        {
            sent = await worker.RunOnceAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"telegram delivery pass failed: {Sanitize(ex.Message)}", ex);
        }
        _log.WriteLine($"[runtime] deliveries sent this pass: {sent}");

        return new PipelineReport(raw.Count, fresh, sourceDup, cross, saved, sent, profiles.Count);
    }

    /// <summary>Loop mode with graceful shutdown via the linked cancellation token.</summary>
    public async Task RunLoopAsync(TimeSpan interval, CancellationToken ct = default)
    {
        _log.WriteLine($"[runtime] loop started — interval {interval}");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var report = await RunPipelineAsync(ct).ConfigureAwait(false);
                _log.WriteLine($"[runtime] pass complete: collected {report.Collected}, new {report.New}, " +
                               $"matches {report.MatchesSaved}, sent {report.DeliveriesSent}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break; // graceful shutdown
            }
            catch (Exception ex)
            {
                _log.WriteLine($"[runtime] pass failed: {Sanitize(ex.Message)}");
            }

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
        _log.WriteLine("[runtime] loop stopped");
    }

    private ISemanticEvaluator? BuildEvaluator() => _semanticEvaluator;

    private ITelegramClient BuildTelegramClient()
    {
        if (string.IsNullOrWhiteSpace(_telegramBotToken))
        {
            _log.WriteLine("[runtime] TELEGRAM_BOT_TOKEN not set — running in DRY-RUN mode (no real sends)");
            return new TelegramDryRunClient(_log);
        }
        _log.WriteLine("[runtime] Telegram client: REAL (token configured — never logged)");
        // Own client: TelegramBotClient adjusts Timeout, which throws if the HttpClient already sent a request.
        return new TelegramBotClient(_httpFactory(), _telegramBotToken);
    }

    /// <summary>
    /// Redacts every configured secret (DB connection string, Telegram token, provider keys)
    /// from a message before it is logged or rethrown. <see cref="SecretRedactor"/> also
    /// strips Npgsql Password= segments, which the full connection-string redaction alone
    /// would miss.
    /// </summary>
    private string Sanitize(string message) => SecretRedactor.RedactSecrets(message, _redactionSecrets);
}