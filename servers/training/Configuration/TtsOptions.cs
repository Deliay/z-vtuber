using System.ComponentModel.DataAnnotations;

namespace Z.Vtuber.Training.Configuration;

/// <summary>
/// 上游 TTS 配置（节 <c>Tts</c>）——内网 index-tts-api（IndexTTS-2.5）
/// 「<c>https://tts.internal.fffdan.com</c>」，手写 multipart <c>POST /synthesize</c>。
/// </summary>
public sealed class TtsOptions
{
    public const string SectionName = "Tts";

    public const string DefaultBaseUrl = "https://tts.internal.fffdan.com";

    /// <summary>index-tts-api 内网地址。</summary>
    [Required]
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>合成请求超时（秒）；GPU 推理慢，默认 120s。</summary>
    [Range(1, 3600)]
    public int RequestTimeoutSeconds { get; set; } = 120;

    /// <summary>缺省语种（请求未带 <c>lang</c>）；白名单 ZH/EN/JA/ES/AR。</summary>
    [Required]
    public string DefaultLang { get; set; } = "ZH";

    /// <summary>缺省情绪强度 <c>emo_alpha</c>（无 <c>emotionControlWeight</c> 时）。</summary>
    [Range(0d, 1d)]
    public double DefaultEmoAlpha { get; set; } = 1.0;

    /// <summary>语速因子 <c>duration_factor</c>（&gt;1 变慢）。</summary>
    [Range(0.5d, 2.0d)]
    public double DurationFactor { get; set; } = 1.0;

    /// <summary>数字/日期归一化 <c>text_normalization</c>。</summary>
    public bool TextNormalization { get; set; } = true;

    /// <summary>文本分段长度 <c>max_text_tokens_per_segment</c>。</summary>
    [Range(1, 10000)]
    public int MaxTextTokensPerSegment { get; set; } = 120;

    /// <summary>分段间静音毫秒 <c>interval_silence</c>。</summary>
    [Range(0, 100000)]
    public int IntervalSilence { get; set; } = 200;
}
