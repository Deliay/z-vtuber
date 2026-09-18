namespace Z.Vtuber.Recording.Contracts;

/// <summary>批量文件磁盘状态查询请求。</summary>
public sealed class RecordingFileStatsRequest
{
    public List<RecordingFileStatsRoomRequest>? Items { get; set; }
}

public sealed class RecordingFileStatsRoomRequest
{
    public long RoomId { get; set; }

    public List<string>? Files { get; set; }
}
