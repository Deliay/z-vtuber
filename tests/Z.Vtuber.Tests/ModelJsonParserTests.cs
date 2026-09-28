using Z.Vtuber.Training.Ai;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 1.5：<see cref="ModelJsonParser"/> 纯函数直测——围栏剥离、首个平衡对象、
/// 大小写不敏感、字段存在性校验与数值钳制。
/// </summary>
public class ModelJsonParserTests
{
    [Fact]
    public void ParseRecognizeText_StripsFencesAndTrailingJunk()
    {
        var raw = """
            ```json
            {"text":"你好世界"}
            ```
            """;

        Assert.Equal("你好世界", ModelJsonParser.ParseRecognizeText(raw));

        Assert.Equal("尾部杂文", ModelJsonParser.ParseRecognizeText("{\"text\":\"尾部杂文\"} 谢谢，希望有帮助"));
        Assert.Equal("无语言标注", ModelJsonParser.ParseRecognizeText("```{\"text\":\"无语言标注\"}```"));
    }

    [Fact]
    public void ParseRecognizeText_TakesFirstBalancedObjectWithNestedAndStringBraces()
    {
        var raw = "说明： {\"text\":\"a {b} c\",\"meta\":{\"nested\":{\"deep\":true}}} 之后的文字";

        Assert.Equal("a {b} c", ModelJsonParser.ParseRecognizeText(raw));
    }

    [Fact]
    public void ParseRecognizeText_IsCaseInsensitive()
    {
        Assert.Equal("大小写", ModelJsonParser.ParseRecognizeText("{\"TEXT\":\"大小写\"}"));
    }

    [Theory]
    [InlineData("这不是 JSON")]
    [InlineData("{ \"text\": 未加引号 }")]
    [InlineData("[]")]
    [InlineData("")]
    public void ParseRecognizeText_NonJson_ThrowsModelOutput(string raw)
    {
        Assert.Throws<ModelOutputException>(() => ModelJsonParser.ParseRecognizeText(raw));
    }

    [Fact]
    public void ParseRecognizeText_MissingText_ThrowsModelOutput()
    {
        Assert.Throws<ModelOutputException>(() => ModelJsonParser.ParseRecognizeText("{\"other\":\"x\"}"));
        Assert.Throws<ModelOutputException>(() => ModelJsonParser.ParseRecognizeText("{\"text\":123}"));
    }

    [Fact]
    public void ParseEmotion_FullObject_MapsAllFields()
    {
        var raw = """
            ```json
            {
              "emotionControlWeight": 0.6,
              "emotionDescription": "克制的失落",
              "happy": 0.05, "angry": 0.0, "sad": 0.7, "afraid": 0.05,
              "disgusted": 0.0, "melancholic": 0.8, "surprised": 0.1, "calm": 0.4
            }
            ```
            """;

        var emotion = ModelJsonParser.ParseEmotion(raw);

        Assert.Equal(0.6, emotion.EmotionControlWeight);
        Assert.Equal("克制的失落", emotion.EmotionDescription);
        Assert.Equal(0.05, emotion.Happy);
        Assert.Equal(0.7, emotion.Sad);
        Assert.Equal(0.8, emotion.Melancholic);
        Assert.Equal(0.4, emotion.Calm);
    }

    [Fact]
    public void ParseEmotion_MissingDescription_DefaultsToEmpty()
    {
        var emotion = ModelJsonParser.ParseEmotion(
            "{\"emotionControlWeight\":1,\"happy\":0,\"angry\":0,\"sad\":0,\"afraid\":0,\"disgusted\":0,\"melancholic\":0,\"surprised\":0,\"calm\":0}");

        Assert.Equal(string.Empty, emotion.EmotionDescription);
    }

    [Theory]
    [InlineData("{\"happy\":0.1}")] // 缺 emotionControlWeight
    [InlineData("{\"emotionControlWeight\":1,\"happy\":0,\"angry\":0,\"sad\":0,\"afraid\":0,\"disgusted\":0,\"melancholic\":0,\"surprised\":0}")] // 缺 calm
    [InlineData("{\"emotionControlWeight\":\"x\",\"happy\":0,\"angry\":0,\"sad\":0,\"afraid\":0,\"disgusted\":0,\"melancholic\":0,\"surprised\":0,\"calm\":0}")] // 非数值
    public void ParseEmotion_MissingOrNonNumericRequiredField_ThrowsModelOutput(string raw)
    {
        Assert.Throws<ModelOutputException>(() => ModelJsonParser.ParseEmotion(raw));
    }

    [Fact]
    public void ParseEmotion_ClampsDimensionsTo01_AndWeightToTen()
    {
        var emotion = ModelJsonParser.ParseEmotion(
            "{\"emotionControlWeight\":99,\"emotionDescription\":\"x\",\"happy\":2,\"angry\":-3,\"sad\":0.5,"
            + "\"afraid\":1.5,\"disgusted\":-0.1,\"melancholic\":0.9,\"surprised\":-1,\"calm\":3}");

        Assert.Equal(10, emotion.EmotionControlWeight);
        Assert.Equal(1, emotion.Happy);
        Assert.Equal(0, emotion.Angry);
        Assert.Equal(0.5, emotion.Sad);
        Assert.Equal(1, emotion.Afraid);
        Assert.Equal(0, emotion.Disgusted);
        Assert.Equal(0.9, emotion.Melancholic);
        Assert.Equal(0, emotion.Surprised);
        Assert.Equal(1, emotion.Calm);
    }

    [Fact]
    public void ClampAndClamp01_HandleNaNAsMin()
    {
        Assert.Equal(0, ModelJsonParser.Clamp01(double.NaN));
        Assert.Equal(2, ModelJsonParser.Clamp(double.NaN, 2, 5));
        Assert.Equal(5, ModelJsonParser.Clamp(9, 2, 5));
        Assert.Equal(2, ModelJsonParser.Clamp(-1, 2, 5));
        Assert.Equal(3.5, ModelJsonParser.Clamp(3.5, 2, 5));
    }

    // ---- 返工 #3/#4：越界钳制与描述超长需记 Warn，而非静默 ----

    [Fact]
    public void ParseEmotion_OutOfRangeAndLongDescription_LogsWarnings()
    {
        var logger = new ListLogger<ModelJsonParserTests>();
        var longDescription = new string('呵', 260);
        var raw = "{\"emotionControlWeight\":99,\"emotionDescription\":\"" + longDescription
            + "\",\"happy\":2,\"angry\":0,\"sad\":0.5,\"afraid\":0,\"disgusted\":0,"
            + "\"melancholic\":0.9,\"surprised\":0,\"calm\":0.5}";

        var emotion = ModelJsonParser.ParseEmotion(raw, logger);

        Assert.Equal(10, emotion.EmotionControlWeight);
        Assert.Equal(1, emotion.Happy);
        // 描述截断至 ≤ 200 字
        Assert.Equal(200, emotion.EmotionDescription.Length);
        Assert.True(logger.HasWarning);
        Assert.Contains(logger.Entries, e => e.Message.Contains("emotionControlWeight") && e.Message.Contains("out of range"));
        Assert.Contains(logger.Entries, e => e.Message.Contains("description too long"));
    }

    [Fact]
    public void ParseEmotion_InRange_LogsNoWarning()
    {
        var logger = new ListLogger<ModelJsonParserTests>();

        _ = ModelJsonParser.ParseEmotion(
            "{\"emotionControlWeight\":1,\"emotionDescription\":\"正常\",\"happy\":0.2,\"angry\":0.1,"
            + "\"sad\":0.1,\"afraid\":0.1,\"disgusted\":0.1,\"melancholic\":0.1,\"surprised\":0.1,\"calm\":0.1}",
            logger);

        Assert.False(logger.HasWarning);
    }
}
