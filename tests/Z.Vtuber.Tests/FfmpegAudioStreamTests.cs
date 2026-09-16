using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Z.Vtuber.RecordingTranscript.Consumer.Audio;

namespace Z.Vtuber.Tests;

public class FfmpegAudioStreamTests
{
    private static bool FfmpegAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                ArgumentList = { "-version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            process!.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public void BuildArgs_Live_FollowsWithoutSeek()
    {
        var args = FfmpegAudioArgs.Build("http://rec/api/1/stream/audio", live: true, positionMs: 1234);

        Assert.Equal(
            new[] { "-hide_banner", "-loglevel", "warning", "-i", "http://rec/api/1/stream/audio",
                "-vn", "-ac", "1", "-ar", "16000", "-f", "s16le", "pipe:1" },
            args);
    }

    [Fact]
    public void BuildArgs_File_SeeksToPosition()
    {
        var args = FfmpegAudioArgs.Build("http://rec/api/1/recordings/a.flv", live: false, positionMs: 1500);

        Assert.Equal("-ss", args[3]);
        Assert.Equal("1.5s", args[4]);
        Assert.Equal("http://rec/api/1/recordings/a.flv", args[6]);
        Assert.Equal("s16le", args[^2]);
        Assert.Equal("pipe:1", args[^1]);
    }

    [Fact]
    public void PcmConstants_RepresentOneSecondOf16kMonoS16le()
    {
        Assert.Equal(32000, FfmpegAudioArgs.BytesPerSecond);
        Assert.Equal(32000, FfmpegAudioArgs.ChunkBytes);
    }

    [Fact]
    public async Task FakeProcess_StreamsAllBytesThroughPipe()
    {
        var data = Enumerable.Range(0, 100_000).Select(i => (byte)(i % 251)).ToArray();
        var factory = new StaticProcessFactory(data);
        var process = factory.Start("ffmpeg", []);
        await using var stream = new FfmpegAudioStream(process, maxBufferBytes: FfmpegAudioArgs.ChunkBytes);

        using var buffer = new MemoryStream();
        await stream.Audio.CopyToAsync(buffer);

        Assert.Equal(data, buffer.ToArray());
    }

    [Fact]
    public async Task Ffmpeg_FileMode_SeeksAndProducesPcm()
    {
        if (!FfmpegAvailable())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "z-vtuber-audio-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "source.flv");
        try
        {
            await RunFfmpegAsync(
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                "-c:a", "aac", "-f", "flv", source);
            Assert.True(new FileInfo(source).Length > 0);

            var args = FfmpegAudioArgs.Build(source, live: false, positionMs: 1000);
            var process = new FfmpegAudioProcessFactory(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegAudioProcessFactory>.Instance)
                .Start("ffmpeg", args);
            await using var stream = new FfmpegAudioStream(process, FfmpegAudioArgs.BytesPerSecond);

            using var buffer = new MemoryStream();
            await stream.Audio.CopyToAsync(buffer);

            var bytes = buffer.Length;
            // 2s 源从 1s 处 seek：剩余约 1s（32000B），允许编码/解码误差
            Assert.InRange(bytes, 16000, 50000);
            Assert.Equal(0, bytes % 2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Ffmpeg_LiveMode_ChunkedHttp_FollowsAndEnds()
    {
        if (!FfmpegAvailable())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "z-vtuber-audio-http-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "source.flv");
        try
        {
            await RunFfmpegAsync(
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                "-c:a", "aac", "-f", "flv", source);
            var body = await File.ReadAllBytesAsync(source);

            await using var server = new ChunkedHttpServer(body, chunkSize: 4096, delayMilliseconds: 20);
            var args = FfmpegAudioArgs.Build(server.Url, live: true, positionMs: 0);
            var process = new FfmpegAudioProcessFactory(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegAudioProcessFactory>.Instance)
                .Start("ffmpeg", args);
            await using var stream = new FfmpegAudioStream(process, FfmpegAudioArgs.BytesPerSecond);

            using var buffer = new MemoryStream();
            await stream.Audio.CopyToAsync(buffer);

            Assert.True(buffer.Length > 0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task RunFfmpegAsync(params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class StaticProcessFactory(byte[] data) : IAudioProcessFactory
    {
        public IAudioProcess Start(string ffmpegPath, IReadOnlyList<string> args) => new StaticProcess(data);

        private sealed class StaticProcess(byte[] data) : IAudioProcess
        {
            public Stream StandardOutput { get; } = new MemoryStream(data, writable: false);

            public Task<int> WaitForExitAsync(CancellationToken ct) => Task.FromResult(0);

            public void Kill()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ChunkedHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly byte[] _body;
        private readonly int _chunkSize;
        private readonly int _delayMilliseconds;
        private readonly Task _loop;

        public ChunkedHttpServer(byte[] body, int chunkSize, int delayMilliseconds)
        {
            _body = body;
            _chunkSize = chunkSize;
            _delayMilliseconds = delayMilliseconds;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/stream/audio";
            _loop = Task.Run(ServeAsync);
        }

        public string Url { get; }

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                var stream = client.GetStream();
                await ReadHeadersAsync(stream);

                var header = "HTTP/1.1 200 OK\r\n" +
                             "Content-Type: video/x-flv\r\n" +
                             "Transfer-Encoding: chunked\r\n" +
                             "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));

                for (var offset = 0; offset < _body.Length; offset += _chunkSize)
                {
                    var length = Math.Min(_chunkSize, _body.Length - offset);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"{length:x}\r\n"));
                    await stream.WriteAsync(_body.AsMemory(offset, length));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"));
                    await stream.FlushAsync();
                    await Task.Delay(_delayMilliseconds, _cts.Token);
                }

                await stream.WriteAsync(Encoding.ASCII.GetBytes("0\r\n\r\n"));
                await stream.FlushAsync();
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch
            {
                // client disconnect
            }
        }

        private static async Task ReadHeadersAsync(NetworkStream stream)
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                var read = await stream.ReadAsync(one);
                if (read == 0)
                {
                    return;
                }

                bytes.Add(one[0]);
                var count = bytes.Count;
                if (count >= 4
                    && bytes[count - 4] == 13 && bytes[count - 3] == 10
                    && bytes[count - 2] == 13 && bytes[count - 1] == 10)
                {
                    return;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try
            {
                _listener.Stop();
            }
            catch
            {
                // ignore
            }

            try
            {
                await _loop;
            }
            catch
            {
                // ignore
            }

            _cts.Dispose();
        }
    }
}
