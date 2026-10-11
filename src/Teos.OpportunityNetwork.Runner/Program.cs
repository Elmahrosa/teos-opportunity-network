using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Teos.OpportunityNetwork.Collectors;
using Teos.OpportunityNetwork.Valuation;
using Teos.OpportunityNetwork.Ingestion;
using Teos.OpportunityNetwork.Core;
using Teos.OpportunityNetwork.Runner.Workers;

var argsList = Environment.GetCommandLineArgs();

if (argsList.Length > 1 && argsList[1] == "smoke")
{
    Console.WriteLine("=== TEOS Repo Valuation Smoke Test ===");
    try
    {
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        HttpClient http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "TEOS-Opportunity-Network");

        GitHubRepoCollector collector = token!= null
           ? new GitHubRepoCollector(http, token)
            : new GitHubRepoCollector(http);

        RawRepoData raw;
        if (!string.IsNullOrEmpty(token))
        {
            Console.WriteLine("Live mode: calling GitHub API...");
            raw = await collector.CollectAsync("https://github.com/Elmahrosa/teos-opportunity-network");
        }
        else
        {
            Console.WriteLine("Mock mode: using mock data (no GITHUB_TOKEN)");
            raw = new RawRepoData
            {
                Source = "github_repo",
                ExternalId = "Elmahrosa/teos-opportunity-network",
                Url = "https://github.com/Elmahrosa/teos-opportunity-network",
                Name = "teos-opportunity-network",
                Description = "TEOS Opportunity Network - Sovereign mesh",
                Stars = 12,
                Forks = 2,
                OpenIssues = 3,
                SizeKb = 2500,
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-2),
                CreatedAt = DateTimeOffset.UtcNow.AddMonths(-2),
                Language = "C#",
                Languages = new System.Collections.Generic.Dictionary<string,int>
                {
                    { "C#", 15000 },
                    { "JavaScript", 2000 }
                },
                License = "MIT"
            };
        }

        Console.WriteLine($"Collected: {raw.ExternalId} | Stars={raw.Stars} | Size={raw.SizeKb}KB | Langs={raw.Languages.Count}");

        var engine = new RepoValuationEngine();
        var valuation = engine.Evaluate(raw);

        Console.WriteLine($"Valuation: Score={valuation.Score}/100 | ${valuation.EstimatedLow:F0}-${valuation.EstimatedHigh:F0} | Avg=${valuation.EstimatedAvg:F0} | {valuation.Decision}/{valuation.Action}");
        Console.WriteLine($"Metrics: LOC={valuation.Metrics.EstimatedLoc} | Activity={valuation.Metrics.ActivityFactor} | Rep={valuation.Metrics.ReputationFactor:F2}");

        var opportunity = Normalizer.NormalizeRepo(raw, valuation, DateTimeOffset.UtcNow);

        Console.WriteLine($"Normalized: Type={opportunity.OpportunityType} | Title={opportunity.Title} | Budget=${opportunity.BudgetMin}-${opportunity.BudgetMax} | Hash={opportunity.ContentHash.Substring(0,16)}...");
        Console.WriteLine("=== Smoke Test PASSED ===");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Smoke Test FAILED: {ex.Message}");
        Console.WriteLine(ex.ToString());
        return 1;
    }
}

// --- Normal Runner Mode ---
var builder = Host.CreateApplicationBuilder(args);

// 1. Named HttpClient for Telegram
builder.Services.AddHttpClient("TelegramBotClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

// 2. ITelegramClient registration using named client
builder.Services.AddScoped<ITelegramClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = factory.CreateClient("TelegramBotClient");
    return new TelegramBotClient(httpClient);
});

// 3. Func<Guid,string> using ChatResolver
builder.Services.AddSingleton<Func<Guid, string>>(sp =>
{
    return (Guid id) => TelegramBot.ChatResolver.Resolve(id);
});

// 4. TelegramWorker as scoped + hosted
builder.Services.AddScoped<TelegramWorker>();
builder.Services.AddHostedService<TelegramWorker>(sp => sp.GetRequiredService<TelegramWorker>());

// 5. Other services...

var host = builder.Build();
await host.RunAsync();