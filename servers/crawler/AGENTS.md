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

- `Crawlers/`：`IBiliApiClient`/`BiliApiClient`（直连）、`BrowserishBiliApiClient`（拟真通道）、`ResilientBiliApiClient`（自动切换装饰器，依赖 `ICookieServiceClient` 进行风控冷却报告）、`RiskControlHandler`/`RiskControlClassifier`（风控识别 DelegatingHandler）、`CrawlBackoffRegistry`（目标级退避/冷却）、`BilibiliAccountProvider`（按 Cookie 构建并缓存 BilibiliAccount 服务提供者，依赖 `ICookieServiceClient`）
- `Jobs/`：`PeriodicJobBase`、`CrawlLockProvider`（per-key 分布式锁，依赖 `IDistributedLockProvider`）、`AccountInfoJob`（周期枚举 `Dynamic/Danmaku/Guard/Recording` 订阅，保障录播订阅主播 mid→roomId 资料落库）/`LiveStatusJob`/`SpaceTimelineJob`（捕获 `CrawlBackoffActiveException` 跳过退避期，均依赖 `ICookieServiceClient`）、`SubscriptionEventSubscriber`（订阅 Redis 订阅事件频道，将 api 进程创建的订阅桥接为进程内 `Subscribed`，触发 `AccountInfoJob` 即时抓取，避免依赖小时级周期兜底）、`LiveGuardJob`（翻页逻辑已抽取至 `LiveGuardListFetcher`，依赖 `ICookieServiceClient`）、`WeiboTimelineJob`（微博动态周期抓取：订阅驱动 + 即时补抓 + 退避门控 `weibo:{uid}` + 收敛式翻页 + MblogId 幂等落库；风控分支：登录 cookie 命中 → `report-invalid` 阻塞宣告，200 自愈重试一次 / 410/404/504 放弃本轮并退避；匿名命中 → 维持 `RenewAsync` + 退避）
- `Weibo/`：微博抓取链路（与 B 站隔离）：`WeiboApiClient`/`IWeiboApiClient`（m.weibo.cn getIndex）、`WeiboLoginCookieProvider`（主凭证：经 `ICookieServiceClient` 调 `/api/cookie/weibo/checkout`，按 cookieId 复用 `CookieCallGate` per-cookie 互斥；404 返回“无可用”信号不抛异常）、`WeiboCookieSource`（主/兜底组合：`UseLoginCookie=true` 且有货走登录 cookie，否则回退访客并记 warning）、`WeiboVisitorCookieProvider`（兜底：访客 SUB/SUBP，genvisitor2 一步 + 三步握手 fallback、定时刷新、失效重建）、`WeiboRiskControlClassifier`/`WeiboRiskControlException`（风控识别；异常携带 `CookieId`/`IsLoginCookie`）、`WeiboMblogParser`（Ruby `created_at` 解析 + card 提取）
- `Live/`：`LiveEventSource`（弹幕长连接，归一化落库 `bilibili_live_event` 同时向 Redis 发布）、`DanmakuClientFactory`、`DanmakuEventNormalizer`、`ILiveEventPublisher`/`RedisLiveEventPublisher`（频道 `live-event:{roomId}`）
- `Services/`：`AccountInfoCrawler`（场次合并去重）、`CookieServiceClient`（`ICookieServiceClient` 实现，HTTP 客户端调用 cookie 服务的 `/api/cookie/checkout`、`/api/cookie/report-invalid`、`/api/cookie/cooldown` 接口）、`LiveGuardListFetcher`（翻页抓取 + top3 并入 + 页间延迟/退避，供 `LiveGuardJob` 与补录共用）、`LiveGuardTop3BackfillPlanner`（top10 选取与差异计算纯函数）、`LiveGuardTop3BackfillService`（补录编排 + per-mid 进度标记，依赖 `ICookieServiceClient`）
- `Configuration/`：`BilibiliCrawlerOptions`（节 `BilibiliCrawler`：`FetchMode`/退避/冷却/拟真头参数）、`WeiboCrawlerOptions`（节 `WeiboCrawler`：`TimelineIntervalSeconds`/超时/凭证刷新周期/`MaxPagesPerCrawl`/UA/独立退避参数/`UseLoginCookie`（默认 true）/`LoginCookieRetryOnce`（默认 true））
- `Program.cs`：`WebApplication` 宿主；注册 Mongo/Redis 客户端、Options 校验、仓储、共享服务、抓取通道/风控/退避、`ILiveEventPublisher`、Job 与 `LiveEventSource` 的 `AddHostedService`；`CookieServiceClient`（`ICookieServiceClient`）HTTP 客户端注册（指向 cookie 服务）；`LiveGuardListFetcher` 与 `LiveGuardTop3BackfillService` 注册；微博链路注册（`WeiboSubscriptionRepository`/`WeiboTimelineRepository`/`WeiboSubscriptionService`/`WeiboLoginCookieProvider`/`WeiboCookieSource`/`WeiboVisitorCookieProvider`/`WeiboApiClient`/`WeiboTimelineJob`）；`SubscriptionEventSubscriber` 注册；启动执行幂等索引初始化与回填；`/health`（进程存活 + MongoDB 连通性，`MongoDbHealthCheck`）
- `Dockerfile`：两阶段构建（aspnet 运行时，EXPOSE 8080），restore 前同时拷贝 common 与 crawler csproj
- 镜像流水线：`.github/workflows/build-crawler-image.yml`（`zero-tools/z-vtuber-crawler`）

## 事件通道语义

crawler 在事件落库同时 `PUBLISH live-event:{roomId}`；api 侧按需订阅/退订。Redis pub/sub 为 fire-and-forget，事件可靠性以 MongoDB 落库为准，发布失败仅记日志不影响落库与长连接。

## Cookie 服务依赖

crawler 通过 `ICookieServiceClient`（实现 `CookieServiceClient`）HTTP 客户端调用 cookie 服务（`CookieService:BaseUrl`），进行 Cookie 领取（checkout）、失效报告（report-invalid）、冷却标记（cooldown）。不再直接访问 MongoDB Cookie 池。微博链路使用 `CheckoutWeiboAsync`/`ReportWeiboInvalidAsync`/`CooldownWeiboAsync`（`/api/cookie/weibo/*`）；微博 checkout 无可用时返回“无可用”信号（不抛 `NoAvailableCookieException`），由 `WeiboCookieSource` 回退匿名凭证。

## 分布式锁迁移

`CookieCallGate` 和 `CrawlLockProvider` 已迁移至 `IDistributedLockProvider` 接口（common 定义），具体实现 `RedisDistributedLockProvider` 在 cookie 服务中。crawler 通过 DI 注入 `IDistributedLockProvider` 实例。

## 裸部署配置

- `ConnectionStrings:mongo`：MongoDB 连接串
- `ConnectionStrings:messaging`：Redis 连接串
- `BilibiliCrawler` 节：抓取周期/风控/退避配置（`ValidateOnStart`）
- `WeiboCrawler` 节：微博抓取间隔/凭证刷新/退避等配置（`ValidateOnStart`）
- 健康检查端口：默认 8080（Kestrel）

## 开发流程

- TDD 开发（单元测试：`tests/Z.Vtuber.Tests`，Testcontainers MongoDB + Redis 集成测试）
- 参考：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`
