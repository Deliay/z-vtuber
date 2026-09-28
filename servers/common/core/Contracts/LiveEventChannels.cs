namespace Z.Vtuber.Common.Contracts;

public static class LiveEventChannels
{
    public static string ChannelName(long roomId) => $"live-event:{roomId}";
}
