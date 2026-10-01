# Z.Vtuber.Common

## 技术栈说明

- 语言：C# (.NET 10)
- 类型：类库（`Microsoft.NET.Sdk` + `FrameworkReference Microsoft.AspNetCore.App`，供 DI/Logging/Options 抽象）
- 数据库：MongoDB（`MongoDB.Driver` 3.9.0）

## 定位

各后端服务（`servers/api`、`servers/crawler`、`servers/cookie`、`servers/notifier`、`servers/recording`、`servers/recording-transcript-api`、`servers/recording-transcript-consumer`、`servers/training`）共享的公共类型单一来源。已按业务领域拆分为 **1 个共享内核 + 7 个领域项目**，领域项目之间零引用（星型依赖 Core），使服务镜像流水线的触发范围与实际依赖一致。

## 项目结构（星型依赖，领域项目间零引用）

| 项目 | 目录 | 归属 |
|------|------|------|
| `Z.Vtuber.Common.Core` | `core/` | 共享内核：Mongo 基座 `MongoDbInitializer`（`DatabaseName`、迁移 marker 读写、`DropIndexIfExistsAsync`）、`MetaRecord`、`CoreCollectionNames.Meta`、`BsonConvert`、`StringTolerantBsonDocumentSerializer`、`LiveEventPayloadParser`、`SubscriptionType`、`Locking/` 契约、跨服务事件/异常契约（`Z.Vtuber.Common.Contracts`） |
| `Z.Vtuber.Common.Live` | `live/` | B 站直播域：实体/仓储/共享服务（`SubscriptionService`、`LiveSessionTracker`、`LiveStatusStateMachine`、`PubTsExtractor`、`SpaceTimelinePaging`）/提取器（`LiveEventTextExtractor`、`GiftPriceExtractor`）/`LiveEvent`/`LiveMongoSetup`/`LiveCollectionNames` |
| `Z.Vtuber.Common.Cookie` | `cookie/` | Cookie 域（含跨平台 Cookie 池与微博 cookie 两集合）：`CookieRecord`/`CookieSessionRecord`/`CookieAccountInfoRecord`、`ICookiePoolService`/`CookiePoolService`、`CookieAccountInfoRepository`、`CookieMongoSetup`、`CookieCollectionNames`、`BackstageDataAggregationExtensions` |
| `Z.Vtuber.Common.Weibo` | `weibo/` | 微博域：`WeiboSubscriptionRecord`/`WeiboTimelineRecord`、2 仓储、`WeiboSubscriptionService`、`WeiboMongoSetup`、`WeiboCollectionNames` |
| `Z.Vtuber.Common.Recording` | `recording/` | 录播域（含场次归档）：`RecordingStatusRecord`/`RecordingStatusRepository`、`RecordingRecord`/`RecordingRepository`、`RecordingMongoSetup`、`RecordingCollectionNames` |
| `Z.Vtuber.Common.Transcript` | `transcript/` | 转写域：任务/片段/模型/自动入队实体与仓储（`TranscriptTaskRecord`/`TranscriptModelRecord` 已标注 `[BsonIgnoreExtraElements]` 以容忍存量/异构版本未映射元素）、`TranscriptMongoSetup`（含 `CleanupAsync`）、`TranscriptCollectionNames` |
| `Z.Vtuber.Common.Training` | `training/` | 训练域：数据集/条目实体与仓储、候选短句反向索引实体/仓储（`TrainingDatasetItemSegmentRecord`/`TrainingDatasetItemSegmentRepository`）、Provider/Model/情绪任务实体与仓储（`TrainingProviderRecord`/`TrainingModelRecord`/`TrainingEmotionTaskRecord` + 三仓储）、条目仓储 `SetEmotionAsync`（写 `Extensions.emotion`）、情绪任务仓储并发原语（`TryInsertActiveAsync` 唯一键幂等、`UpdateRunningAsync` 按 `Id+Status+Attempts` fencing、`RetryFailedAsync` 按 `Id+Status==FAILED` 原子 CAS）、`TrainingMongoSetup`、`TrainingCollectionNames`（`training_datasets`/`training_dataset_items`/`training_dataset_item_segments`（候选短句拼音反向索引，索引 `DatasetId_1`/`DatasetId_1_ItemId_1`）/`training_providers`/`training_models`/`training_emotion_tasks`；后者含 `ItemId_1_Active` 部分唯一索引） |
| `Z.Vtuber.Common.Notification` | `notification/` | 通知域：`NotificationSubscriptionRecord`、`INotificationSubscriptionRepository`/实现、`NotificationMongoSetup`（含 `MigrateAsync`）、`NotificationCollectionNames` |

## 归属约定

- 每个模型/类型唯一归属一个领域；领域项目只引用 `Core`，**禁止领域项目之间互相引用**（由 `ProjectReferenceGuardTests` 守护）。
- 跨服务事件/异常契约（`CookieInvalidEvent`、`CookieEventChannels`、`RecordingEventChannels`、`RecordingStartedEvent`、`RecordingFinishedEvent`、`TranscriptSegmentEvent`、`SubscriptionEvent`、`SubscriptionEventChannels`、`LiveEventChannels`、`NoAvailableCookieException`）统一在 `Z.Vtuber.Common.Contracts`（Core），跨服务协议变更会触发全部 8 条镜像流水线（合理）。
- 集合名常量按域切分为 `<域>CollectionNames`（常量值逐字节不变），共享 `CoreCollectionNames.Meta`。
- 索引初始化/迁移按域切分为 `XxxMongoSetup`（`EnsureIndexesAsync`/`MigrateAsync`/`BackfillAsync`/`CleanupAsync`）；服务启动只初始化自己引用的领域，且保持拆分前的调用顺序（幂等，部署顺序无关）。
- 「微博 cookie」两集合（`weibo_cookies`/`weibo_cookie_sessions`）的文档模型为 `CookieRecord`/`CookieSessionRecord`，索引初始化归 Cookie 域（`CookieMongoSetup`），保持 Weibo → Core 单向。
- `SubscriptionType` 上移 Core（被 `SubscriptionEvent`、recording、api 使用）；`SubscriptionRecord` 仍留 Live 域。
- 场次归档 `RecordingRecord`/`RecordingRepository` 归录播域（生产方在 recording 服务）。
- 仅被一方使用的代码归该服务：抓取专用（`Crawlers/`、`Jobs/`、`LiveEventSource` 等）在 `servers/crawler`；对外接口（`Controllers/`）与 SSE `LiveEventBus` 在 `servers/api`。

## 裸部署配置

- `ConnectionStrings:mongo`：MongoDB 连接串
- `ConnectionStrings:messaging`：Redis 连接串

## 开发流程

- 跑测口径：遵循 `docs/engineering/common-rules.md`「测试性能基线与超时护栏」小节（单用例 ≤5s；报告工具 `tests/Z.Vtuber.TestReport --check` 护栏）
- TDD 开发（单元测试：`tests/Z.Vtuber.Tests`）
- 参考：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`
