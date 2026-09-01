# Z.Vtuber API

## 技术栈说明

- 语言：C# (.NET 10)
- 框架：ASP.NET Core Web API
- API 文档：Microsoft.AspNetCore.OpenApi
- 数据库：MongoDB（Aspire 编排，`Aspire.MongoDB.Driver` + `MongoDB.Driver`）
- 消息通道：Redis pub/sub（`Aspire.StackExchange.Redis`，连接串 `ConnectionStrings:messaging`）

## 项目模块示意图

```mermaid
graph LR
    Web[apps/web] --> API[servers/api Z.Vtuber]
    API --> M[(MongoDB)]
    R[(Redis pub/sub)] -->|按需订阅 live-event:{roomId}| API
    Common[servers/common Z.Vtuber.Common] -.被引用.-> API
```

## 模块结构

纯 API 服务：对外接口与读侧能力，进程内不承载任何周期性抓取任务（抓取在 `servers/crawler`）。

- `Controllers/`：对外 API（Health + BilibiliSubscription/BilibiliUser/BilibiliLive + BilibiliBackstage，统一前缀 `/api/b`；`BilibiliSubscriptionController` 订阅/退订支持 `dynamic`/`danmaku`/`guard`；`BilibiliBackstageController` 为后台聚合只读接口，前缀 `/api/b/backstage`，内存 join 返回订阅/ Cookie 列表及其昵称）
- `Live/`：`ILiveEventBus`/`LiveEventBus`（SSE 订阅门控；数据源为 Redis pub/sub，按订阅计数驱动按需 `SUBSCRIBE`/`UNSUBSCRIBE`，收到消息反序列化为 `LiveEvent` 后走既有 type 过滤门控分发）
- `Program.cs`：注册 Controllers/OpenApi/HealthChecks/mongo/Redis 客户端/仓储/共享服务/`ILiveEventBus`/backstage 聚合；启动时执行幂等索引初始化与回填

## 共享依赖（servers/common）

实体、仓储、共享领域服务（`CookiePoolService`、`SubscriptionService`、`LiveSessionTracker`、`LiveStatusStateMachine`、`PubTsExtractor`、`SpaceTimelinePaging`）、共享模型 `LiveEvent`、`LiveGuardRepository`、`MongoDbInitializer`、`BsonConvert`、backstage 聚合扩展均位于 `servers/common`（`Z.Vtuber.Common`），本服务与 crawler 共同引用单份实现。

## 开发流程

- TDD 开发（单元测试：`tests/Z.Vtuber.Tests`，Testcontainers MongoDB + Redis 集成测试）
- 契约式开发（OpenAPI）：契约 `contracts/bilibili-crawler.openapi.yaml`
- 参考：`docs/engineering/common-rules.md`

## 相关规范引用

- `docs/engineering/common-rules.md`
- `docs/engineering/backend-rules.md`
- 契约：`contracts/bilibili-crawler.openapi.yaml`
