namespace Z.Vtuber.Recording.Live;

/// <summary>直播间开播状态信息。</summary>
public sealed record LiveRoomInfo(long RoomId, string Title, bool IsLive);

/// <summary>playurl 展开后的一条候选流。</summary>
public sealed record LiveStreamCandidate(
    string Url,
    string Protocol,
    string Format,
    string Codec,
    int? BitrateKbps = null);
