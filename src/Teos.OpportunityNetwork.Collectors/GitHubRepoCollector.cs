using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Teos.OpportunityNetwork.Core;

namespace Teos.OpportunityNetwork.Collectors
{
    /// <summary>
    /// Collects raw repository data from GitHub.
    /// </summary>
    public sealed class GitHubRepoCollector
    {
        private static readonly Uri DefaultBase = new("https://api.github.com");

        private readonly HttpClient _http;
        private readonly string? _token;

        public GitHubRepoCollector(HttpClient http, string? githubToken = null)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _token = githubToken;
        }

        public async Task<RawRepoData> CollectAsync(string repoUrl, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(repoUrl))
                throw new ArgumentException("Repository URL cannot be null or empty.", nameof(repoUrl));

            // Parse the repository URL to extract owner and repo name.
            // Expected format: https://github.com/{owner}/{repo}
            // We'll also handle URLs with trailing slashes or .git extension.
            var uri = new Uri(repoUrl);
            var segments = uri.AbsolutePath.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2 || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Invalid GitHub repository URL. Expected format: https://github.com/{owner}/{repo}", nameof(repoUrl));
            }

            string owner = segments[0];
            string repo = segments[1];
            // Remove .git extension if present
            if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                repo = repo.Substring(0, repo.Length - 4);

            var repoApiUri = new Uri(DefaultBase, $"repos/{owner}/{repo}");
            var languagesApiUri = new Uri(DefaultBase, $"repos/{owner}/{repo}/languages");

            // Fetch repository data
            var repoResponse = await SendGitHubRequestAsync(repoApiUri, ct).ConfigureAwait(false);
            var repoJson = await repoResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var repoDoc = JsonDocument.Parse(repoJson);
            var repoRoot = repoDoc.RootElement;

            // Fetch languages data
            var languagesResponse = await SendGitHubRequestAsync(languagesApiUri, ct).ConfigureAwait(false);
            var languagesJson = await languagesResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var languagesDoc = JsonDocument.Parse(languagesJson);
            var languagesRoot = languagesDoc.RootElement;

            var languages = new Dictionary<string, int>();
            if (languagesRoot.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in languagesRoot.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Number)
                    {
                        languages.Add(property.Name, property.Value.GetInt32());
                    }
                }
            }

            // Determine primary language (the one with the most bytes)
            string? primaryLanguage = null;
            int maxBytes = 0;
            foreach (var kvp in languages)
            {
                if (kvp.Value > maxBytes)
                {
                    maxBytes = kvp.Value;
                    primaryLanguage = kvp.Key;
                }
            }

            return new RawRepoData
            {
                Source = "github_repo",
                ExternalId = $"{owner}/{repo}",
                Url = repoUrl,
                Name = GetString(repoRoot, "name") ?? repo,
                Description = GetString(repoRoot, "description"),
                Stars = GetInt32(repoRoot, "stargazers_count"),
                Forks = GetInt32(repoRoot, "forks_count"),
                OpenIssues = GetInt32(repoRoot, "open_issues_count"),
                SizeKb = GetInt64(repoRoot, "size"),
                UpdatedAt = GetDateTime(repoRoot, "updated_at"),
                CreatedAt = GetDateTime(repoRoot, "created_at"),
                Language = primaryLanguage,
                Languages = languages,
                License = GetString(GetObject(repoRoot, "license"), "spdx_id")
            };
        }

        private async Task<HttpResponseMessage> SendGitHubRequestAsync(Uri uri, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("User-Agent", "TEOS-Opportunity-Network");
            if (!string.IsNullOrWhiteSpace(_token))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _token);
            }

            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            // Ensure success status code, but do not include the token in any exception message.
            if (!response.IsSuccessStatusCode)
            {
                // We deliberately do not include the token in the exception message.
                var reason = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"GitHub API request failed: {(int)response.StatusCode} {response.ReasonPhrase}. Reason: {reason}",
                    null,
                    response.StatusCode);
            }

            return response;
        }

        private static string? GetString(JsonElement? element, string propertyName)
        {
            if (element == null)
                return null;

            return element.Value.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static int GetInt32(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : 0;

        private static long GetInt64(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt64()
                : 0;

        private static DateTime GetDateTime(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                && DateTime.TryParse(value.GetString(), out var date)
                    ? date.ToUniversalTime()
                    : DateTime.MinValue;

        private static JsonElement? GetObject(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Object
                ? value
                : null;
    }
}