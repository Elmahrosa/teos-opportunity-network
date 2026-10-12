using Teos.OpportunityNetwork.Telegram;
using Xunit;

namespace Teos.OpportunityNetwork.Tests;

public class TelegramDryRunClientTests
{
    [Fact]
    public async Task Send_returns_ok_without_network()
    {
        var sink = new StringWriter();
        var client = new TelegramDryRunClient(sink);

        var result = await client.SendAsync("123", "<b>hello</b>");

        Assert.True(result.Ok);
        Assert.StartsWith("dry-run-", result.ProviderMessageId);
        var recorded = sink.ToString();
        Assert.Contains("123", recorded);
        Assert.Contains("<b>hello</b>", recorded);
    }
}