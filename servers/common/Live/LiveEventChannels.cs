namespace Z.Vtuber.Common.Live;

public static class LiveEventChannels
{
    public static string ChannelName(long roomId) => $"live-event:{roomId}";
}