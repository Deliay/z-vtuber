using Z.Vtuber.Recording.Live;
using Z.Vtuber.Recording.Recording;
using Z.Vtuber.Recording.Services;

namespace Z.Vtuber.Tests;

public sealed class FakeRecordingCookieClient : IRecordingCookieClient
{
    public Func<CancellationToken, Task<CookieCheckout>>? CheckoutFunc { get; set; }
    public List<string> ReportedInvalid { get; } = [];

    public Task<CookieCheckout> CheckoutAsync(CancellationToken ct = default) =>
        CheckoutFunc?.Invoke(ct) ?? Task.FromResult(new CookieCheckout
        {
            CookieId = "rec-1",
            Cookie = "SESSDATA=fake",
        });

    public Task ReportInvalidAsync(string cookieId, CancellationToken ct = default)
    {
        ReportedInvalid.Add(cookieId);
        return Task.CompletedTask;
    }
}

public sealed class FakeBiliLiveApi : IBiliLiveApi
{
    public Func<long, LiveRoomInfo>? RoomInfoProvider { get; set; }
    public Func<long, IReadOnlyList<LiveStreamCandidate>>? StreamsProvider { get; set; }

    public Task<LiveRoomInfo> GetRoomInfoAsync(string cookie, long roomId, CancellationToken ct = default) =>
        Task.FromResult(RoomInfoProvider?.Invoke(roomId) ?? new LiveRoomInfo(roomId, "title", true));

    public Task<IReadOnlyList<LiveStreamCandidate>> GetStreamCandidatesAsync(
        string cookie, long roomId, CancellationToken ct = default) =>
        Task.FromResult(StreamsProvider?.Invoke(roomId)
            ?? [new LiveStreamCandidate("http://cdn/stream.flv", "http_stream", "flv", "avc")]);
}

public sealed class FakeRecordingLockProvider : IRecordingLockProvider
{
    public bool AcquireResult { get; set; } = true;
    public int AcquireCount;
    public Func<IRecordingLock, Task<bool>>? OwnershipCheck { get; set; }
    public RecordingLockHandle? LastHandle { get; private set; }

    public Task<IRecordingLock?> TryAcquireAsync(long roomId, CancellationToken ct = default)
    {
        Interlocked.Increment(ref AcquireCount);
        if (!AcquireResult)
        {
            return Task.FromResult<IRecordingLock?>(null);
        }

        LastHandle = new RecordingLockHandle(roomId, OwnershipCheck);
        return Task.FromResult<IRecordingLock?>(LastHandle);
    }
}

public sealed class RecordingLockHandle : IRecordingLock
{
    private readonly Func<IRecordingLock, Task<bool>>? _ownershipCheck;

    public RecordingLockHandle(long roomId, Func<IRecordingLock, Task<bool>>? ownershipCheck = null)
    {
        RoomId = roomId;
        _ownershipCheck = ownershipCheck;
    }

    public long RoomId { get; }
    public string Token { get; } = Guid.NewGuid().ToString("N");
    public bool Lost { get; private set; }
    public bool Released { get; private set; }

    public Task<bool> CheckOwnershipAsync(CancellationToken ct = default) =>
        _ownershipCheck is null ? Task.FromResult(true) : _ownershipCheck(this);

    public Task<bool> RenewAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task ReleaseAsync(CancellationToken ct = default)
    {
        Released = true;
        return Task.CompletedTask;
    }

    public void StartRenewal(CancellationToken ct)
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FakeRecordingProcessFactory : IRecordingProcessFactory
{
    public List<FfmpegCommand> StartedCommands { get; } = [];
    public Func<FfmpegCommand, FakeRecordingProcess>? ProcessFactory { get; set; }

    public IRecordingProcess Start(FfmpegCommand command)
    {
        StartedCommands.Add(command);
        return ProcessFactory?.Invoke(command) ?? new FakeRecordingProcess();
    }
}

public sealed class FakeRecordingProcess : IRecordingProcess
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeRecordingProcess(byte[]? audioBytes = null)
    {
        StandardOutput = new MemoryStream(audioBytes ?? []);
    }

    public Stream StandardOutput { get; }
    public bool HasExited { get; private set; }
    public bool Killed { get; private set; }

    public Task<int> WaitForExitAsync(CancellationToken ct) => _exit.Task;

    public void Kill()
    {
        Killed = true;
        Exit(137);
    }

    public void Exit(int code)
    {
        HasExited = true;
        _exit.TrySetResult(code);
    }

    public ValueTask DisposeAsync()
    {
        Kill();
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeRecordingSessionRunner : IRecordingSessionRunner
{
    public List<long> StartedRooms { get; } = [];
    public Func<long, string?, LiveRoomInfo, string, CancellationToken, Task>? RunFunc { get; set; }

    public Task RunAsync(long roomId, string? anchorName, LiveRoomInfo room, string cookie, CancellationToken ct)
    {
        StartedRooms.Add(roomId);
        return RunFunc?.Invoke(roomId, anchorName, room, cookie, ct) ?? Task.CompletedTask;
    }
}
