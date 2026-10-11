using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Teos.OpportunityNetwork.Ingestion;
using Teos.OpportunityNetwork.Matching;
using Teos.OpportunityNetwork.Persistence;
using Teos.OpportunityNetwork.Telegram;
using Teos.OpportunityNetwork.Core.Domain;

namespace Teos.OpportunityNetwork.Runner
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var builder = Host.CreateDefaultBuilder(args);
            builder.ConfigureAppConfiguration((hostingContext, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                config.AddEnvironmentVariables();
            });
            builder.UseSerilog((hostingContext, loggerConfiguration) =>
            {
                loggerConfiguration.ReadFrom.Configuration(hostingContext.Configuration);
            });
            builder.ConfigureServices((hostContext, services) =>
            {
                // Register configuration options
                services.AddHttpClient("GitHubCollector")
                    .ConfigurePrimaryHttpMessageHandler(() =>
                    {
                        var handler = new HttpClientHandler();
                        // Don't automatically decompress response
                        handler.AutomaticDecompression = System.Net.DecompressionMethods.None;
                        return handler;
                    })
                    .AddTypedClient<GitHubBountyCollector>();
                services.AddHttpClient("TelegramBotClient")
                    .ConfigurePrimaryHttpMessageHandler(() =>
                    {
                        var handler = new HttpClientHandler();
                        // Don't automatically decompress response
                        handler.AutomaticDecompression = System.Net.DecompressionMethods.None;
                        return handler;
                    })
                    .AddTypedClient<TelegramBotClient>();
                services.AddSingleton<Normalizer>();
                services.AddScoped<MatchRepository>(sp =>
                {
                    var config = sp.GetRequiredService<IConfiguration>();
                    var connectionString = config.GetConnectionString("DefaultConnection");
                    if (string.IsNullOrEmpty(connectionString))
                    {
                        throw new InvalidOperationException('Connection string ''DefaultConnection'' not found.');
                    }
                    return new MatchRepository(connectionString);
                });
                services.AddScoped<NotificationRepository>(sp =>
                {
                    var config = sp.GetRequiredService<IConfiguration>();
                    var connectionString = config.GetConnectionString("DefaultConnection");
                    if (string.IsNullOrEmpty(connectionString))
                    {
                        throw new InvalidOperationException('Connection string ''DefaultConnection'' not found.');
                    }
                    return new NotificationRepository(connectionString);
                });
                services.AddSingleton<ScoringConfig>(sp =>
                {
                    var config = sp.GetRequiredService<IConfiguration>();
                    var weightsVersion = config["WeightsVersion"] ?? ScoringConfig.HybridV1;
                    ScoringConfig scoringConfig = weightsVersion switch
                    {
                        ScoringConfig.HybridV1 => ScoringConfig.CreateHybridV1(),
                        ScoringConfig.HybridV2 => ScoringConfig.CreateHybridV2(),
                        _ => throw new InvalidOperationException('Unknown weights version: ' + weightsVersion);
                    };
                    return scoringConfig;
                });
                services.AddScoped<HybridMatcher>(sp =>
                {
                    var scoringConfig = sp.GetRequiredService<ScoringConfig>();
                    var config = sp.GetRequiredService<IConfiguration>();
                    string? apiKey = config["OPENAI_API_KEY"];
                    ISemanticEvaluator? evaluator = null;
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        var httpClient = new HttpClient();
                        evaluator = new ChatCompletionsSemanticEvaluator(httpClient, apiKey);
                    }
                    return new HybridMatcher(scoringConfig, evaluator);
                });
                // Register ITelegramClient using the named HttpClient "TelegramBotClient"
                services.AddHttpClient<ITelegramClient, TelegramBotClient>("TelegramBotClient")
                    .ConfigurePrimaryHttpMessageHandler(() =>
                    {
                        var handler = new HttpClientHandler();
                        handler.AutomaticDecompression = System.Net.DecompressionMethods.None;
                        return handler;
                    });
                // Register the chat ID resolver function
                services.AddSingleton<Func<Guid, string>>(sp =>
                {
                    var config = sp.GetRequiredService<IConfiguration>();
                    var connectionString = config.GetConnectionString("DefaultConnection");
                    return TelegramBot.ChatResolver(connectionString);
                });
                // Register TelegramWorker
                services.AddScoped<TelegramWorker>();
                // Add the worker as a hosted service
                services.AddHostedService<TelegramWorker>();
            });

            var host = builder.Build();
            await host.RunAsync();
            return 0;
        }
    }
}