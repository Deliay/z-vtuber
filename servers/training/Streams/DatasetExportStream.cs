using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Clients;

namespace Z.Vtuber.Training.Streams;

/// <summary>
/// 数据集导出 zip 的**可读生成器流**：外部（如 <see cref="Stream.CopyToAsync(Stream, CancellationToken)"/>）
/// 通过 <see cref="ReadAsync(Memory{byte}, CancellationToken)"/> 拉取 zip 字节，生产在后台进行。
/// <para>
/// 关键点：<see cref="ZipArchive"/> 只提供同步写（BCL 无 async 打包 API），若直写
/// Kestrel 的 <c>Response.Body</c> 会触发 <c>Synchronous operations are disallowed</c>。
/// 本流把 ZipArchive 的同步写经 <see cref="Pipe"/> 桥接为异步读：生产端（后台任务）同步写 pipe，
/// 消费端（请求线程）异步读并写响应体，全程不对 Kestrel 做同步 IO，且内存受 pipe 背压约束
/// （O(缓冲块) 而非 O(数据集)），保持流式产出。
/// </para>
/// </summary>
public sealed class DatasetExportStream : Stream
{
    private readonly Pipe _pipe;
    private readonly Task _producer;
    private readonly UTF8Encoding _encoder = new(encoderShouldEmitUTF8Identifier: false);
    private bool _disposed;

    /// <summary>
    /// 依据条目与音频取数依赖启动后台生产任务。
    /// </summary>
    /// <param name="entries">条目列表（已按 CreatedAt、AudioKey 升序，与页面列表一致）。</param>
    /// <param name="audio">音频片段取数客户端（与试听同源）。</param>
    /// <param name="logger">跳过条目时的告警日志。</param>
    /// <param name="datasetId">日志用数据集 id。</param>
    /// <param name="ct">取消令牌（通常为 <c>HttpContext.RequestAborted</c>）。</param>
    public DatasetExportStream(
        IReadOnlyList<TrainingDatasetItemRecord> entries,
        IRecordingAudioClient audio,
        ILogger logger,
        string datasetId,
        CancellationToken ct = default)
    {
        _pipe = new Pipe(new PipeOptions(useSynchronizationContext: false));
        _producer = Task.Run(
            () => ProduceAsync(entries, audio, logger, datasetId, ct),
            CancellationToken.None);
    }

    /// <summary>成功写入 zip 的条目数（生产任务完成后读取）。</summary>
    public int Exported { get; private set; }

    /// <summary>因音频不可用或降级非 mp3 被跳过（不占编号）的条目数（生产任务完成后读取）。</summary>
    public int Skipped { get; private set; }

    /// <summary>后台生产任务完成信号；消费完成后可 await 以确保 <see cref="Exported"/>/<see cref="Skipped"/> 可见。</summary>
    public Task Completion => _producer;

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush()
    {
        // 只读生成器流：无写出可冲刷。
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var result = await _pipe.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var sequence = result.Buffer;

            if (!sequence.IsEmpty)
            {
                // 只消费 buffer.Length：未读完的余量留在 pipe 中，下次 ReadAsync 再次返回。
                var count = (int)Math.Min(buffer.Length, sequence.Length);
                sequence.Slice(0, count).CopyTo(buffer.Span);
                _pipe.Reader.AdvanceTo(sequence.GetPosition(count));
                return count;
            }

            _pipe.Reader.AdvanceTo(sequence.Start);
            if (result.IsCompleted)
            {
                return 0;
            }
        }
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 完成读端：使阻塞在 pipe 背压上的生产写端尽早结束（抛出并落入下面 catch）。
        _pipe.Reader.Complete();
        try
        {
            await _producer.ConfigureAwait(false);
        }
        catch
        {
            // 生产异常已在 ReadAsync 侧暴露或属取消；此处仅确保任务被观察。
        }

        _pipe.Writer.Complete();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// 后台生产：用 <see cref="ZipArchive"/> 同步写 pipe（<see cref="PipeWriter.AsStream"/> 的同步写会即时刷新
    /// 并受背压约束），逐条取音频、成功条目从 <c>000001</c> 起 <c>{D6}.mp3</c> + <c>{D6}.txt</c> 成对写入。
    /// </summary>
    private async Task ProduceAsync(
        IReadOnlyList<TrainingDatasetItemRecord> entries,
        IRecordingAudioClient audio,
        ILogger logger,
        string datasetId,
        CancellationToken ct)
    {
        try
        {
            var sink = _pipe.Writer.AsStream(leaveOpen: true);
            using (var zip = new ZipArchive(sink, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var item in entries)
                {
                    ct.ThrowIfCancellationRequested();

                    AudioClip clip;
                    try
                    {
                        clip = await audio.GetClipAsync(item.AudioUri, item.StartMs, item.EndMs, ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Skipped++;
                        logger.LogWarning(
                            "training dataset export skip: datasetId={DatasetId} audioKey={AudioKey} reason={Reason}",
                            datasetId, item.AudioKey, ex.Message);
                        continue;
                    }

                    // recording 片段端点缺 mp3 编码时降级 audio/wav，此类条目按「音频不可用」跳过，
                    // 不产生与 .mp3 扩展名不符的残缺文件（提案 §6.1 决策 4/5）。
                    if (!string.Equals(clip.ContentType, "audio/mpeg", StringComparison.OrdinalIgnoreCase))
                    {
                        Skipped++;
                        logger.LogWarning(
                            "training dataset export skip: datasetId={DatasetId} audioKey={AudioKey} reason={Reason}",
                            datasetId, item.AudioKey, $"content type {clip.ContentType}");
                        continue;
                    }

                    // 成功写入后才分配编号：失败条目跳过不占号，编号保持连续。
                    Exported++;
                    var stem = Exported.ToString("D6", CultureInfo.InvariantCulture);

                    var mp3 = zip.CreateEntry(stem + ".mp3");
                    await using (var entry = mp3.Open())
                    {
                        await entry.WriteAsync(clip.Bytes, ct).ConfigureAwait(false);
                    }

                    var txt = zip.CreateEntry(stem + ".txt");
                    await using (var entry = txt.Open())
                    {
                        // 容忍历史/上游脏数据 text=null：单条缺失文本不应击穿整个导出（写空 txt，保持配对）。
                        await entry.WriteAsync(_encoder.GetBytes(item.Text ?? string.Empty), ct)
                            .ConfigureAwait(false);
                    }
                }
            }

            await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _pipe.Writer.Complete();
        }
        catch (Exception ex)
        {
            _pipe.Writer.Complete(ex);
        }
    }
}
