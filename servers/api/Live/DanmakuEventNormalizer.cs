using Mikibot.Crawler.WebsocketCrawler.Data.Commands;

namespace Z.Vtuber.Live;

public static class DanmakuEventNormalizer
{
    private static readonly IReadOnlyDictionary<KnownCommands, string> TypeMapping =
        new Dictionary<KnownCommands, string>
        {
            [KnownCommands.DANMU_MSG] = "danmaku",
            [KnownCommands.SEND_GIFT] = "gift",
            [KnownCommands.COMBO_SEND] = "gift",
            [KnownCommands.SUPER_CHAT_MESSAGE] = "super_chat",
            [KnownCommands.GUARD_BUY] = "guard_buy",
            [KnownCommands.INTERACT_WORD] = "interact",
            [KnownCommands.INTERACT_WORD_V2] = "interact",
            [KnownCommands.ENTRY_EFFECT] = "entry_effect",
            [KnownCommands.WATCHED_CHANGE] = "watched_change",
            [KnownCommands.ROOM_REAL_TIME_MESSAGE_UPDATE] = "room_update",
            [KnownCommands.ONLINE_RANK_COUNT] = "online_rank_count",
            [KnownCommands.ONLINE_RANK_V2] = "online_rank_v2",
            [KnownCommands.ONLINE_RANK_V3] = "online_rank_v3",
            [KnownCommands.ANCHOR_LOT_START] = "anchor_lot_start",
            [KnownCommands.ANCHOR_LOT_AWARD] = "anchor_lot_award",
            [KnownCommands.POPULARITY_RED_POCKET_START] = "popularity_red_pocket_start",
            [KnownCommands.HOT_RANK_SETTLEMENT_V2] = "hot_rank_settlement",
        };

    public static LiveEvent? Normalize(long roomId, string? liveSessionId, string rawContent, DateTime occurredAt)
    {
        ICommandBase? command;
        try
        {
            command = ICommandBase.Parse(rawContent);
        }
        catch (Exception)
        {
            return null;
        }

        if (command is null)
        {
            return null;
        }

        var type = TypeMapping.TryGetValue(command.Command, out var mapped)
            ? mapped
            : command.Command.ToString().ToLowerInvariant();

        return new LiveEvent(roomId, type, liveSessionId, occurredAt, rawContent);
    }
}
