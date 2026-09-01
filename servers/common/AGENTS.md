# Z.Vtuber.Common

## 技术栈说明

- 语言：C# (.NET 10)
- 类型：类库（`Microsoft.NET.Sdk` + `FrameworkReference Microsoft.AspNetCore.App`，供 DI/Logging/Options 抽象）
- 数据库：MongoDB（`MongoDB.Driver` 3.9.0）

## 定位

两个服务（`servers/api`、`servers/crawler`）共享的公共类型单一来源，避免双份维护口径漂移。被 api 与 crawler 共同引用。

## 模块结构

- `Persistence/`：10 个 MongoDB 集合实体（9 实体 + Meta）、仓储（含 `CookieAccountInfoRepository`：按 `CookieId` upsert 帐号昵称、批量查询；`AccountBasicInfoRepository.GetLatestByMidsAsync` 批量取最新主播资料）、`MongoDbInitializer`（幂等索引初始化 + 各类回填 + 订阅类型拆分迁移）、`BsonConvert`、`BackstageDataAggregationExtensions`（注册 `ICookieAccountInfoRepository`）
- `Services/`：`ICookiePoolService`（三态：在线/冷却/离线，round-robin）、`ISubscriptionService`、`ILiveSessionTracker`、`LiveStatusStateMachine`（场次划分）、`PubTsExtractor`、`SpaceTimelinePaging`、`ICookieNavProbeService` 契约（具体实现 `CookieNavProbeService` 在 crawler）
- `Live/`：共享事件模型 `LiveEvent`、弹幕文本/价格提取器（`LiveEventTextExtractor`、`GiftPriceExtractor`，供 crawler 归一化与 common 回填共用）、事件频道名约定 `LiveEventChannels`

## 归属约定

- 仅被一方使用的代码归该方：抓取专用（`Crawlers/`、`Jobs/`、`LiveEventSource` 等）在 `servers/crawler`；对外接口（`Controllers/`）与 SSE `LiveEventBus` 在 `servers/api`。
- 被双方使用的代码（实体、仓储、共享服务、共享模型、提取器、`ICookieNavProbeService` 契约）入 common。
- `ICookieNavProbeService` 契约在 common，具体实现 `CookieNavProbeService`（依赖抓取通道与 Options）在 crawler，避免 common 反向依赖 crawler。

## 裸部署配置

- `ConnectionStrings:mongo`：MongoDB 连接串
- `ConnectionStrings:messaging`：Redis 连接串

## 开发流程

- TDD 开发（单元测试：`tests/Z.Vtuber.Tests`）
- 参考：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`