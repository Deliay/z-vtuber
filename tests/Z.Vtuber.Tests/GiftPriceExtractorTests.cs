using Z.Vtuber.Crawler.Live;
using Z.Vtuber.Common.Live;

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

    [Fact]
    public void ExtractGoldSeeds_ReturnsDiscountPrice_WhenBothPriceAndDiscountPriceExist()
    {
        var payload = """{"data":{"price":200,"discount_price":150,"coin_type":"gold"}}""";
        
        Assert.Equal(150, GiftPriceExtractor.ExtractGoldSeeds("gift", payload));
    }

    [Fact]
    public void ExtractGoldSeeds_ReturnsPrice_WhenOnlyPriceExists()
    {
        var payload = """{"data":{"price":200,"coin_type":"gold"}}""";
        
        Assert.Equal(200, GiftPriceExtractor.ExtractGoldSeeds("gift", payload));
    }

    [Fact]
    public void ExtractGoldSeeds_ReturnsZero_WhenDiscountPriceIsNotNumber()
    {
        var payload = """{"data":{"price":200,"discount_price":"150","coin_type":"gold"}}""";
        
        Assert.Equal(200, GiftPriceExtractor.ExtractGoldSeeds("gift", payload));
    }

    [Fact]
    public void ExtractGoldSeeds_ReturnsZero_WhenBothPriceAndDiscountPriceAreInvalid()
    {
        var payload = """{"data":{"price":"200","discount_price":"150","coin_type":"gold"}}""";
        
        Assert.Equal(0, GiftPriceExtractor.ExtractGoldSeeds("gift", payload));
    }
}
