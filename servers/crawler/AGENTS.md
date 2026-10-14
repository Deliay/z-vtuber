# Z.Vtuber.Crawler

## 技术栈说明

- 语言：C# (.NET 10)
- 框架：Worker（`Microsoft.NET.Sdk.Worker` + `FrameworkReference Microsoft.AspNetCore.App`，挂最小 Kestrel 提供 `/health` 健康检查）
- 抓取库：Mikibot.Crawler（NuGet 1.0.9）
- 数据库：MongoDB（Aspire 编排，`Aspire.MongoDB.Driver` + `MongoDB.Driver`）
- 消息通道：Redis pub/sub（`Aspire.StackExchange.Redis`，连接串 `ConnectionStrings:messaging`）
- 对象存储：外部 S3 兼容存储（`AWSSDK.S3`，`ServiceURL` + `ForcePathStyle` 兼容 MinIO），用于投稿视频下载 cache-aside 缓存

## 项目模块示意图

```mermaid
graph LR
    Crawler[servers/crawler Z.Vtuber.Crawler] --> M[(MongoDB)]
    Crawler --> BILI[B站接口/弹幕长连接]
    Crawler -->|PUBLISH live-event:{roomId}| R[(Redis pub/sub)]
    Crawler -->|视频下载 cache-aside| S3[(外部 S3)]
    Common[servers/common Core+Live+Weibo] -.被引用.-> Crawler
```

## 模块结构

独立抓取服务：专职周期任务、弹幕长连接与事件归一化、风控退避，持续向数据库写入。不承载对外 API。

- `Crawlers/`：`IBiliApiClient`/`BiliApiClient`（直连）、`BrowserishBiliApiClient`（拟真通道）、`ResilientBiliApiClient`（自动切换装饰器，依赖 `ICookieServiceClient` 进行风控冷却报告）、`RiskControlHandler`/`RiskControlClassifier`（风控识别 DelegatingHandler）、`CrawlBackoffRegistry`（目标级退避/冷却）、`BilibiliAccountProvider`（按 Cookie 构建并缓存 BilibiliAccount 服务提供者，依赖 `ICookieServiceClient`）
- `Jobs/`：`PeriodicJobBase`、`CrawlLockProvider`（per-key 分布式锁，依赖 `IDistributedLockProvider`）、`AccountInfoJob`（周期枚举 `Dynamic/Danmaku/Guard/Recording` 订阅，保障录播订阅主播 mid→roomId 资料落库）/`LiveStatusJob`/`SpaceTimelineJob`（捕获 `CrawlBackoffActiveException` 跳过退避期，均依赖 `ICookieServiceClient`）、`SubscriptionEventSubscriber`（订阅 Redis 订阅事件频道，将 api 进程创建的订阅桥接为进程内 `Subscribed`，触发 `AccountInfoJob` 即时抓取，避免依赖小时级周期兜底）、`LiveGuardJob`（翻页逻辑已抽取至 `LiveGuardListFetcher`，依赖 `ICookieServiceClient`）、`WeiboTimelineJob`（微博动态周期抓取：订阅驱动 + 即时补抓 + 退避门控 `weibo:{uid}` + 收敛式翻页 + MblogId 幂等落库；风控分支：登录 cookie 命中 → `report-invalid` 阻塞宣告，200 自愈重试一次 / 410/404/504 放弃本轮并退避；匿名命中 → 维持 `RenewAsync` + 退避）
- `Weibo/`：微博抓取链路（与 B 站隔离）：`WeiboApiClient`/`IWeiboApiClient`（m.weibo.cn getIndex）、`WeiboLoginCookieProvider`（主凭证：经 `ICookieServiceClient` 调 `/api/cookie/weibo/checkout`，按 cookieId 复用 `CookieCallGate` per-cookie 互斥；404 返回“无可用”信号不抛异常）、`WeiboCookieSource`（主/兜底组合：`UseLoginCookie=true` 且有货走登录 cookie，否则回退访客并记 warning）、`WeiboVisitorCookieProvider`（兜底：访客 SUB/SUBP，genvisitor2 一步 + 三步握手 fallback、定时刷新、失效重建）、`WeiboRiskControlClassifier`/`WeiboRiskControlException`（风控识别；异常携带 `CookieId`/`IsLoginCookie`）、`WeiboMblogParser`（Ruby `created_at` 解析 + card 提取）
- `Live/`：`LiveEventSource`（弹幕长连接，归一化落库 `bilibili_live_event` 同时向 Redis 发布）、`DanmakuClientFactory`、`DanmakuEventNormalizer`、`ILiveEventPublisher`/`RedisLiveEventPublisher`（频道 `live-event:{roomId}`）
- `Services/`：`AccountInfoCrawler`（场次合并去重）、`CookieServiceClient`（`ICookieServiceClient` 实现，HTTP 客户端调用 cookie 服务的 `/api/cookie/checkout`、`/api/cookie/report-invalid`、`/api/cookie/cooldown` 接口）、`LiveGuardListFetcher`（翻页抓取 + top3 并入 + 页间延迟/退避，供 `LiveGuardJob` 与补录共用）、`LiveGuardTop3BackfillPlanner`（top10 选取与差异计算纯函数）、`LiveGuardTop3BackfillService`（补录编排 + per-mid 进度标记，依赖 `ICookieServiceClient`）
- `Video/`：B 站投稿视频实时取流下载（本服务首个 Controller）+ 外部 S3 cache-aside 缓存：`VideoDownloadController`（`GET /api/video-download/{bvid}?kind=video|audio&page=`，回写状态码与 `Content-Type`/`Content-Disposition`/`Accept-Ranges`/`Content-Range`/`Content-Length` 后 `CopyToAsync(Response.Body)`，错误统一 `{error:中文原因}`）、`VideoStreamService`（编排 `ICookieServiceClient.CheckoutAsync` → `IBiliApiClient.GetVideoPlayInfoAsync` → 按 `kind` 选流（DASH 取最高档、flv 回退整段；flv 下 `kind=audio` 报 400）→ 独立 named HttpClient `VideoStream`（`Timeout=InfiniteTimeSpan`，仅 Referer+UA、**不挂 `RiskControlHandler`**）以 `ResponseHeadersRead` 打开 B 站 CDN 原始流并透传 `Range`/`If-Range`，`RequestAborted` 联动取消，全程不落盘、不整段缓冲；B 站 code→中文原因映射为可测纯函数，Cookie 锁仅覆盖取数阶段不覆盖流式传输；cache-aside：参数校验后先查缓存命中直出，未命中回源且仅「无 Range + 上游 200」时边透传边写缓存，缓存故障降级不阻断）、`IVideoObjectCache`/`VideoCacheModels`（缓存端口与键/元数据/If-Range/Range 纯函数）、`S3VideoObjectCache`（AWSSDK.S3：GetObjectMetadata+GetObject 读、multipart 写、abort；全异常降级）、`S3AvailabilityProbe`（启动期 `ListObjectsV2(MaxKeys=1)` 探测，不可用抛 `S3UnavailableException` 拒绝启动）、`NoOpVideoObjectCache`（未启用时空实现）、`CachingReadStream`（边读上游边写缓存，EOF 完成、异常/中断 abort）、`VideoDownloadFileName`（文件名与 `filename*` 纯函数，Sanitize 同 recording 规则）、`VideoDownloadModels`（领域异常与下载结果，支持同步/异步释放）
- `Configuration/`：`BilibiliCrawlerOptions`（节 `BilibiliCrawler`：`FetchMode`/退避/冷却/拟真头参数）、`WeiboCrawlerOptions`（节 `WeiboCrawler`：`TimelineIntervalSeconds`/超时/凭证刷新周期/`MaxPagesPerCrawl`/UA/独立退避参数/`UseLoginCookie`（默认 true）/`LoginCookieRetryOnce`（默认 true））、`S3Options`（节 `S3`：`Enabled`（默认 false）/`ServiceUrl`/`Bucket`/`AccessKey`/`SecretKey`/`Region`/`ForcePathStyle`（默认 true，MinIO 兼容）/`Prefix`（默认 `video-cache`）；仅 Enabled=true 时校验必填项）
- `Program.cs`：`WebApplication` 宿主；注册 Mongo/Redis 客户端、Options 校验、仓储、共享服务、抓取通道/风控/退避、`ILiveEventPublisher`、Job 与 `LiveEventSource` 的 `AddHostedService`；`CookieServiceClient`（`ICookieServiceClient`）HTTP 客户端注册（指向 cookie 服务）；`LiveGuardListFetcher` 与 `LiveGuardTop3BackfillService` 注册；微博链路注册（`WeiboSubscriptionRepository`/`WeiboTimelineRepository`/`WeiboSubscriptionService`/`WeiboLoginCookieProvider`/`WeiboCookieSource`/`WeiboVisitorCookieProvider`/`WeiboApiClient`/`WeiboTimelineJob`）；`SubscriptionEventSubscriber` 注册；`AddControllers()`+`MapControllers()` 与 `VideoStreamService`、named HttpClient `VideoStream`（超时 Infinite）注册（视频下载取流端点）；`IVideoObjectCache` 按 `S3:Enabled` 注册 `S3VideoObjectCache`（共用单例 `IAmazonS3`）或 `NoOpVideoObjectCache`；启用时注册 `IS3AvailabilityProbe` 并在启动前探测 S3（不可用拒绝启动）；启动执行幂等索引初始化与回填；`/health`（进程存活 + MongoDB 连通性，`MongoDbHealthCheck`）
- `Dockerfile`：两阶段构建（aspnet 运行时，EXPOSE 8080），restore 前同时拷贝 common 与 crawler csproj
- 镜像流水线：`.github/workflows/build-crawler-image.yml`（`zero-tools/z-vtuber-crawler`）

## 事件通道语义

crawler 在事件落库同时 `PUBLISH live-event:{roomId}`；api 侧按需订阅/退订。Redis pub/sub 为 fire-and-forget，事件可靠性以 MongoDB 落库为准，发布失败仅记日志不影响落库与长连接。

## Cookie 服务依赖

crawler 通过 `ICookieServiceClient`（实现 `CookieServiceClient`）HTTP 客户端调用 cookie 服务（`CookieService:BaseUrl`），进行 Cookie 领取（checkout）、失效报告（report-invalid）、冷却标记（cooldown）。不再直接访问 MongoDB Cookie 池。微博链路使用 `CheckoutWeiboAsync`/`ReportWeiboInvalidAsync`/`CooldownWeiboAsync`（`/api/cookie/weibo/*`）；微博 checkout 无可用时返回“无可用”信号（不抛 `NoAvailableCookieException`），由 `WeiboCookieSource` 回退匿名凭证。

## 分布式锁迁移

`CookieCallGate` 和 `CrawlLockProvider` 已迁移至 `IDistributedLockProvider` 接口（common 定义），具体实现 `RedisDistributedLockProvider` 在 cookie 服务中。crawler 通过 DI 注入 `IDistributedLockProvider` 实例。

## 视频 S3 缓存

投稿视频下载采用 cache-aside（`IVideoObjectCache`）：键为 `{S3:Prefix}/videos/{bvid}/p{page:D2}/{video|audio}`（默认前缀 `video-cache`），对象元数据携带 `Content-Type` 与 `content-disposition`。命中时经 `GetObjectMetadata`+`GetObject`（`Range`/`If-Range`/416 语义，If-Range 校验与 416 判定为纯函数）直接流式返回，不触达 B 站与 Cookie 服务；未命中时回源 B 站，仅「调用方无 `Range` + 上游 200」以 multipart 边透传边写入（5MB 分片，EOF 完成、异常/中断 abort）。缓存为尽力而为：**运行时**读写/启停任何异常都降级为无缓存，不影响下载主链路；不落本地磁盘。**启动期**则严格：`S3:Enabled=true` 时先探测 Bucket 可达，不可用即拒绝启动（`S3UnavailableException`，进程非零退出）。

## 裸部署配置

- `ConnectionStrings:mongo`：MongoDB 连接串
- `ConnectionStrings:messaging`：Redis 连接串
- `BilibiliCrawler` 节：抓取周期/风控/退避配置（`ValidateOnStart`）
- `WeiboCrawler` 节：微博抓取间隔/凭证刷新/退避等配置（`ValidateOnStart`）
- `S3` 节：视频缓存对象存储配置（`Enabled`/`ServiceUrl`/`Bucket`/`AccessKey`/`SecretKey`/`Region`/`ForcePathStyle`/`Prefix`，`ValidateOnStart`；默认 `Enabled=false` 关闭缓存；`Enabled=true` 时启动前探测 Bucket，不可用拒绝启动）
- 健康检查端口：默认 8080（Kestrel）

## 开发流程

- 跑测口径：遵循 `docs/engineering/common-rules.md`「测试性能基线与超时护栏」小节（单用例 ≤5s；报告工具 `tests/Z.Vtuber.TestReport --check` 护栏）
- TDD 开发（单元测试：`tests/Z.Vtuber.Tests`，Testcontainers MongoDB + Redis 集成测试）
- 参考：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`
