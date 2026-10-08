using Teos.OpportunityNetwork.Core.Domain;
using Teos.OpportunityNetwork.Ingestion;
using Xunit;

namespace Teos.OpportunityNetwork.Tests;

public class IngestionTests
{
    // ---- normalizer --------------------------------------------------------

    [Theory]
    [InlineData("  Build   a REST API.  ", "Build a REST API")]
    [InlineData("Line\nBreak\tTitle", "Line Break Title")]
    public void Titles_are_collapsed_and_trimmed(string input, string expected)
    {
        Assert.Equal(expected, Normalizer.NormalizeTitle(input));
    }

    [Fact]
    public void Urls_are_canonicalized_and_tracking_params_dropped()
    {
        var a = Normalizer.NormalizeUrl("http://example.com/jobs/1?utm_source=x&utm_medium=y&ref=gh");
        var b = Normalizer.NormalizeUrl("https://example.com/jobs/1");
        Assert.Equal(b, a);
        Assert.Null(Normalizer.NormalizeUrl("not a url"));
        Assert.Null(Normalizer.NormalizeUrl(null));
        Assert.Null(Normalizer.NormalizeUrl("   "));
    }

    [Theory]
    [InlineData("2026-10-01", 2026, 10, 1)]
    [InlineData("2026-10-01T12:30:00Z", 2026, 10, 1)]
    [InlineData("Oct 1, 2026", 2026, 10, 1)]
    public void Dates_normalize_to_UTC(string raw, int year, int month, int day)
    {
        var dt = Normalizer.NormalizeDate(raw);
        Assert.NotNull(dt);
        Assert.Equal(TimeSpan.Zero, dt!.Value.Offset); // always UTC
        Assert.Equal(new DateTime(year, month, day), dt.Value.UtcDateTime.Date);
    }

    [Fact]
    public void Garbage_dates_are_null_not_thrown()
    {
        Assert.Null(Normalizer.NormalizeDate("not-a-date"));
        Assert.Null(Normalizer.NormalizeDate(""));
        Assert.Null(Normalizer.NormalizeDate(null));
    }

    [Fact]
    public void Budgets_parse_and_unknown_stays_null()
    {
        var (min, max, cur, type) = Normalizer.NormalizeBudget("$500");
        Assert.Equal(500, min);
        Assert.Equal(500, max);
        Assert.Equal("USD", cur);
        Assert.Equal("fixed", type);

        var range = Normalizer.NormalizeBudget("$1,000 – $2,500 USD");
        Assert.Equal(1000, range.min);
        Assert.Equal(2500, range.max);
        Assert.Equal("range", range.budgetType);

        var unknown = Normalizer.NormalizeBudget("competitive");
        Assert.Null(unknown.min);
        Assert.Null(unknown.max);
        Assert.Equal("unknown", unknown.budgetType);

        var nothing = Normalizer.NormalizeBudget(null);
        Assert.Null(nothing.min);
        Assert.Equal("unknown", nothing.budgetType);
    }

    [Fact]
    public void Skills_normalize_deduplicate_and_sort_deterministically()
    {
        var a = Normalizer.NormalizeSkills(new[] { " C# ", "csharp", "C#", "PostgreSQL" });
        var b = Normalizer.NormalizeSkills(new[] { "postgresql", "csharp", "c#" });
        Assert.Equal(new[] { "c#", "csharp", "postgresql" }, a);
        Assert.Equal(a, b); // deterministic order regardless of input order
    }

    [Theory]
    [InlineData("HACKATHON", OpportunityType.HACKATHON)]
    [InlineData("bounty track", OpportunityType.BOUNTY)]
    [InlineData("full-time job", OpportunityType.JOB)]
    [InlineData("service contract", OpportunityType.SERVICE)]
    [InlineData("software license", OpportunityType.SOFTWARE)]
    [InlineData("internal project", OpportunityType.PROJECT)]
    public void Opportunity_types_normalize(string raw, OpportunityType expected)
    {
        Assert.Equal(expected, Normalizer.NormalizeType(raw));
    }

    [Fact]
    public void Content_hash_is_deterministic_and_content_addressed()
    {
        var h1 = Normalizer.ComputeContentHash("Title Here", "Acme", "https://example.com/a", OpportunityType.BOUNTY);
        var h2 = Normalizer.ComputeContentHash("  title   here ", "ACME", "http://example.com/a", OpportunityType.BOUNTY);
        var h3 = Normalizer.ComputeContentHash("Title Here", "Acme", "https://example.com/a", OpportunityType.JOB);

        Assert.Equal(h1, h2);    // same content → same hash
        Assert.NotEqual(h1, h3); // different type → different hash
        Assert.Equal(64, h1.Length);
        Assert.Matches("^[0-9a-f]{64}$", h1);
    }

    [Fact]
    public void Normalized_opportunity_has_utc_timestamps_and_valid_hash()
    {
        var raw = new RawOpportunityRecord
        {
            Source = " GitHub ",
            ExternalId = " node-1 ",
            Title = "  Fix   the bug ",
            Company = " acme ",
            Url = "http://example.com/i/1?utm_source=x",
            BudgetText = "$200-$400",
            Type = "BOUNTY",
            Skills = new[] { "C#", "c#" },
            PostedAt = "2026-09-01",
            Deadline = "2026-10-01"
        };

        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.FromHours(2)); // non-UTC input
        var opp = Normalizer.Normalize(raw, now);

        Assert.Equal("github", opp.Source);
        Assert.Equal("node-1", opp.ExternalId);
        Assert.Equal("Fix the bug", opp.Title);
        Assert.Equal("https://example.com/i/1", opp.Url);
        Assert.Equal(200, opp.BudgetMin);
        Assert.Equal(400, opp.BudgetMax);
        Assert.Equal(OpportunityType.BOUNTY, opp.OpportunityType);
        Assert.Equal(TimeSpan.Zero, opp.FirstSeenAt.Offset); // UTC-aware
        Assert.Equal(TimeSpan.Zero, opp.LastSeenAt.Offset);
        Assert.Equal(TimeSpan.Zero, opp.PostedAt!.Value.Offset);
        Assert.Equal(new[] { "c#" }, opp.SkillsRequired);
        Assert.Matches("^[0-9a-f]{64}$", opp.ContentHash);
    }

    // ---- deduplicator --------------------------------------------------------

    private sealed class InMemoryStore : IOpportunityStore
    {
        public List<Opportunity> Rows { get; } = new();

        public Task<Opportunity?> FindBySourceExternalAsync(string source, string externalId, CancellationToken ct) =>
            Task.FromResult(Rows.FirstOrDefault(o =>
                o.Source == source && o.ExternalId == externalId));

        public Task<Opportunity?> FindByContentHashAsync(string contentHash, CancellationToken ct) =>
            Task.FromResult(Rows.FirstOrDefault(o => o.ContentHash == contentHash));
    }

    private static Opportunity NewOpp(string source, string extId, string hash) => new()
    {
        Id = Guid.NewGuid(), Source = source, ExternalId = extId, Title = "T",
        ContentHash = hash, OpportunityType = OpportunityType.BOUNTY, Status = "OPEN"
    };

    [Fact]
    public async Task Exact_source_duplicate_is_authoritative()
    {
        var store = new InMemoryStore();
        var existing = NewOpp("github", "node-1", new string('a', 64));
        store.Rows.Add(existing);

        var dedup = new Deduplicator(store);
        var candidate = NewOpp("github", "node-1", new string('b', 64)); // same source+ext, different hash

        var result = await dedup.EvaluateAsync(candidate);

        Assert.Equal(DedupVerdict.SourceDuplicate, result.Verdict);
        Assert.Equal(existing.Id, result.ExistingOpportunityId);
    }

    [Fact]
    public async Task Content_hash_is_only_a_cross_source_candidate_signal()
    {
        var store = new InMemoryStore();
        var original = NewOpp("github", "node-1", new string('a', 64));
        store.Rows.Add(original);

        var dedup = new Deduplicator(store);
        var fromOtherSource = NewOpp("gitlab", "issue-9", new string('a', 64));

        var result = await dedup.EvaluateAsync(fromOtherSource);

        Assert.Equal(DedupVerdict.CrossSourceCandidate, result.Verdict);
        Assert.Equal(original.Id, result.ExistingOpportunityId);
        Assert.Contains("candidate", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Same_source_with_different_external_id_is_not_an_exact_duplicate()
    {
        var store = new InMemoryStore();
        store.Rows.Add(NewOpp("github", "node-1", new string('a', 64)));

        var dedup = new Deduplicator(store);
        var result = await dedup.EvaluateAsync(NewOpp("github", "node-2", new string('c', 64)));

        Assert.Equal(DedupVerdict.New, result.Verdict);
        Assert.Null(result.ExistingOpportunityId);
    }

    // ---- collector ------------------------------------------------------------

    [Fact]
    public async Task GitHub_collector_uses_search_issues_api_with_correct_headers()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"items":[{"id":1,"node_id":"I_x","title":"Fix login bug","body":"details","html_url":"https://github.com/acme/app/issues/7","created_at":"2026-09-01T00:00:00Z","labels":[{"name":"bounty"},{"name":"budget:$500"},{"name":"skill:csharp"}]}]}""")
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com") };
        var token = "ghs_example_token_value";

        var collector = new GitHubBountyCollector(http, token);
        var records = await collector.CollectAsync();

        // Correct endpoint (search/issues, not github.com)
        Assert.Contains("/search/issues", handler.LastRequestUri!.ToString());
        Assert.Contains("q=", handler.LastRequestUri.ToString());

        // Correct headers
        Assert.Equal("Bearer " + token, handler.LastAuthorization);
        Assert.Equal("application/vnd.github+json", handler.LastAccept);
        Assert.NotNull(handler.LastUserAgent);

        // Correct mapping
        var record = Assert.Single(records);
        Assert.Equal("github", record.Source);
        Assert.Equal("I_x", record.ExternalId);
        Assert.Equal("Fix login bug", record.Title);
        Assert.Equal("acme/app", record.Company);
        Assert.Equal("https://github.com/acme/app/issues/7", record.Url);
        Assert.Equal("BOUNTY", record.Type); // label:bounty → type hint
        Assert.Contains("skill:csharp", record.Skills!);
        Assert.Contains("budget:$500", record.Skills!); // label surfaced; budget extracted separately below
        Assert.Equal("$500", record.BudgetText);
    }

    [Fact]
    public void GitHub_collector_omits_authorization_header_without_token()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"items":[]}""")
        });
        var http = new HttpClient(handler);

        _ = new GitHubBountyCollector(http, token: null).CollectAsync().GetAwaiter().GetResult();

        Assert.Null(handler.LastAuthorization);
        Assert.Equal("application/vnd.github+json", handler.LastAccept);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public RecordingHandler(HttpResponseMessage response) => _response = response;

        public Uri? LastRequestUri { get; private set; }
        public string? LastAuthorization { get; private set; }
        public string? LastAccept { get; private set; }
        public string? LastUserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastAccept = request.Headers.Accept.ToString();
            LastUserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(_response);
        }
    }
}
