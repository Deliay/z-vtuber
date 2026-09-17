using System.Runtime.CompilerServices;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Consumer.Asr;
using Z.Vtuber.RecordingTranscript.Consumer.Audio;
using Z.Vtuber.RecordingTranscript.Consumer.Context;
using Z.Vtuber.RecordingTranscript.Consumer.Services;
using Z.Vtuber.RecordingTranscript.Consumer.Tasks;

namespace Z.Vtuber.Tests;

public sealed class FakeTranscriptTaskLock : ITranscriptTaskLock
{
    public ObjectId TaskId { get; set; } = ObjectId.GenerateNewId();
    public string Token { get; } = Guid.NewGuid().ToString("N");
    public bool Lost { get; private set; }
    public bool OwnershipResult { get; set; } = true;
    public bool Released { get; private set; }
    public int RenewalStarted;

    public Task<bool> CheckOwnershipAsync(CancellationToken ct = default)
    {
        if (!OwnershipResult)
        {
            Lost = true;
        }

        return Task.FromResult(OwnershipResult);
    }

    public Task<bool> RenewAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task ReleaseAsync(CancellationToken ct = default)
    {
        Released = true;
        return Task.CompletedTask;
    }

    public void StartRenewal(CancellationToken ct) => Interlocked.Increment(ref RenewalStarted);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FakeTranscriptTaskLockProvider : ITranscriptTaskLockProvider
{
    public bool AcquireResult { get; set; } = true;
    public HashSet<ObjectId> HeldIds { get; } = [];
    public int AcquireCount;
    public FakeTranscriptTaskLock? LastHandle { get; private set; }

    public Task<ITranscriptTaskLock?> TryAcquireAsync(ObjectId taskId, CancellationToken ct = default)
    {
        Interlocked.Increment(ref AcquireCount);
        if (!AcquireResult)
        {
            return Task.FromResult<ITranscriptTaskLock?>(null);
        }

        LastHandle = new FakeTranscriptTaskLock { TaskId = taskId };
        return Task.FromResult<ITranscriptTaskLock?>(LastHandle);
    }

    public Task<bool> IsHeldAsync(ObjectId taskId, CancellationToken ct = default) =>
        Task.FromResult(HeldIds.Contains(taskId));
}

public sealed class FakeStreamingAsrService : IStreamingAsrService
{
    private readonly IReadOnlyList<StreamingAsrSegment> _segments;
    private readonly Exception? _error;

    public FakeStreamingAsrService(IReadOnlyList<StreamingAsrSegment>? segments = null, Exception? error = null)
    {
        _segments = segments ?? [];
        _error = error;
    }

    public async IAsyncEnumerable<StreamingAsrSegment> TranscribeAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var segment in _segments)
        {
            ct.ThrowIfCancellationRequested();
            yield return segment;
            await Task.Yield();
        }

        if (_error is not null)
        {
            throw _error;
        }
    }
}

public sealed class FakeStreamingAsrServiceFactory : IStreamingAsrServiceFactory
{
    public List<long> Offsets { get; } = [];
    public List<string> ApiKeys { get; } = [];
    public List<TranscriptModelRecord> Models { get; } = [];
    public List<IReadOnlyList<string>> Contexts { get; } = [];
    public Func<long, IStreamingAsrService>? Provider { get; set; }

    public IStreamingAsrService Create(TranscriptModelRecord model, long offset, IReadOnlyList<string> context)
    {
        Models.Add(model);
        Offsets.Add(offset);
        ApiKeys.Add(model.ApiKey);
        Contexts.Add(context);
        return Provider?.Invoke(offset) ?? new FakeStreamingAsrService([]);
    }
}

public sealed class FakeTranscriptContextBuilder : ITranscriptContextBuilder
{
    public IReadOnlyList<string> Result { get; set; } = [];
    public List<RecordingRecord> Built { get; } = [];

    public Task<IReadOnlyList<string>> BuildAsync(RecordingRecord recording, CancellationToken ct = default)
    {
        Built.Add(recording);
        return Task.FromResult(Result);
    }
}

public sealed class FakeAudioSource : IAudioSource
{
    public Stream Audio { get; } = new MemoryStream();
    public bool Disposed { get; private set; }

    /// <summary>ffmpeg 退出码：0=正常结束，非 0=音源失败（应 FAILED）。</summary>
    public int ExitCode { get; set; }

    public Task<int> WaitForExitAsync(CancellationToken ct) => Task.FromResult(ExitCode);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeAudioSourceFactory : IAudioSourceFactory
{
    public List<(string Url, long Offset)> LiveCalls { get; } = [];
    public List<(string Url, long Position)> FileCalls { get; } = [];
    public List<FakeAudioSource> Sources { get; } = [];

    /// <summary>创建的音频源退出码（非 0 模拟 ffmpeg 失败）。</summary>
    public int ExitCode { get; set; }

    public IAudioSource CreateLive(string liveUrl, long offsetMs)
    {
        LiveCalls.Add((liveUrl, offsetMs));
        return Track(new FakeAudioSource());
    }

    public IAudioSource CreateFile(string fileUrl, long positionMs)
    {
        FileCalls.Add((fileUrl, positionMs));
        return Track(new FakeAudioSource());
    }

    private FakeAudioSource Track(FakeAudioSource source)
    {
        source.ExitCode = ExitCode;
        Sources.Add(source);
        return source;
    }
}

public sealed class FakeRecordingServiceClient : IRecordingServiceClient
{
    public LiveAudioProbe ProbeResult { get; set; } = LiveAudioProbe.NotLive;

    /// <summary>兼容既有测试：true→Live，false→NotLive。</summary>
    public bool LiveAvailable
    {
        get => ProbeResult == LiveAudioProbe.Live;
        set => ProbeResult = value ? LiveAudioProbe.Live : LiveAudioProbe.NotLive;
    }

    public string LiveUrlBase { get; set; } = "http://recording/api/stream/audio";
    public string FileUrlBase { get; set; } = "http://recording/api/file";

    public string BuildLiveAudioUrl(long roomId) => LiveUrlBase;

    public string BuildFileUrl(long roomId, string fileName) => $"{FileUrlBase}/{fileName}";

    public Task<LiveAudioProbe> ProbeLiveAudioAsync(long roomId, CancellationToken ct = default) =>
        Task.FromResult(ProbeResult);
}

public sealed class FakeTranscriptSegmentPublisher : ITranscriptSegmentPublisher
{
    public List<(ObjectId TaskId, StreamingAsrSegment Segment)> Published { get; } = [];

    public Task PublishAsync(ObjectId taskId, StreamingAsrSegment segment, CancellationToken ct = default)
    {
        Published.Add((taskId, segment));
        return Task.CompletedTask;
    }
}

public sealed class FakeTranscriptTaskRunner : ITranscriptTaskRunner
{
    public List<ObjectId> RunTaskIds { get; } = [];
    public List<TaskCompletionSource> Gates { get; } = [];
    public bool WaitForGate { get; set; }
    public Func<TranscriptTaskRecord, Task>? OnRun { get; set; }

    public async Task RunAsync(
        TranscriptTaskRecord task, ITranscriptTaskLock? handle = null, CancellationToken ct = default)
    {
        RunTaskIds.Add(task.Id);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Gates.Add(gate);

        if (OnRun is not null)
        {
            await OnRun(task);
        }

        if (WaitForGate)
        {
            await gate.Task.WaitAsync(ct);
        }
    }
}
