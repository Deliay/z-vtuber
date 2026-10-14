using System.ComponentModel.DataAnnotations;

namespace Z.Vtuber.Training.Configuration;

/// <summary>
/// 语音合成配置（节 <c>Speech</c>）——语种白名单与缺省语种（自原 <c>TtsOptions</c> 迁入）。
/// 语音模型经 Models 管理配置（功能点 <c>speech-synthesize</c> 解析），不再有独立的 TTS 上游地址。
/// </summary>
public sealed class SpeechOptions
{
    public const string SectionName = "Speech";

    /// <summary>语种白名单（大小写不敏感，不再依赖原 index-tts-api 语种集）。</summary>
    public static readonly string[] SupportedLangs = ["ZH", "EN", "JA", "ES", "AR"];

    /// <summary>缺省语种（请求未带 <c>lang</c>）；白名单 ZH/EN/JA/ES/AR。</summary>
    [Required]
    public string DefaultLang { get; set; } = "ZH";
}
