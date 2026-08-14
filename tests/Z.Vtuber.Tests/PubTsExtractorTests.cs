using System.Text.Json;
using Z.Vtuber.Services;

namespace Z.Vtuber.Tests;

public class PubTsExtractorTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void FromJsonElement_ReturnsPubTs_WhenPresent()
    {
        var element = Parse("""{"modules":{"module_author":{"pub_ts":1710000000}}}""");

        Assert.Equal(1710000000, PubTsExtractor.FromJsonElement(element));
    }

    [Fact]
    public void FromJsonElement_ReturnsNull_WhenMissing()
    {
        var element = Parse("""{"modules":{"module_author":{}}}""");

        Assert.Null(PubTsExtractor.FromJsonElement(element));
    }

    [Fact]
    public void FromJsonElement_ReturnsNull_WhenInvalidType()
    {
        var element = Parse("""{"modules":{"module_author":{"pub_ts":"not-a-number"}}}""");

        Assert.Null(PubTsExtractor.FromJsonElement(element));
    }

    [Fact]
    public void FromJsonElement_ReturnsNull_WhenStructureDiffers()
    {
        var element = Parse("""{"id_str":"123","desc":{}}""");

        Assert.Null(PubTsExtractor.FromJsonElement(element));
    }

    [Fact]
    public void FromRawJson_ReturnsNull_ForMalformedJson()
    {
        Assert.Null(PubTsExtractor.FromRawJson("not-json{"));
    }
}
