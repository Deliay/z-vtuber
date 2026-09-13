using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;

namespace Z.Vtuber.Tests;

public class LiveEventPayloadParserTests
{
    [Fact]
    public void ToBsonDocument_ValidJsonObject_ReturnsDocument()
    {
        var result = LiveEventPayloadParser.ToBsonDocument("""{"info":["18","Hello",[123,"user"]]}""");

        Assert.Equal(BsonType.Document, result.BsonType);
        Assert.True(result.Contains("info"));
        Assert.Equal(BsonType.Array, result["info"].BsonType);
    }

    [Fact]
    public void ToBsonDocument_EmptyObject_ReturnsEmptyDocument()
    {
        var result = LiveEventPayloadParser.ToBsonDocument("{}");

        Assert.Equal(BsonType.Document, result.BsonType);
        Assert.Empty(result);
    }

    [Fact]
    public void ToBsonDocument_NullOrWhitespace_ReturnsEmptyDocument()
    {
        Assert.Equal(new BsonDocument(), LiveEventPayloadParser.ToBsonDocument(null!));
        Assert.Equal(new BsonDocument(), LiveEventPayloadParser.ToBsonDocument(""));
        Assert.Equal(new BsonDocument(), LiveEventPayloadParser.ToBsonDocument("  "));
    }

    [Fact]
    public void ToBsonDocument_NonObjectJson_WrapsInRaw()
    {
        var result = LiveEventPayloadParser.ToBsonDocument("[1,2,3]");

        Assert.True(result.Contains("raw"));
        Assert.Equal("[1,2,3]", result["raw"].AsString);
    }

    [Fact]
    public void ToBsonDocument_InvalidJson_WrapsInRaw()
    {
        var result = LiveEventPayloadParser.ToBsonDocument("not-json-at-all");

        Assert.True(result.Contains("raw"));
        Assert.Equal("not-json-at-all", result["raw"].AsString);
    }

    [Fact]
    public void ToBsonDocument_DanmakuSample_ParsesCorrectly()
    {
        var sample = """{"info":["18","Hello World",[403496,"Zero_Ash",123],[25,"小粉螈","枝堇Sumire"]]}""";
        var result = LiveEventPayloadParser.ToBsonDocument(sample);

        Assert.True(result.Contains("info"));
        var info = result["info"].AsBsonArray;
        Assert.Equal(4, info.Count);
        Assert.Equal("Hello World", info[1].AsString);
    }

    [Fact]
    public void ToBsonDocument_GiftSample_ParsesCorrectly()
    {
        var sample = """{"data":{"coin_type":"gold","discount_price":100,"price":100}}""";
        var result = LiveEventPayloadParser.ToBsonDocument(sample);

        Assert.True(result.Contains("data"));
        var data = result["data"].AsBsonDocument;
        Assert.Equal("gold", data["coin_type"].AsString);
        Assert.Equal(100, data["discount_price"].AsInt32);
    }

    [Fact]
    public void ToBsonDocument_GiftV2Sample_KeepsNestedRawString()
    {
        var sample = """{"data":{"coin_type":"gold"},"raw":"{\"coin_type\":\"gold\"}"}""";
        var result = LiveEventPayloadParser.ToBsonDocument(sample);

        Assert.True(result.Contains("data"));
        Assert.True(result.Contains("raw"));
        Assert.Equal(BsonType.String, result["raw"].BsonType);
    }

    [Fact]
    public void ToBsonDocument_SuperChatSample_ParsesCorrectly()
    {
        var sample = """{"data":{"message":"SC Message","uid":663620883,"user_info":{"uname":"lxl-lxl"},"medal_info":{"medal_name":"小粉螈"}}}""";
        var result = LiveEventPayloadParser.ToBsonDocument(sample);

        var data = result["data"].AsBsonDocument;
        Assert.Equal("SC Message", data["message"].AsString);
        Assert.Equal(663620883L, data["uid"].ToInt64());
    }

    [Theory]
    [InlineData("danmaku", """{"info":["18","Hello",[123,"user"]]}""")]
    [InlineData("gift", """{"data":{"coin_type":"gold","discount_price":100}}""")]
    [InlineData("super_chat", """{"data":{"message":"SC","uid":123}}""")]
    [InlineData("gift_v2", """{"data":{},"raw":"test"}""")]
    public void ToBsonDocument_AllEventTypes_ReturnsDocument(string type, string payload)
    {
        var result = LiveEventPayloadParser.ToBsonDocument(payload);
        Assert.Equal(BsonType.Document, result.BsonType);
    }

    [Fact]
    public void TryToBsonDocument_ValidJsonObject_ReturnsTrue()
    {
        var ok = LiveEventPayloadParser.TryToBsonDocument("""{"key":"value"}""", out var result);

        Assert.True(ok);
        Assert.Equal("value", result["key"].AsString);
    }

    [Fact]
    public void TryToBsonDocument_InvalidJson_ReturnsFalse()
    {
        var ok = LiveEventPayloadParser.TryToBsonDocument("not-json", out var result);

        Assert.False(ok);
        Assert.True(result.Contains("raw"));
        Assert.Equal("not-json", result["raw"].AsString);
    }

    [Fact]
    public void TryToBsonDocument_NonObjectJson_ReturnsFalse()
    {
        var ok = LiveEventPayloadParser.TryToBsonDocument("[1,2]", out var result);

        Assert.False(ok);
        Assert.True(result.Contains("raw"));
    }

    [Fact]
    public void TryToBsonDocument_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(LiveEventPayloadParser.TryToBsonDocument(null!, out _));
        Assert.False(LiveEventPayloadParser.TryToBsonDocument("", out _));
    }
}
