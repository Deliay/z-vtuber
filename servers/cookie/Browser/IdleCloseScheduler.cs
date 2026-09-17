namespace Z.Vtuber.Cookie.Browser;

/// <summary>
/// per-key 空闲延时关闭调度器：每次 <see cref="Touch"/> 重置该 key 的到期时间；
/// 到期后调用关闭回调（异常仅记日志、不外抛、不影响其他 key）；<see cref="Cancel"/> 取消未触发的计时。
/// 回调在「到期且未被新 Touch 顺延」时被调用一次，并携带触发时的版本号供调用方二次校验。
/// </summary>
public sealed class IdleCloseScheduler : IAsyncDisposable
{
    private readonly TimeSpan _delay;
    private readonly Func<string, long, CancellationToken, Task> _onIdle;
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, CancellationTokenSource> _timers = new();
    private readonly Dictionary<string, long> _versions = new();

    public IdleCloseScheduler(
        TimeSpan delay,
        Func<string, long, CancellationToken, Task> onIdle,
        ILogger logger)
    {
        _delay = delay;
        _onIdle = onIdle;
        _logger = logger;
    }

    /// <summary>启动/重置 key 的空闲计时，返回本次计时的版本号。</summary>
    public long Touch(string key)
    {
        lock (_sync)
        {
            if (_timers.TryGetValue(key, out var previous))
            {
                previous.Cancel();
                previous.Dispose();
            }

            var version = _versions.TryGetValue(key, out var current) ? current + 1 : 1;
            _versions[key] = version;

            var cts = new CancellationTokenSource();
            _timers[key] = cts;

            _ = RunAsync(key, version, cts);
            return version;
        }
    }

    /// <summary>取消 key 未触发的空闲计时（若不存在则无操作）。</summary>
    public void Cancel(string key)
    {
        lock (_sync)
        {
            if (_timers.Remove(key, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }

            _versions.Remove(key);
        }
    }

    private async Task RunAsync(string key, long version, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(_delay, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_sync)
        {
            if (!_versions.TryGetValue(key, out var current) || current != version)
            {
                return;
            }

            if (!_timers.TryGetValue(key, out var timer) || !ReferenceEquals(timer, cts))
            {
                return;
            }

            _timers.Remove(key);
        }

        try
        {
            await _onIdle(key, version, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Idle close callback failed for {Key}", key);
        }
        finally
        {
            cts.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        List<CancellationTokenSource> timers;
        lock (_sync)
        {
            timers = [.. _timers.Values];
            _timers.Clear();
            _versions.Clear();
        }

        foreach (var cts in timers)
        {
            cts.Cancel();
            cts.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
