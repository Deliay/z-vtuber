using System.Diagnostics;
using System.IO.Pipelines;
using Z.Vtuber.RecordingTranscript.Consumer.Configuration;

namespace Z.Vtuber.RecordingTranscript.Consumer.Audio;

/// <summary>ffmpeg 音频输出参数构造（纯函数，可测试）。</summary>
public static class FfmpegAudioArgs
{
    /// <summary>s16le 单声道 16kHz 的字节率。</summary>
    public const int BytesPerSecond = 16000 * 2;

    /// <summary>约 1 秒的读取块。</summary>
    public const int ChunkBytes = BytesPerSecond;

    public const string OutputPipe = "pipe:1";

    public static IReadOnlyList<string> Build(string inputUrl, bool live, long positionMs)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "warning" };
        if (!live)
        {
            args.AddRange(["-ss", $"{positionMs / 1000.0}s"]);
        }

        args.AddRange([
            "-i", inputUrl,
            "-vn",
            "-ac", "1",
            "-ar", "16000",
            "-f", "s16le",
            OutputPipe,
        ]);
        return args;
    }
}

public interface IAudioProcess : IAsyncDisposable
{
    Stream StandardOutput { get; }
    Task<int> WaitForExitAsync(CancellationToken ct);
    void Kill();
}

public interface IAudioProcessFactory
{
    IAudioProcess Start(string ffmpegPath, IReadOnlyList<string> args);
}

public sealed class FfmpegAudioProcessFactory(ILogger<FfmpegAudioProcessFactory> logger) : IAudioProcessFactory
{
    public IAudioProcess Start(string ffmpegPath, IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        return new FfmpegAudioProcess(process, logger);
    }

    private sealed class FfmpegAudioProcess(Process process, ILogger logger) : IAudioProcess
    {
        private readonly Task _stderrPump = PumpStderrAsync(process, logger);

        public Stream StandardOutput => process.StandardOutput.BaseStream;

        public Task<int> WaitForExitAsync(CancellationToken ct) =>
            process.WaitForExitAsync(ct).ContinueWith(_ => process.ExitCode, ct);

        public void Kill()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to kill ffmpeg audio process");
            }
        }

        public async ValueTask DisposeAsync()
        {
            Kill();
            try
            {
                await _stderrPump;
            }
            catch
            {
                // ignore
            }

            process.Dispose();
        }

        private static async Task PumpStderrAsync(Process process, ILogger logger)
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) is not null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        logger.LogDebug("ffmpeg(audio): {Line}", line);
                    }
                }
            }
            catch
            {
                // 进程退出/销毁时读取可能失败
            }
        }
    }
}

public interface IAudioSource : IAsyncDisposable
{
    Stream Audio { get; }
    Task<int> WaitForExitAsync(CancellationToken ct);
}

/// <summary>
/// 将 ffmpeg stdout（s16le 16kHz mono）经 System.IO.Pipelines 桥接为可读 Stream：
/// 约 1 秒分块写出；有界缓冲上限 maxBufferBytes（默认 60s 音频），满则暂停读 stdout 形成背压。
/// </summary>
public sealed class FfmpegAudioStream : IAudioSource
{
    private readonly IAudioProcess _process;
    private readonly Pipe _pipe;
    private readonly Task _producer;
    private readonly Stream _audio;

    public FfmpegAudioStream(IAudioProcess process, int maxBufferBytes)
    {
        _process = process;
        var pipeOptions = new PipeOptions(
            pauseWriterThreshold: maxBufferBytes,
            resumeWriterThreshold: Math.Max(maxBufferBytes / 2, FfmpegAudioArgs.ChunkBytes),
            minimumSegmentSize: FfmpegAudioArgs.ChunkBytes,
            useSynchronizationContext: false);
        _pipe = new Pipe(pipeOptions);
        _audio = _pipe.Reader.AsStream();
        _producer = Task.Run(ProduceAsync);
    }

    public Stream Audio => _audio;

    public async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        var exitCode = await _process.WaitForExitAsync(ct);
        await _producer;
        return exitCode;
    }

    private async Task ProduceAsync()
    {
        var buffer = new byte[FfmpegAudioArgs.ChunkBytes];
        try
        {
            int read;
            while ((read = await _process.StandardOutput.ReadAsync(buffer)) > 0)
            {
                await _pipe.Writer.WriteAsync(buffer.AsMemory(0, read));
                await _pipe.Writer.FlushAsync();
            }

            await _pipe.Writer.CompleteAsync();
        }
        catch (Exception ex)
        {
            await _pipe.Writer.CompleteAsync(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _pipe.Writer.CompleteAsync();
        }
        catch
        {
            // ignore
        }

        _pipe.Reader.Complete();
        _pipe.Writer.Complete();
        await _process.DisposeAsync();
        try
        {
            await _producer;
        }
        catch
        {
            // ignore
        }
    }
}

public interface IAudioSourceFactory
{
    IAudioSource CreateLive(string liveUrl, long offsetMs);
    IAudioSource CreateFile(string fileUrl, long positionMs);
}

public sealed class FfmpegAudioSourceFactory(
    TranscriptConsumerOptions options,
    IAudioProcessFactory processFactory) : IAudioSourceFactory
{
    public IAudioSource CreateLive(string liveUrl, long offsetMs) =>
        Create(liveUrl, live: true, positionMs: offsetMs);

    public IAudioSource CreateFile(string fileUrl, long positionMs) =>
        Create(fileUrl, live: false, positionMs: positionMs);

    private IAudioSource Create(string url, bool live, long positionMs)
    {
        var args = FfmpegAudioArgs.Build(url, live, positionMs);
        var process = processFactory.Start(options.FfmpegPath, args);
        var maxBufferBytes = options.AudioBufferSeconds * FfmpegAudioArgs.BytesPerSecond;
        return new FfmpegAudioStream(process, maxBufferBytes);
    }
}
