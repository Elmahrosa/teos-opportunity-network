using System.Net;
using System.Text;
using Teos.OpportunityNetwork.Persistence;

namespace Teos.OpportunityNetwork.Telegram;

/// <summary>
/// Pure formatter — no I/O, no scoring, no matching. Builds message text exclusively
/// from persisted data. The stored match score appears verbatim; nothing is recomputed.
/// Escapes for Telegram HTML parse mode and truncates safely below the 4096-char limit.
/// </summary>
public static class TelegramFormatter
{
    public const int TelegramLimit = 4096;
    // Reserve room for the truncation marker.
    public const int SafeLimit = TelegramLimit - 32;

    public static string Format(PendingAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var sb = new StringBuilder();
        sb.AppendLine($"<b>{WebUtility.HtmlEncode(alert.Title)}</b>");

        if (!string.IsNullOrWhiteSpace(alert.Company) || !string.IsNullOrWhiteSpace(alert.Source))
            sb.AppendLine($"Source: {WebUtility.HtmlEncode(alert.Company ?? alert.Source)}");

        sb.AppendLine($"Type: {WebUtility.HtmlEncode(alert.OpportunityType)}");

        // Stored score, verbatim — the formatter never recomputes or re-derives it.
        sb.AppendLine($"Match score: {alert.MatchScore:0.##} ({alert.Decision})");

        if (alert.MatchedSkills.Length > 0)
            sb.AppendLine($"Matched skills: {WebUtility.HtmlEncode(string.Join(", ", alert.MatchedSkills))}");
        if (alert.MissingSkills.Length > 0)
            sb.AppendLine($"Missing skills: {WebUtility.HtmlEncode(string.Join(", ", alert.MissingSkills))}");

        if (alert.BudgetMin is not null || alert.BudgetMax is not null)
        {
            var budget = alert.BudgetMin is not null && alert.BudgetMax is not null && alert.BudgetMin != alert.BudgetMax
                ? $"{alert.BudgetMin:N0}–{alert.BudgetMax:N0}"
                : (alert.BudgetMax ?? alert.BudgetMin)?.ToString("N0");
            sb.AppendLine($"Budget: {budget} {alert.Currency}".TrimEnd());
        }
        else
        {
            sb.AppendLine("Budget: not specified");
        }

        if (alert.Deadline is not null)
            sb.AppendLine($"Deadline: {alert.Deadline:yyyy-MM-dd} UTC");

        sb.AppendLine($"Action: {alert.RecommendedAction.Replace('_', ' ')}");

        if (!string.IsNullOrWhiteSpace(alert.Url))
            sb.AppendLine($"<a href=\"{WebUtility.HtmlEncode(alert.Url)}\">Open opportunity</a>");

        var text = sb.ToString().TrimEnd();
        return Truncate(text, SafeLimit);
    }

    internal static string Truncate(string text, int limit)
    {
        if (text.Length <= limit) return text;
        return text[..(limit - 1)] + "…";
    }
}
