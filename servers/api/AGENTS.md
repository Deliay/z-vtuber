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

- `Controllers/`：对外 API（Health + BilibiliSubscription/BilibiliUser/BilibiliLive + BilibiliLiveGuard + BilibiliBackstage，统一前缀 `/api/b`；`BilibiliSubscriptionController` 订阅/退订支持 `dynamic`/`danmaku`/`guard`/`recording`；`BilibiliBackstageController` 为后台聚合只读接口，前缀 `/api/b/backstage`，内存 join 返回订阅/ Cookie 列表及其昵称；`BilibiliLiveRecordingController` 提供录播能力：`GET /api/b/live/recording-status`（读 `recording_status` 内存 join 订阅/昵称聚合；recording 状态 UpdatedAt 超过 3 分钟未刷新时读侧退化为 unknown）、`GET /api/b/live/{roomId}/recordings[/{fileName}]`（流式代理列表/下载；列表透传含大小与创建时间的对象数组，下载对上游 400——文件名非法或文件正在录制中——原样透传响应体，并透传 `Range`/`If-Range` 请求头与 200/206 状态码及 `Accept-Ranges`/`Content-Range`/`Content-Length` 响应头、新增 416 透传分支）、`GET /api/b/live/{roomId}/live-stream/audio`（流式代理实时音频，禁用缓冲）、`GET /api/b/live/{roomId}/recordings/{fileName}/audio`（试听音频片段代理：query `startMs`/`endMs` 原样透传，`ResponseHeadersRead` + `CopyToAsync` 流式转发；状态映射与下载代理同口径——上游 400 透传响应体、404→404、不可达/超时/非成功→502；成功仅透传 `Content-Type` 与 `Cache-Control`，**不设置 `Content-Disposition`**（供 `<audio>` 内联播放）、不透传 Range），recording 不可达时列表/下载返回 502、状态聚合退化 unknown 不阻断；`BilibiliLiveController.Subscribe` 端点含连接级心跳行为：连接建立后立即发送 `event: heartbeat` + `data: {}`，之后每 10 秒发送一次，心跳不受 type 过滤影响、不持久化、不出现在历史事件查询结果中；`BilibiliLiveGuardController` 提供在舰查询端点 `GET /api/b/live/{roomId}/guard`，支持 any/all 统计口径）。微博链路：`WeiboSubscriptionController`（`/api/weibo/subscription` GET/POST/DELETE）与 `WeiboUserController`（`/api/weibo/user/{uid}/timeline` 分页+时间范围查询，返回微博原始 mblog JSON），与 B 站端点完全隔离，契约见 `contracts/weibo-crawler.openapi.yaml`；`RecordingTranscriptProxyController` 将 `/api/transcripts/**` 与 `/api/recordings/**` 流式代理至 recording-transcript-api（named client `TranscriptService`，`ResponseHeadersRead` + `CopyToAsync`、SSE 禁缓冲并透传 `text/event-stream`、上游不可达 502；`ForwardAsync` 透传 `Content-Type`/`Cache-Control`，上游带 `Content-Disposition` 时一并透传、不带则不设置，供字幕下载得到正确文件名；含 `GET /api/transcripts/task/{taskId}/subtitle`（字幕下载镜像，透传 `format` query 与 `Content-Disposition`）、`PUT /api/transcripts/task/{taskId}/offset`（任务播放偏移镜像，PUT body 与 Content-Type 原样透传，200/400/404 透传、上游不可达 502；`ForwardAsync` 对 PUT 透传已支持，本路由零额外逻辑）、`GET /api/transcripts/candidate-rooms` 与 `DELETE /api/recordings/{roomId}/transcripts/settings`（透传 204/404））；`TrainingProxyController` 将 `/api/training/**` 共 8 个端点镜像至 training 服务（named client `TrainingService`，节 `TrainingService:BaseUrl` 默认 `http://training`；`ForwardAsync` 原样透传 query 与 POST/PUT 的 body/`Content-Type`，200/201/204/400/404 状态码与响应体一并透传，上游不可达或超时返回 502 `{"error":"training service unavailable"}`，无 SSE 分支；覆盖数据集 CRUD、条目列表/更新/批量删除与 `POST .../items/import` 导入），web 只认 `/api`
- `Live/`：`ILiveEventBus`/`LiveEventBus`（SSE 订阅门控；数据源为 Redis pub/sub，按订阅计数驱动按需 `SUBSCRIBE`/`UNSUBSCRIBE`，收到消息反序列化为 `LiveEvent` 后走既有 type 过滤门控分发）、`SubscriptionEventPublisher`（HostedService，宿主启动即挂载进程内 `Subscribed` 事件监听并发布到 Redis 频道 `subscription-subscribed`，供 crawler 即时抓取新订阅主播资料）
- `Program.cs`：注册 Controllers/OpenApi/HealthChecks/mongo/Redis 客户端/仓储/共享服务/`ILiveEventBus`/backstage 聚合/named HttpClient（`RecordingService:BaseUrl`、`TranscriptService:BaseUrl`、`TrainingService:BaseUrl`）；启动时执行幂等索引初始化与回填

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
