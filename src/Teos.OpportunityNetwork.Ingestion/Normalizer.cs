using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Teos.OpportunityNetwork.Core.Domain;

namespace Teos.OpportunityNetwork.Ingestion;

/// <summary>
/// Deterministic normalization of raw source records into the canonical Opportunity shape.
/// Pure and side-effect free: same input always yields the same output.
/// </summary>
public static partial class Normalizer
{
    private static readonly string[] TrackingParams =
        { "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "ref", "fbclid", "gclid" };

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\d[\d,]*(?:\.\d+)?", RegexOptions.Compiled)]
    private static partial Regex Amount();

    public static string NormalizeTitle(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var collapsed = Whitespace().Replace(input, " ").Trim();
        // Drop trailing sentence punctuation that sources add inconsistently.
        return collapsed.TrimEnd('.', '…', '!', '?').Trim();
    }

    public static string? NormalizeUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;

        var kept = new List<string>();
        if (!string.IsNullOrEmpty(uri.Query))
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var key = pair.Split('=', 2)[0];
                if (TrackingParams.Contains(key, StringComparer.OrdinalIgnoreCase))
                    continue;
                kept.Add(pair);
            }
        }

        // Canonical form is always https and never carries tracking params.
        var builder = new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeHttps,
            Port = -1,
            Fragment = string.Empty,
            Query = kept.Count == 0 ? string.Empty : string.Join("&", kept)
        };
        return builder.Uri.AbsoluteUri;
    }

    public static DateTimeOffset? NormalizeDate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        if (!DateTimeOffset.TryParse(
                input.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
            return null;

        return parsed;
    }

    /// <summary>Parses free-text budgets. Unknown budgets are valid, never errors.</summary>
    public static (decimal? min, decimal? max, string? currency, string budgetType) NormalizeBudget(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return (null, null, null, "unknown");

        var text = input.Trim();
        var amounts = Amount().Matches(text)
            .Select(m => m.Value)
            .Select(v => v.Replace(",", "", StringComparison.Ordinal))
            .Select(v => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : (decimal?)null)
            .Where(d => d is not null)
            .Select(d => d!.Value)
            .ToList();

        if (amounts.Count == 0)
            return (null, null, null, "unknown");

        var currency = DetectCurrency(text);

        if (amounts.Count == 1)
            return (amounts[0], amounts[0], currency, "fixed");

        var min = amounts.Min();
        var max = amounts.Max();
        var budgetType = min == max ? "fixed" : "range";
        return (min, max, currency, budgetType);
    }

    private static string? DetectCurrency(string text)
    {
        if (text.Contains("USD", StringComparison.OrdinalIgnoreCase) || text.Contains('$'))
            return "USD";
        if (text.Contains("EUR", StringComparison.OrdinalIgnoreCase) || text.Contains('€'))
            return "EUR";
        if (text.Contains("GBP", StringComparison.OrdinalIgnoreCase) || text.Contains('£'))
            return "GBP";
        return null;
    }

    public static string[] NormalizeSkills(IEnumerable<string>? input)
    {
        if (input is null)
            return Array.Empty<string>();

        return input
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
    }

    public static OpportunityType NormalizeType(string? input)
    {
        var text = (input ?? string.Empty).Trim().ToLowerInvariant();

        if (text.Contains("hackathon", StringComparison.Ordinal)) return OpportunityType.HACKATHON;
        if (text.Contains("bounty", StringComparison.Ordinal)) return OpportunityType.BOUNTY;
        if (text.Contains("job", StringComparison.Ordinal) || text.Contains("full-time", StringComparison.Ordinal) ||
            text.Contains("full time", StringComparison.Ordinal)) return OpportunityType.JOB;
        if (text.Contains("service", StringComparison.Ordinal)) return OpportunityType.SERVICE;
        if (text.Contains("software", StringComparison.Ordinal) || text.Contains("license", StringComparison.Ordinal))
            return OpportunityType.SOFTWARE;
        if (text.Contains("project", StringComparison.Ordinal)) return OpportunityType.PROJECT;

        return OpportunityType.PROJECT;
    }

    public static string ComputeContentHash(string? title, string? company, string? url, OpportunityType type)
    {
        var canonical = string.Join('|',
            NormalizeTitle(title).ToLowerInvariant(),
            (company ?? string.Empty).Trim().ToLowerInvariant(),
            (NormalizeUrl(url) ?? string.Empty).ToLowerInvariant(),
            type.ToString());

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static Opportunity Normalize(RawOpportunityRecord raw, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var utcNow = now.ToUniversalTime();
        var title = NormalizeTitle(raw.Title);
        var company = string.IsNullOrWhiteSpace(raw.Company) ? null : raw.Company.Trim();
        var url = NormalizeUrl(raw.Url);
        var type = string.IsNullOrWhiteSpace(raw.Type)
            ? OpportunityType.PROJECT
            : NormalizeType(raw.Type);
        var (min, max, currency, budgetType) = NormalizeBudget(raw.BudgetText);

        return new Opportunity
        {
            Id = Guid.NewGuid(),
            Source = raw.Source.Trim().ToLowerInvariant(),
            ExternalId = raw.ExternalId.Trim(),
            Title = title,
            Company = company,
            Url = url,
            Description = raw.Description,
            Location = string.IsNullOrWhiteSpace(raw.Location) ? null : raw.Location.Trim(),
            OpportunityType = type,
            SkillsRequired = NormalizeSkills(raw.Skills),
            BudgetMin = min,
            BudgetMax = max,
            Currency = currency,
            BudgetType = budgetType,
            PostedAt = NormalizeDate(raw.PostedAt),
            Deadline = NormalizeDate(raw.Deadline),
            Status = "OPEN",
            ContentHash = ComputeContentHash(title, company, url, type),
            FirstSeenAt = utcNow,
            LastSeenAt = utcNow
        };
    }
}
