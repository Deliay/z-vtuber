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

- `Controllers/`：对外 API（Health + BilibiliSubscription/BilibiliUser/BilibiliLive + BilibiliLiveGuard + BilibiliBackstage，统一前缀 `/api/b`；`BilibiliSubscriptionController` 订阅/退订支持 `dynamic`/`danmaku`/`guard`/`recording`；`BilibiliBackstageController` 为后台聚合只读接口，前缀 `/api/b/backstage`，内存 join 返回订阅/ Cookie 列表及其昵称；`BilibiliLiveRecordingController` 提供录播能力：`GET /api/b/live/recording-status`（读 `recording_status` 内存 join 订阅/昵称聚合）、`GET /api/b/live/{roomId}/recordings[/{fileName}]`（流式代理列表/下载）、`GET /api/b/live/{roomId}/live-stream/audio`（流式代理实时音频，禁用缓冲），recording 不可达时列表/下载返回 502、状态聚合退化 unknown 不阻断；`BilibiliLiveController.Subscribe` 端点含连接级心跳行为：连接建立后立即发送 `event: heartbeat` + `data: {}`，之后每 10 秒发送一次，心跳不受 type 过滤影响、不持久化、不出现在历史事件查询结果中；`BilibiliLiveGuardController` 提供在舰查询端点 `GET /api/b/live/{roomId}/guard`，支持 any/all 统计口径）。微博链路：`WeiboSubscriptionController`（`/api/weibo/subscription` GET/POST/DELETE）与 `WeiboUserController`（`/api/weibo/user/{uid}/timeline` 分页+时间范围查询，返回微博原始 mblog JSON），与 B 站端点完全隔离，契约见 `contracts/weibo-crawler.openapi.yaml`
- `Live/`：`ILiveEventBus`/`LiveEventBus`（SSE 订阅门控；数据源为 Redis pub/sub，按订阅计数驱动按需 `SUBSCRIBE`/`UNSUBSCRIBE`，收到消息反序列化为 `LiveEvent` 后走既有 type 过滤门控分发）、`SubscriptionEventPublisher`（订阅进程内 `Subscribed` 事件并发布到 Redis 频道 `subscription-subscribed`，供 crawler 即时抓取新订阅主播资料）
- `Program.cs`：注册 Controllers/OpenApi/HealthChecks/mongo/Redis 客户端/仓储/共享服务/`ILiveEventBus`/backstage 聚合；启动时执行幂等索引初始化与回填

## 共享依赖（servers/common）

实体、仓储、共享领域服务（`CookiePoolService`、`SubscriptionService`、`LiveSessionTracker`、`LiveStatusStateMachine`、`PubTsExtractor`、`SpaceTimelinePaging`、`WeiboSubscriptionService`）、共享模型 `LiveEvent`、`LiveGuardRepository`、`MongoDbInitializer`、`BsonConvert`、backstage 聚合扩展均位于 `servers/common`（`Z.Vtuber.Common`），本服务与 crawler 共同引用单份实现。

## 开发流程

- TDD 开发（单元测试：`tests/Z.Vtuber.Tests`，Testcontainers MongoDB + Redis 集成测试）
- 契约式开发（OpenAPI）：契约 `contracts/bilibili-crawler.openapi.yaml`
- 参考：`docs/engineering/common-rules.md`

## 相关规范引用

- `docs/engineering/common-rules.md`
- `docs/engineering/backend-rules.md`
- 契约：`contracts/bilibili-crawler.openapi.yaml`
