using System.ComponentModel.DataAnnotations;

namespace Z.Vtuber.RecordingTranscript.Api.Configuration;

public sealed class TranscriptApiOptions
{
    public const string SectionName = "TranscriptApi";

    [Range(1, 1000)]
    public int MaxPageSize { get; set; } = 200;
}
