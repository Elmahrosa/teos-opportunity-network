namespace Teos.OpportunityNetwork.Runner;

/// <summary>
/// Minimal .env loader (KEY=VALUE, # comments, surrounding quotes stripped).
/// Values are loaded UNDER real environment variables so the environment always wins.
/// Loads nothing when the file is missing — never throws.
/// </summary>
public static class EnvFileLoader
{
    public static Dictionary<string, string?> Load(string path)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            if (key.Length == 0) continue;

            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                value = value[1..^1];

            result[key] = value;
        }
        return result;
    }
}