# Z.Vtuber.Common

## 技术栈说明

- 语言：C# (.NET 10)
- 类型：类库（`Microsoft.NET.Sdk` + `FrameworkReference Microsoft.AspNetCore.App`，供 DI/Logging/Options 抽象）
- 数据库：MongoDB（`MongoDB.Driver` 3.9.0）

## 定位

各后端服务（`servers/api`、`servers/crawler`、`servers/cookie`、`servers/recording`、`servers/recording-transcript-consumer`、`servers/recording-transcript-api`）共享的公共类型单一来源，避免多份维护口径漂移。

## 模块结构

- `Persistence/`：MongoDB 集合实体（B 站 9 实体 + Meta；微博 `WeiboSubscriptionRecord`/`WeiboTimelineRecord`；通知 `NotificationSubscriptionRecord`（群维度字段：`GroupId`/`GroupName`））、仓储（含 `CookieAccountInfoRepository`：按 `CookieId` upsert 帐号昵称（新增 `FetchedAt` 字段记录探测时间）、批量查询；`AccountBasicInfoRepository.GetLatestByMidsAsync` 批量取最新主播资料；`LiveGuardRepository.GetOnboardGuardEntriesAsync`：聚合管道查询在舰用户，支持 any/all 统计口径，`RoomId_1_Date_1` 复合索引加速查询，补录方法 `GetDistinctMidsAsync`/`GetSnapshotsByMidAsync`/`BackfillMissingEntriesAsync`；微博 `WeiboSubscriptionRepository`（按 Uid upsert/删除/列举）、`WeiboTimelineRepository`（按 MblogId SetOnInsert 幂等落库 + 按 PubTs 过滤分页）；通知 `NotificationSubscriptionRepository`（按 `EventType`+`GroupId` 复合索引）；录播 `RecordingStatusRepository`（`recording_status` 集合，按 `RoomId` 唯一键 upsert / 全量或按 RoomIds 批量读）；转写 5 集合实体与仓储 `RecordingRecord`/`RecordingRepository`（场次归档，insert/回写 EndedAt/分页列表/按 RoomId+StartedAt 幂等查）、`TranscriptTaskRecord`/`TranscriptTaskRepository`（状态/模式/来源常量，插入/扫描查询/状态与 position 更新/按 RecordingId+ModelId 查）、`TranscriptSegmentRecord`/`TranscriptSegmentRepository`（事务内 insert/按 TaskId 升序读）、`TranscriptModelRecord`/`TranscriptModelRepository`（CRUD/默认位原子切换/按并发计数）、`TranscriptAutoEnqueueRecord`/`TranscriptAutoEnqueueRepository`（按 RoomId upsert/全量读）；`LiveSessionRepository` 新增 `GetByRoomIdAndTimeWindowAsync`（存量清洗按 RoomId+时间窗查场次））、`MongoDbInitializer`（幂等索引初始化 + 各类回填 + 订阅类型拆分迁移；含微博 2 集合索引初始化 + 通知 `(EventType, GroupId)` 唯一索引 + 录播 `recording_status.RoomId_1` 唯一索引 + 转写 `EnsureTranscriptIndexesAsync`（`recordings`：`RoomId_1_StartedAt_-1`；`transcript_tasks`：唯一 `RecordingId_1_ModelId_1` + `Status_1__id_1`；`transcript_segments`：唯一 `TaskId_1_StartTime_1`；`transcript_models`：`IsDefault_1` 部分唯一；`transcript_auto_enqueue`：唯一 `RoomId_1`）；含微博 Cookie 集合索引 `EnsureWeiboCookieIndexesAsync`（`weibo_cookies`：`(Status, LastUsedAt)`+`AccountId`；`weibo_cookie_sessions`：`Status`+`CookieId`）与失效事件类型拆分迁移 `MigrateCookieInvalidEventTypeSplitAsync`（marker `cookie_invalid_platform_split_v1`，存量 `cookie_invalid` → `bilibili_cookie_invalid` 幂等））、`BsonConvert`、`BackstageDataAggregationExtensions`（注册 `ICookieAccountInfoRepository`）
- `Locking/`：`IDistributedLockProvider`（分布式锁抽象）、`IDistributedLockHandle`（锁句柄，`IAsyncDisposable`）
- `Services/`：`ICookiePoolService`（三态：在线/冷却/离线，round-robin）、`ISubscriptionService`、`ILiveSessionTracker`、`LiveStatusStateMachine`（场次划分）、`PubTsExtractor`、`SpaceTimelinePaging`、`IWeiboSubscriptionService`/`WeiboSubscriptionService`（Uid upsert 幂等 + `Subscribed` 事件，与 B 站订阅隔离）、`ICookieNavProbeService` 契约（含 `CookieProbeOutcome` 枚举：Success/Cooldown/Offline/Backoff）
- `Live/`：共享事件模型 `LiveEvent`、弹幕文本/价格提取器（`LiveEventTextExtractor`、`GiftPriceExtractor`，供 crawler 归一化与 common 回填共用）、事件频道名约定 `LiveEventChannels`、Cookie 失效事件 `CookieInvalidEvent`（字段含 `Platform`，空/未知按 bilibili 处理）与频道名 `CookieEventChannels`、跨进程订阅事件 `SubscriptionEvent`/`SubscriptionEventChannels`（api 发布、crawler 消费）、录播转写事件频道 `RecordingEventChannels`（`recording-started`/`recording-finished`/`transcript-segment:{taskId}`）与事件 record `RecordingStartedEvent`/`RecordingFinishedEvent`/`TranscriptSegmentEvent`（recording 发布 started/finished、consumer 发布 segment）

## 归属约定

- 仅被一方使用的代码归该方：抓取专用（`Crawlers/`、`Jobs/`、`LiveEventSource` 等）在 `servers/crawler`；对外接口（`Controllers/`）与 SSE `LiveEventBus` 在 `servers/api`。
- 被双方使用的代码（实体、仓储、共享服务、共享模型、提取器、`ICookieNavProbeService` 契约、`Locking/` 契约）入 common。
- `ICookieNavProbeService` 契约在 common，具体实现 `CookieNavProbeService`（依赖抓取通道与 Options）在 cookie 服务，避免 common 反向依赖 crawler。
- `NextAsync`/探测契约已删除：Cookie 池调度（checkout/dispatch）由 cookie 服务通过 HTTP API 提供，crawler 不再直接调用 `ICookiePoolService.NextAsync`。
- `SubscriptionType` 枚举含 `Dynamic/Danmaku/Guard/Recording`（均 `[BsonRepresentation(BsonType.String)]`）；录播状态实体 `RecordingStatusRecord`/仓储 `RecordingStatusRepository` 入 common（recording 写、api 读）。

## 裸部署配置

- `ConnectionStrings:mongo`：MongoDB 连接串
- `ConnectionStrings:messaging`：Redis 连接串

## 开发流程

- TDD 开发（单元测试：`tests/Z.Vtuber.Tests`）
- 参考：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`
