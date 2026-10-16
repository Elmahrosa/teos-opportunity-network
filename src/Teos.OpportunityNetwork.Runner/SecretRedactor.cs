namespace Teos.OpportunityNetwork.Runner;

/// <summary>
/// Replaces configured secret values with "[REDACTED]" in any text destined for logs or
/// stdout. This matters because the scheduled scanner runs on GitHub-hosted runners in a
/// PUBLIC repository, and Actions logs are world-readable.
///
/// Handles PostgreSQL connection strings specially: Npgsql timeout/error text often echoes
/// only the "Password=..." segment rather than the whole string, so both the full value and
/// password components are redacted. Over-redaction is safe; under-redaction is a leak.
/// </summary>
public static class SecretRedactor
{
    public const string Replacement = "[REDACTED]";

    /// <summary>Redacts every non-empty seed value (and Npgsql password segments) present in the message.</summary>
    public static string RedactSecrets(string? message, IEnumerable<string?> secrets)
    {
        if (string.IsNullOrEmpty(message)) return message ?? string.Empty;

        var result = message;
        foreach (var secret in secrets)
        {
            var value = secret?.Trim();
            if (string.IsNullOrEmpty(value)) continue;

            result = result.Replace(value, Replacement, StringComparison.Ordinal);
            result = RedactPasswordSegments(result, value);
        }
        return result;
    }

    /// <summary>
    /// Collects secret-ish values from the current process environment: DATABASE_URL plus any
    /// key containing TOKEN / API_KEY / SECRET / PASSWORD. Used on CLI exception paths where the
    /// runtime instance (with its explicit seed list) is not available.
    /// </summary>
    public static string[] FromCurrentEnvironment()
    {
        var seeds = new List<string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString();
            var value = entry.Value?.ToString();
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) continue;
            if (key.Equals("DATABASE_URL", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("API_KEY", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("SECRET", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase))
                seeds.Add(value);
        }
        return seeds.ToArray();
    }

    /// <summary>Redacts Password= segments of an Npgsql-style connection string (key=value;key=value).</summary>
    private static string RedactPasswordSegments(string text, string connectionString)
    {
        if (!connectionString.Contains('=', StringComparison.Ordinal)) return text;

        var result = text;
        foreach (var pair in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0 || idx >= pair.Length - 1) continue;

            var key = pair[..idx].Trim();
            if (!key.Equals("Password", StringComparison.OrdinalIgnoreCase)) continue;

            var value = pair[(idx + 1)..].Trim();
            result = result.Replace(pair, "Password=*", StringComparison.OrdinalIgnoreCase);
            result = result.Replace(value, Replacement, StringComparison.Ordinal);
        }
        return result;
    }
}