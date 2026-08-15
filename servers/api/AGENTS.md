# Z.Vtuber API

## 技术栈说明

- 语言：C# (.NET 10)
- 框架：ASP.NET Core Web API
- API 文档：Microsoft.AspNetCore.OpenApi
- 数据库：MongoDB（Aspire 编排，`Aspire.MongoDB.Driver` + `MongoDB.Driver`）
- 抓取库：Mikibot.Crawler（NuGet 1.0.8）

## 项目模块示意图

```mermaid
graph LR
    Web[apps/web] --> API[servers/api Z.Vtuber]
    API --> M[(MongoDB)]
    API --> BILI[B站接口/弹幕长连接]
```

## 模块结构

- `Controllers/`：对外 API（Health + BilibiliSubscription/BilibiliUser/BilibiliLive，统一前缀 `/api/b`）
- `Configuration/`：`BilibiliCrawlerOptions`（抓取周期等配置，节 `BilibiliCrawler`）
- `Crawlers/`：`IBiliApiClient`/`BiliApiClient`（per-cookie HTTP 抓取封装）、`BiliRawCrawler`
- `Jobs/`：`PeriodicJobBase`、`CrawlLockProvider`（per-key 锁）、`CookieProbeJob`/`AccountInfoJob`/`LiveStatusJob`/`SpaceTimelineJob`
- `Live/`：`LiveEventSource`（弹幕长连接）、`DanmakuClientFactory`、`DanmakuEventNormalizer`、`ILiveEventBus`/`LiveEventBus`（SSE 订阅门控）、`LiveEvent`
- `Persistence/`：6 个 MongoDB 集合实体、仓储、`MongoDbInitializer`（索引初始化 + `SpaceTimeline` PubTs 幂等回填）、`BsonConvert`
- `Services/`：`ICookiePoolService`（round-robin）、`ISubscriptionService`、`ILiveSessionTracker`、`LiveStatusStateMachine`（场次划分，`Decide` 返回 `SessionTransition`：None/Started/Ended）、`PubTsExtractor`（从动态 JSON 提取 `pub_ts`）、`AccountInfoCrawler`（B 站场次按时间邻近合并去重）、`SpaceTimelinePaging`
- 场次维护：`LiveStatusJob` 在开播/下播时通过 `LiveSessionRepository.UpsertAsync`/`CloseAsync` 同步 `bilibili_live_session`；分页端点统一支持可选 `startedAt`/`endedAt` 时间范围过滤
- 直播事件：`BilibiliLiveController` 的 `event/range` 支持分页（`page`/`pageSize`）与可重复 `type` 类型筛选、可选时间范围；`event/subscribe` 支持可重复 `type` 过滤推送；仓储 `LiveEventRepository.GetPageAsync` 组合 RoomId + 类型 `$in` + 可选 OccurredAt 范围 + Count/Find 分页

## 开发流程

- TDD 开发（单元测试：`tests/Z.Vtuber.Tests`，Testcontainers 集成测试）
- 契约式开发（OpenAPI）：契约 `contracts/bilibili-crawler.openapi.yaml`
- 参考：`docs/engineering/common-rules.md`

## 相关规范引用

- `docs/engineering/common-rules.md`
- `docs/engineering/backend-rules.md`
- 契约：`contracts/bilibili-crawler.openapi.yaml`
