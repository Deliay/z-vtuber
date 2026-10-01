namespace Z.Vtuber.Recording.Contracts;

/// <summary>多片段音频合并请求。</summary>
public sealed class RecordingAudioMergeRequest
{
    public List<RecordingAudioMergeItem>? Items { get; set; }
}

/// <summary>合并请求中的单个片段：录播文件 + 起止毫秒。</summary>
public sealed class RecordingAudioMergeItem
{
    public long RoomId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public long StartMs { get; set; }

    public long EndMs { get; set; }
}
