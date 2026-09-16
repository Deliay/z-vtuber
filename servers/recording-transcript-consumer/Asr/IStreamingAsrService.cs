namespace Z.Vtuber.RecordingTranscript.Consumer.Asr;

public sealed record StreamingAsrSegment(long Start, long End, string Text);

public interface IStreamingAsrService
{
    IAsyncEnumerable<StreamingAsrSegment> TranscribeAsync(Stream stream, CancellationToken ct = default);
}
