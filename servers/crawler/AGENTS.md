# Z.Vtuber.Crawler

## 技术栈说明

- 语言：C# (.NET 10)
- 框架：Worker（`Microsoft.NET.Sdk.Worker` + `FrameworkReference Microsoft.AspNetCore.App`，挂最小 Kestrel 提供 `/health` 健康检查）
- 抓取库：Mikibot.Crawler（NuGet 1.0.9）
- 数据库：MongoDB（Aspire 编排，`Aspire.MongoDB.Driver` + `MongoDB.Driver`）
- 消息通道：Redis pub/sub（`Aspire.StackExchange.Redis`，连接串 `ConnectionStrings:messaging`）

## 项目模块示意图

```mermaid
graph LR
    Crawler[servers/crawler Z.Vtuber.Crawler] --> M[(MongoDB)]
    Crawler --> BILI[B站接口/弹幕长连接]
    Crawler -->|PUBLISH live-event:{roomId}| R[(Redis pub/sub)]
    Common[servers/common Z.Vtuber.Common] -.被引用.-> Crawler
```

## 模块结构

独立抓取服务：专职周期任务、弹幕长连接与事件归一化、风控退避，持续向数据库写入。不承载对外 API。

- `Crawlers/`：`IBiliApiClient`/`BiliApiClient`（直连）、`BrowserishBiliApiClient`（拟真通道）、`ResilientBiliApiClient`（自动切换装饰器）、`RiskControlHandler`/`RiskControlClassifier`（风控识别 DelegatingHandler）、`CrawlBackoffRegistry`（目标级退避/冷却）、`BilibiliAccountProvider`
- `Jobs/`：`PeriodicJobBase`、`CrawlLockProvider`（per-key 锁）、`CookieProbeJob`/`AccountInfoJob`/`LiveStatusJob`/`SpaceTimelineJob`（捕获 `CrawlBackoffActiveException` 跳过退避期）
- `Live/`：`LiveEventSource`（弹幕长连接，归一化落库 `bilibili_live_event` 同时向 Redis 发布）、`DanmakuClientFactory`、`DanmakuEventNormalizer`、`ILiveEventPublisher`/`RedisLiveEventPublisher`（频道 `live-event:{roomId}`）
- `Services/`：`AccountInfoCrawler`（场次合并去重）、`CookieNavProbeService`（nav 探测 + 帐号信息落库 + 状态机处置，实现 common 的 `ICookieNavProbeService` 契约）
- `Configuration/`：`BilibiliCrawlerOptions`（节 `BilibiliCrawler`：`FetchMode`/退避/冷却/拟真头参数）
- `Program.cs`：`WebApplication` 宿主；注册 Mongo/Redis 客户端、Options 校验、仓储、共享服务、抓取通道/风控/退避、`ILiveEventPublisher`、4 个 Job 与 `LiveEventSource` 的 `AddHostedService`；启动执行幂等索引初始化与回填；`/health`（进程存活 + MongoDB 连通性，`MongoDbHealthCheck`）
- `Dockerfile`：两阶段构建（aspnet 运行时，EXPOSE 8080），restore 前同时拷贝 common 与 crawler csproj
- 镜像流水线：`.github/workflows/build-crawler-image.yml`（`zero-tools/z-vtuber-crawler`）

## 事件通道语义

crawler 在事件落库同时 `PUBLISH live-event:{roomId}`；api 侧按需订阅/退订。Redis pub/sub 为 fire-and-forget，事件可靠性以 MongoDB 落库为准，发布失败仅记日志不影响落库与长连接。

## 裸部署配置

- `ConnectionStrings:mongo`：MongoDB 连接串
- `ConnectionStrings:messaging`：Redis 连接串
- `BilibiliCrawler` 节：抓取周期/风控/退避配置（`ValidateOnStart`）
- 健康检查端口：默认 8080（Kestrel）

## 开发流程

- TDD 开发（单元测试：`tests/Z.Vtuber.Tests`，Testcontainers MongoDB + Redis 集成测试）
- 参考：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`
