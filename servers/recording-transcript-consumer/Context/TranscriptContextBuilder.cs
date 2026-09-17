using System.Text.Json.Nodes;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.RecordingTranscript.Consumer.Context;

public interface ITranscriptContextBuilder
{
    Task<IReadOnlyList<string>> BuildAsync(RecordingRecord recording, CancellationToken ct = default);
}

/// <summary>
/// 识别上下文组装：按优先级汇集主播名 → 最新一天舰长名单 → 当场 SC 用户名/粉丝牌 → SC 内容，
/// 以 HashSet 语义去重并按 400 字符上限整行截断。任一类查询失败/为空时降级跳过，不影响其余。
/// </summary>
public sealed class TranscriptContextBuilder(
    AccountBasicInfoRepository accountInfo,
    LiveGuardRepository liveGuards,
    LiveEventTextIndexRepository liveEventTextIndex,
    ILogger<TranscriptContextBuilder> logger) : ITranscriptContextBuilder
{
    /// <summary>FunASR input.context 单轮文本上限（字符）。</summary>
    public const int MaxContextChars = 400;

    /// <summary>单次上下文组装最多读取的 SC 条数（截断前上限）。</summary>
    public const int MaxScRecords = 500;

    public async Task<IReadOnlyList<string>> BuildAsync(RecordingRecord recording, CancellationToken ct = default)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var trimmed = value.Trim();
            if (seen.Add(trimmed))
            {
                ordered.Add(trimmed);
            }
        }

        // 1) 主播名（最高优先级）
        Add(await TryGetAnchorNameAsync(recording.Mid, ct));

        // 2) 最新一天舰长快照的舰长名
        foreach (var guardName in await TryGetGuardNamesAsync(recording.RoomId, ct))
        {
            Add(guardName);
        }

        // 3) 窗口内 SC：先用户名/粉丝牌交替，再 SC 内容
        var superChats = await TryGetSuperChatsAsync(recording, ct);
        foreach (var sc in superChats)
        {
            Add(sc.SenderName);
            Add(sc.SenderMedal);
        }

        foreach (var sc in superChats)
        {
            Add(sc.Text);
        }

        return Truncate(ordered, MaxContextChars);
    }

    /// <summary>按添加顺序逐行累加，累计将超上限时停止追加当前及后续优先级的行（不产生半截行）。</summary>
    internal static IReadOnlyList<string> Truncate(IReadOnlyList<string> ordered, int maxChars)
    {
        var result = new List<string>();
        var total = 0;
        foreach (var line in ordered)
        {
            var added = line.Length + (result.Count > 0 ? 1 : 0);
            if (total + added > maxChars)
            {
                break;
            }

            result.Add(line);
            total += added;
        }

        return result;
    }

    private async Task<string?> TryGetAnchorNameAsync(long mid, CancellationToken ct)
    {
        if (mid <= 0)
        {
            return null;
        }

        try
        {
            var record = await accountInfo.GetLatestByMidAsync(mid, ct);
            return ExtractUName(record?.Data);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to query anchor name for mid {Mid}; continuing without it", mid);
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> TryGetGuardNamesAsync(long roomId, CancellationToken ct)
    {
        try
        {
            var snapshot = await liveGuards.GetLatestSnapshotByRoomIdAsync(roomId, ct);
            if (snapshot is null || snapshot.Guards.Count == 0)
            {
                return [];
            }

            return snapshot.Guards
                .Select(g => g.UserName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to query live guard snapshot for room {RoomId}; continuing without it", roomId);
            return [];
        }
    }

    private async Task<IReadOnlyList<LiveEventTextIndexRecord>> TryGetSuperChatsAsync(
        RecordingRecord recording, CancellationToken ct)
    {
        try
        {
            var from = recording.StartedAt;
            var to = recording.EndedAt ?? DateTime.UtcNow;
            if (to < from)
            {
                return [];
            }

            return await liveEventTextIndex.ListByRoomTypeAndWindowAsync(
                recording.RoomId, LiveEventTextTypes.SuperChat, from, to, MaxScRecords, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Failed to query super chats for room {RoomId}; continuing without them", recording.RoomId);
            return [];
        }
    }

    /// <summary>防御式解析 account_basic_info.Data.uName（与 AnchorNameExtractor 同构）。</summary>
    internal static string? ExtractUName(string? data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(data) is not JsonObject obj)
            {
                return null;
            }

            return obj["uName"] is JsonValue value && value.TryGetValue<string>(out var name) ? name : null;
        }
        catch
        {
            return null;
        }
    }
}
