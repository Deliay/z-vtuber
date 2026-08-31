using Z.Vtuber.Configuration;

namespace Z.Vtuber.Tests;

public sealed class BilibiliCrawlerOptionsTests
{
    [Fact]
    public void GuardOptions_HaveExpectedDefaults()
    {
        var options = new BilibiliCrawlerOptions();

        Assert.Equal(23, options.GuardDailyCrawlHour);
        Assert.Equal(10, options.GuardCheckIntervalMinutes);
        Assert.Equal(2, options.GuardPageDelayMinSeconds);
        Assert.Equal(5, options.GuardPageDelayMaxSeconds);
        Assert.Equal(50, options.GuardMaxPages);
    }
}