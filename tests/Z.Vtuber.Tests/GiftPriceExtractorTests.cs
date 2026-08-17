using Z.Vtuber.Live;

namespace Z.Vtuber.Tests;

public class GiftPriceExtractorTests
{
    [Fact]
    public void ExtractGoldSeeds_ReturnsZero_WhenTypeIsNotGift()
    {
        var payload = """{"data":{"price":100,"coin_type":"gold"}}""";
        
        Assert.Equal(0, GiftPriceExtractor.ExtractGoldSeeds("danmaku", payload));
    }

    [Fact]
    public void ExtractGoldSeeds_ReturnsPrice_WhenTypeIsGiftAndCoinTypeIsGold()
    {
        var payload = """{"data":{"price":100,"coin_type":"gold"}}""";
        
        Assert.Equal(100, GiftPriceExtractor.ExtractGoldSeeds("gift", payload));
    }

    [Fact]
    public void ExtractGoldSeeds_ReturnsZero_WhenTypeIsGiftAndCoinTypeIsSilver()
    {
        var payload = """{"data":{"price":100,"coin_type":"silver"}}""";
        
        Assert.Equal(0, GiftPriceExtractor.ExtractGoldSeeds("gift", payload));
    }

    [Fact]
    public void ExtractGoldSeeds_ReturnsZero_WhenPayloadIsInvalidJson()
    {
        Assert.Equal(0, GiftPriceExtractor.ExtractGoldSeeds("gift", "invalid json"));
    }

    [Fact]
    public void ExtractGoldSeeds_ReturnsZero_WhenDataPropertyIsMissing()
    {
        var payload = """{"price":100,"coin_type":"gold"}""";
        
        Assert.Equal(0, GiftPriceExtractor.ExtractGoldSeeds("gift", payload));
    }

    [Fact]
    public void ExtractGoldSeeds_ReturnsZero_WhenPricePropertyIsMissing()
    {
        var payload = """{"data":{"coin_type":"gold"}}""";
        
        Assert.Equal(0, GiftPriceExtractor.ExtractGoldSeeds("gift", payload));
    }

    [Fact]
    public void ExtractGoldSeeds_ReturnsZero_WhenCoinTypePropertyIsMissing()
    {
        var payload = """{"data":{"price":100}}""";
        
        Assert.Equal(0, GiftPriceExtractor.ExtractGoldSeeds("gift", payload));
    }

    [Fact]
    public void ExtractGoldSeeds_ReturnsZero_WhenPriceIsNotNumber()
    {
        var payload = """{"data":{"price":"100","coin_type":"gold"}}""";
        
        Assert.Equal(0, GiftPriceExtractor.ExtractGoldSeeds("gift", payload));
    }

    [Fact]
    public void ExtractGoldSeeds_ReturnsZero_WhenDataIsNotObject()
    {
        var payload = """{"data":"invalid"}""";
        
        Assert.Equal(0, GiftPriceExtractor.ExtractGoldSeeds("gift", payload));
    }

    [Fact]
    public void ExtractGoldSeeds_ReturnsZero_WhenPayloadIsEmpty()
    {
        Assert.Equal(0, GiftPriceExtractor.ExtractGoldSeeds("gift", "{}"));
    }
}
