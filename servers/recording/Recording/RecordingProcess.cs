using System.Diagnostics;

namespace Z.Vtuber.Recording.Recording;

public interface IRecordingProcess : IAsyncDisposable
{
    Stream StandardOutput { get; }
    bool HasExited { get; }
    Task<int> WaitForExitAsync(CancellationToken ct);
    void Kill();
}

public interface IRecordingProcessFactory
{
    IRecordingProcess Start(FfmpegCommand command);
}

/// <summary>基于 System.Diagnostics.Process 的 ffmpeg 进程工厂。</summary>
public sealed class FfmpegRecordingProcessFactory(ILogger<FfmpegRecordingProcessFactory> logger) : IRecordingProcessFactory
{
    public IRecordingProcess Start(FfmpegCommand command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in command.Arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        return new FfmpegProcess(process, logger);
    }

    private sealed class FfmpegProcess(Process process, ILogger logger) : IRecordingProcess
    {
        private readonly Task _stderrPump = PumpStderrAsync(process, logger);

        public Stream StandardOutput => process.StandardOutput.BaseStream;

        public bool HasExited => process.HasExited;

        public Task<int> WaitForExitAsync(CancellationToken ct) => process.WaitForExitAsync(ct).ContinueWith(_ => process.ExitCode, ct);

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
                logger.LogDebug(ex, "Failed to kill ffmpeg process");
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
                        logger.LogDebug("ffmpeg: {Line}", line);
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
