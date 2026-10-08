using System.Text.Json;

namespace Teos.OpportunityNetwork.Ingestion;

/// <summary>
/// Collects open bounty/hackathon issues from the GitHub Search API.
/// Transport concerns only: builds the request, maps JSON → RawOpportunityRecord.
/// The optional token is never logged or surfaced.
/// </summary>
public sealed class GitHubBountyCollector
{
    private static readonly Uri DefaultBase = new("https://api.github.com");

    private readonly HttpClient _http;
    private readonly string? _token;

    public GitHubBountyCollector(HttpClient http, string? token)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _token = token;
    }

    public async Task<IReadOnlyList<RawOpportunityRecord>> CollectAsync(CancellationToken ct = default)
    {
        var baseUri = _http.BaseAddress ?? DefaultBase;
        var uri = new Uri(baseUri, "search/issues?q=label%3Abounty%20is%3Aopen&per_page=50&sort=created&order=desc");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("User-Agent", "teos-opportunity-network/1.0");
        if (!string.IsNullOrWhiteSpace(_token))
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _token);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);

        var records = new List<RawOpportunityRecord>();
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return records;

        foreach (var item in items.EnumerateArray())
        {
            var labels = ReadLabels(item);
            var htmlUrl = GetString(item, "html_url");

            records.Add(new RawOpportunityRecord
            {
                Source = "github",
                ExternalId = GetString(item, "node_id") ?? string.Empty,
                Title = GetString(item, "title") ?? string.Empty,
                Description = GetString(item, "body"),
                Company = CompanyFromUrl(htmlUrl),
                Url = htmlUrl,
                PostedAt = GetString(item, "created_at"),
                Skills = labels,
                BudgetText = ReadBudgetText(labels),
                Type = TypeFromLabels(labels)
            });
        }

        return records;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string[] ReadLabels(JsonElement item)
    {
        if (!item.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return labels.EnumerateArray()
            .Select(l => l.TryGetProperty("name", out var n) ? n.GetString() : null)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToArray();
    }

    private static string? ReadBudgetText(string[] labels) =>
        labels.FirstOrDefault(l => l.StartsWith("budget:", StringComparison.OrdinalIgnoreCase))
            ?.Substring("budget:".Length).Trim();

    private static string TypeFromLabels(string[] labels)
    {
        if (labels.Any(l => l.Contains("hackathon", StringComparison.OrdinalIgnoreCase))) return "HACKATHON";
        if (labels.Any(l => l.Contains("bounty", StringComparison.OrdinalIgnoreCase))) return "BOUNTY";
        if (labels.Any(l => l.Contains("job", StringComparison.OrdinalIgnoreCase))) return "JOB";
        if (labels.Any(l => l.Contains("service", StringComparison.OrdinalIgnoreCase))) return "SERVICE";
        if (labels.Any(l => l.Contains("software", StringComparison.OrdinalIgnoreCase))) return "SOFTWARE";
        return "PROJECT";
    }

    private static string? CompanyFromUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 ? $"{segments[0]}/{segments[1]}" : null;
    }
}
