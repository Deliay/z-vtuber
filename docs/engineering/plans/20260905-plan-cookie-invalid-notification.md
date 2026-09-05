---
description: Cookie 失效通知实现 Plan——基于 PRD v1.0 与技术提案 v1.0（Satori 协议 HTTP 直连修订版）的实施阶段划分、任务分解与验收对照；common 新增事件模型/频道常量/订阅实体与仓储，crawler 失效路径发布 Redis 事件，新增 Z.Vtuber.Notifier（Satori 协议直连 + 订阅泵 + 投递 + 配置 API），Web 新增「通知」模块，含契约/Aspire/Dockerfile/CI 接入
type: Permanent
---

# Cookie 失效通知 - 实现 Plan

**功能名称**: Cookie 失效通知
**关联 PRD**: `docs/product/draft/20260904-cookie-invalid-notification-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20260904-proposal-cookie-invalid-notification.md`（v1.0，commit 2d4450b 修订：Satori 接入改为协议 HTTP 直连，移除 Satori.NET）
**Plan 版本**: v1.1
**创建日期**: 2026-09-05
**更新日期**: 2026-09-05（v1.1：依据打回评估结论同步上游提案修订——移除 Satori.NET 依赖，`SatoriApiClient` 以 HttpClient 直连协议并承担 `message.create` 投递，删除 `SatoriClientHostedService`；请求头按现行协议规范 `Satori-Platform`/`Satori-User-ID`；新增独立 `SatoriApiClientTests` 任务）
**作者**: Deliay
**feat-branch**: `feat/cookie-invalid-notification`（需求总分支）

## 1. 概述

本 Plan 基于 PRD v1.0 与技术提案 v1.0（协议直连修订版）编写，指导 Cookie 失效通知的编码实施。改动分五块：① common 新增事件模型/频道常量/订阅实体与仓储/集合初始化；② crawler 失效路径挂接 Redis 发布（fire-and-forget）；③ 新增 `servers/notifier`（Z.Vtuber.Notifier：Satori 协议 v1 HTTP 直连 + Redis 订阅泵 + 投递 + 配置 API，无第三方 Satori SDK）；④ `apps/web` 新增「通知」模块与同源代理；⑤ 工程接入（契约/slnx/Aspire/Dockerfile/GitHub Action）。实施全程遵循 `docs/engineering/common-rules.md`（TDD、契约式开发、核心逻辑覆盖率 ≥ 80%）、`backend-rules.md`、`frontend-rules.md` 及各子项目 AGENTS.md。

### 1.0 上游核实结论（代码现状复查）

对当前需求总分支代码核实，提案技术路径**整体可行**，但发现四处与现状的偏差，Plan 已在对应任务中给出处理（v1.1 复查：提案修订仅涉及 notifier 的 Satori 接入方式，分支自 60d624d 起仅文档提交，以下代码现状结论不变）：

- **失效分支上下文核实**：`CookieNavProbeService.ProbeAsync` offline 分支（`servers/crawler/Services/CookieNavProbeService.cs:62-67`）持有完整 `CookieRecord`（含 `Id`），可直接构造事件；`BilibiliAccountProvider` 失效分支（`servers/crawler/Crawlers/BilibiliAccountProvider.cs:47-52`）**只有 cookie 原始字符串，拿不到 cookie id**，而 `ICookieAccountInfoRepository.GetByCookieIdsAsync`（`servers/common/Persistence/CookieAccountInfoRepository.cs:7-12`）仅支持按 `ObjectId` 批量反查，`ICookiePoolService`（`servers/common/Services/CookiePoolService.cs:8-14`）也没有按 cookie 字符串反查记录的方法。又因 payload 不得含 cookie 原文，不能拿 cookie 串当事件主键。**处理**：`ICookiePoolService` 新增 `FindByCookieAsync(string cookie, CancellationToken)` 按 cookie 字符串反查 `CookieRecord`（Mongo 查询，不涉及基础设施依赖，不违反 common 归属约定）；`BilibiliAccountProvider` 失效分支先反查再发布，查不到则记日志跳过（见任务 2.2/2.4）。
- **Redis 事件序列化并非 snake_case**：既有 `LiveEvent`（`servers/common/Live/LiveEvent.cs:3`）为无 `JsonPropertyName` 的 positional record，发布（`RedisLiveEventPublisher.cs:21`）与订阅（`LiveEventBus.cs:145`）均用默认 `JsonSerializer`，线上格式实为 PascalCase。提案「snake_case 与既有保持一致」前提不成立。**处理**：`CookieInvalidEvent` 以 `[JsonPropertyName]` 显式标注 snake_case 字段名（cookie_id/mid/uname/occurred_at/reason），发布/订阅两侧同用默认 options 即可对齐（见任务 2.1）。
- **Mongo 实体无 `[BsonElement]` 惯例**：既有实体均为 PascalCase 属性直落库。提案数据模型表的 snake_case 字段名（event_type/channel_id 等）需在新实体上显式 `[BsonElement]`，否则落库为 PascalCase（见任务 2.1）。
- **测试夹具需同步**：`MongoDbFixture.ResetAsync`（`tests/Z.Vtuber.Tests/MongoDbFixture.cs:27-40`）显式枚举集合名逐个 Drop，新增 `notification_subscriptions` 集合须加入该清单（见任务 1.1）。
- 其余假设全部核实一致：发布器模式（`RedisLiveEventPublisher`：literal `RedisChannel` + catch-all 记日志，:12-27）、订阅泵模式（`LiveEventBus`：`ChannelMessageQueue` + `await foreach` + 2s 重订阅，:123-209）、crawler Program.cs DI 形态（`AddMongoDBClient("mongo")`/`AddRedisClient("messaging")`/`MapHealthChecks("/health")`，:12-96）、Dockerfile 两阶段模板（api/crawler 同形 16 行）、镜像 workflow 模板（`build-crawler-image.yml`）、slnx 结构、AppHost 编排（确认当前无 `AddParameter`，本需求首次引入）、web 侧 `App.tsx` menuItems（:6-17）/`client.ts` request 封装（:17-51）/vite proxy（:11-16）/nginx `location /api`（:23-30）/`docker-entrypoint.sh` envsubst（:4-6）/codegen 单条命令（`package.json:12`）、测试设施（`[Collection("mongo")]`/`[Collection("redis")]`，测试 csproj 已引用 api/common/crawler，需追加 notifier 引用）、全部项目 net10.0。
- 两处编辑注意：`infra/apphost/Z.Vtuber.AppHost.csproj` 为 **CRLF 行尾**，编辑时保留；`LiveEventBus` 脏消息为静默丢弃，notifier 泵「记 warning 后丢弃」属增强，不冲突。

### 1.1 实施范围

- **做**：
  - 契约：`contracts/notifier.openapi.yaml`（新增，先于实现）。
  - common：`CookieEventChannels`、`CookieInvalidEvent`、`NotificationSubscriptionRecord`、`NotificationSubscriptionRepository`、`MongoCollectionNames.NotificationSubscriptions` + 唯一索引初始化；`ICookiePoolService.FindByCookieAsync`。
  - crawler：`ICookieInvalidEventPublisher`/`RedisCookieInvalidEventPublisher`；`CookieNavProbeService` 与 `BilibiliAccountProvider` 失效分支挂接；`Program.cs` 注册。
  - notifier 新服务：`SatoriOptions`（endpoint fail-fast）、`SatoriApiClient`（`HttpClient` + `System.Text.Json` 直连协议 `guild.list`/`channel.list`/`message.create`，含翻页与纯文本 XML 转义，**无第三方 Satori SDK、无 WebSocket 生命周期管理**）、`SatoriModels`、`CookieInvalidEventSubscriber`（常驻泵）、`NotificationDispatchService`、`NotifyConfigController`、`Program.cs`（minimal Kestrel 8080 + `/health`）。
  - web：「通知」页面（绑定列表/新增绑定弹窗/删除二次确认）、client.ts 新增 notify API、`schema-notify.d.ts` codegen、vite/nginx/entrypoint 代理接入、`App.tsx` 菜单项。
  - 工程接入：slnx、AppHost（`AddParameter` + notifier 编排 + web `VITE_NOTIFY_URL`）、`servers/notifier/Dockerfile`、`.github/workflows/build-notifier-image.yml`。
  - 单元/集成/前端测试，覆盖率达标且无回归。
- **不做**：通知类型通用框架化（仅 `cookie_invalid`）；配置 API 鉴权；事件去重/聚合；api 服务聚合转发 notifier API；Satori 服务器部署与账号接入；Satori WebSocket 事件流消费。

### 1.2 现状基线

- **失效路径**：`CookieNavProbeService.ProbeAsync` offline 分支（先 `MarkOfflineAsync` 落库、后返回 `Offline`）与 `BilibiliAccountProvider` 失效分支（`MarkOfflineAsync` 后 rethrow）为仅有的两处「cookie 失效」语义挂接点；`MarkCooldownAsync`（`CookieNavProbeService.cs:58`、`ResilientBiliApiClient.cs:176`）为风控冷却，**不发布**。
- **发布/订阅先例**：crawler `RedisLiveEventPublisher`（接口 + 单实现、构造注入 `IConnectionMultiplexer`、catch-all warning）；api `LiveEventBus`（泵 + 2s 重订阅 + finally 退订）。notifier 的泵为**常驻订阅**（启动即订阅、停止即退订），不同于 LiveEventBus 的按需订阅。
- **持久化先例**：`MongoCollectionNames` 与 `MongoDbInitializer` 同文件（`MongoDbInitializer.cs:6-19`），唯一索引写法参照 `EnsureCookieAccountInfoIndexesAsync`（:164-170）；仓储构造 `database.GetCollection<T>(MongoCollectionNames.X)`；`EnsureIndexesAsync` 由 crawler 启动块（`Program.cs:81-94`）与 api 启动时执行，notifier 启动时同样需执行（订阅集合索引由 notifier 保证建立，crawler/api 侧既有初始化不变）。
- **账号信息反查**：`CookieAccountInfoRepository.UpsertAsync` 由 `CookieNavProbeService` 探活时写入昵称/MID；失效事件发布时按 CookieId 经 `GetByCookieIdsAsync` 反查，查不到 `Mid`/`Uname` 为 `null`，消息文案降级展示 CookieId 尾段。
- **前端先例**：`SubscriptionPage` 行内两阶段删除确认（`confirmingCancel` state，:176-202）沿用为删除交互；新增绑定弹窗为本仓库首个弹窗组件，无现成先例，样式对齐既有页面（侧边栏布局/表格/按钮）。
- **测试基线**：`CookieNavProbeServiceTests`、`CookiePoolServiceTests`、`RedisLiveEventPublisherTests`、`RedisLiveEventBusTests` 为直接参照；`Fakes.cs` 已有 `MockCookieAccountInfoRepository`/`MockCookiePoolService` 可复用或扩展。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash）与 Conventional Commits：

- 本需求开发全程只使用需求总分支 `feat/cookie-invalid-notification`：契约、实现、测试、文档全部直接在该分支提交，不切开发子分支（沿用近两期需求的既定做法；提案头部「实现分支另行切出」以本 Plan 的既定做法为准）。
- 需求总分支已推送 origin，需求总 PR 已存在：`https://github.com/Deliay/z-vtuber/pull/69`，提交照常推送更新分支。
- 提交信息使用 Conventional Commits：契约 `feat:`、测试先行 `test:`、实现 `feat:`、工程接入 `chore:`/`feat:`、文档 `docs:`；测试与实现分开提交。
- 并行 subagent 按子项目划分（common+crawler / notifier / apps-web），每个 subagent 在自己完成后于需求总分支提交；涉及同一文件的任务（如测试 csproj、MongoDbFixture）归入主串行线，避免并行冲突（见第 4 节）。

## 3. 实施阶段

按「契约先行 → TDD 红-绿 → 工程接入 → 收口验证」推进。Phase 0 串行先行；Phase 1/2 按子项目并行（common 先行串行，crawler/notifier 依赖 common 类型）；Phase 3 与后端实现可部分并行；Phase 4 串行收口。

### Phase 0: 契约先行

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | 编写 `contracts/notifier.openapi.yaml`：4 个端点（GET `/api/notify/channels`、GET/POST `/api/notify/subscriptions`、DELETE `/api/notify/subscriptions/{id}`），schema 与提案 3.1 一致——channels 元素 `{platform, guildId, guildName?, channelId, channelName?}`；订阅记录含 `id/eventType/platform/guildId/guildName?/channelId/channelName?/createdAt`；创建请求 `eventType` 枚举 `["cookie_invalid"]`；错误响应（400 非法 eventType / 404 删除不存在 / 503 Satori 不可用）有明确 schema | `contracts/notifier.openapi.yaml`（新） | `npx openapi-typescript` 可成功生成类型；与提案 3.1 逐字段一致 |

### Phase 1: 测试先行（红）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | 测试设施扩展：`Z.Vtuber.Tests.csproj` 追加 `servers/notifier` ProjectReference；`MongoDbFixture.ResetAsync` 集合清单加入 `notification_subscriptions`；`Fakes.cs` 按需扩展（fake 发布器记录调用、fake `ISatoriApiClient` 记录发送、stub `HttpMessageHandler`） | `tests/Z.Vtuber.Tests/Z.Vtuber.Tests.csproj`、`MongoDbFixture.cs`、`Fakes.cs` | 编译通过 |
| 1.2 | `CookiePoolServiceTests` 增补：`FindByCookieAsync` 命中返回记录、未命中返回 `null`、offline/cooldown 记录同样可查 | `tests/Z.Vtuber.Tests/CookiePoolServiceTests.cs` | 对未实现方法编译/运行失败（红） |
| 1.3 | 新建 `RedisCookieInvalidEventPublisherTests`（`[Collection("redis")]`）：① 发布后订阅方收到 payload，`cookie_id`/`mid`/`uname`/`occurred_at`/`reason` 字段名与值正确（snake_case）；② 账号信息缺失时 `mid`/`uname` 为 `null`；③ payload 不含 cookie 原始字符串；④ Redis 异常时静默不抛、仅记日志 | `tests/Z.Vtuber.Tests/RedisCookieInvalidEventPublisherTests.cs`（新） | 红 |
| 1.4 | `CookieNavProbeServiceTests` / `BilibiliAccountProvider` 相关测试增补：offline 分支在 `MarkOfflineAsync` 之后调用发布器（cookieId 与 reason 正确）；cooldown 分支不发布；发布器抛异常（防御）不影响既有返回语义；`BilibiliAccountProvider` 反查不到记录时不发布仅记日志 | `tests/Z.Vtuber.Tests/CookieNavProbeServiceTests.cs` 等 | 红 |
| 1.5 | 新建 `NotificationSubscriptionRepositoryTests`（`[Collection("mongo")]`）：① 增删查；② 按 `event_type` 过滤查询；③ `(event_type, platform, channel_id)` 唯一索引生效（重复插入抛 `MongoWriteException`，由服务层转幂等）；④ 字段以 snake_case 落库（抽查 BSON） | `tests/Z.Vtuber.Tests/NotificationSubscriptionRepositoryTests.cs`（新） | 红 |
| 1.6 | 新建 `CookieInvalidEventSubscriberTests`：① 发布事件→泵收到→反序列化→转发 dispatch（fake）；② 脏消息记 warning 后丢弃不阻塞后续消息；③ 取消时退订退出 | `tests/Z.Vtuber.Tests/CookieInvalidEventSubscriberTests.cs`（新） | 红 |
| 1.7 | 新建 `NotificationDispatchServiceTests`：① 无订阅时忽略不发送；② 多订阅逐频道发送（fake `ISatoriApiClient` 记录 channelId 与文案）；③ 单条发送失败不影响其余订阅；④ 文案含昵称/MID/时间且不含敏感信息，账号信息缺失时降级为 CookieId 尾段；⑤ `Platform`/`SelfId` 未配置时跳过发送记警告 | `tests/Z.Vtuber.Tests/NotificationDispatchServiceTests.cs`（新） | 红 |
| 1.8 | 新建 `NotifyConfigControllerTests`：① 订阅增删查、删除不存在 404；② 非法 `eventType` 400；③ 重复创建幂等返回已有（200/201 按契约）；④ channels 端点聚合各 guild 的频道列表（fake `ISatoriApiClient`），返回元素含 platform/guildId/guildName/channelId/channelName 完整定位信息；⑤ Satori 不可达或未配置登录信息时 503 + 明确错误消息（不静默空列表）；⑥ 缺 `Z_VTUBER_SATORI_ENDPOINT` 时宿主构建因 `ValidateOnStart` 失败 | `tests/Z.Vtuber.Tests/NotifyConfigControllerTests.cs`（新） | 红 |
| 1.9 | 新建 `SatoriApiClientTests`（stub `HttpMessageHandler`）：① 三个端点请求方法与路径正确（POST `/v1/guild.list`、`/v1/channel.list`、`/v1/message.create`），请求头携带 `Authorization: Bearer {token}`（token 为空则不携带）、`Satori-Platform`、`Satori-User-ID`；② 请求体为 snake_case JSON；③ `guild.list`/`channel.list` 按响应 `next` 游标循环翻页直至为空并汇总 `data`；④ `message.create` 请求体 `channel_id` 正确、`content` 纯文本按协议做 XML 转义（`&` `<` `>` `"`）；⑤ 错误映射：401/404 → 抛出可识别的配置/平台异常（上层转 503），5xx/网络异常 → 抛出由调用方处理；⑥ `Platform`/`SelfId` 未配置时调用直接抛明确异常 | `tests/Z.Vtuber.Tests/SatoriApiClientTests.cs`（新） | 红 |
| 1.10 | 前端测试先行：① `client.test.ts` 增补四个 notify 函数的请求路径（`/notify/...`）、方法、请求体与错误处理（`ApiError.serverMessage`）；② 新建 `NotificationPage.test.tsx`：绑定列表渲染与空态、新增绑定弹窗打开时加载频道下拉（label 为「群组 / 频道」）、提交后关闭并刷新、重复绑定提示、频道/列表加载失败的错误态与重试、删除两阶段确认；③ `App.test.tsx` 增补「通知」菜单项渲染切换 | `apps/web/src/api/client.test.ts`、`apps/web/src/pages/notification/NotificationPage.test.tsx`（新）、`apps/web/src/App.test.tsx` | `npm run test` 红 |

### Phase 2: 实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | common 新增（串行先行）：① `Live/CookieEventChannels.cs`：`public const string Invalid = "cookie-invalid"`；② `Live/CookieInvalidEvent.cs`：record（`CookieId`/`Mid`/`Uname`/`OccurredAt`/`Reason`），各属性 `[JsonPropertyName("cookie_id")]` 等显式 snake_case（**偏离提案措辞的修正点**，见 1.0）；③ `Persistence/Entities/NotificationSubscriptionRecord.cs`：`[BsonId] ObjectId Id` + `[BsonElement("event_type"/"platform"/"guild_id"/"guild_name"/"channel_id"/"channel_name"/"created_at")]`，`CreatedAt` 加 `[BsonDateTimeOptions(Kind = Utc)]`；④ `MongoCollectionNames.NotificationSubscriptions = "notification_subscriptions"`；⑤ `NotificationSubscriptionRepository`：`ListAsync`/`GetByEventTypeAsync`/`InsertAsync`/`DeleteAsync`；⑥ `MongoDbInitializer.EnsureNotificationSubscriptionIndexesAsync`：`(event_type, platform, channel_id)` 唯一索引并挂入 `EnsureIndexesAsync`；⑦ `ICookiePoolService.FindByCookieAsync` + `CookiePoolService` 实现（按 cookie 字符串查 `bilibili_cookies`） | `servers/common/Live/CookieEventChannels.cs`（新）、`servers/common/Live/CookieInvalidEvent.cs`（新）、`servers/common/Persistence/Entities/NotificationSubscriptionRecord.cs`（新）、`servers/common/Persistence/NotificationSubscriptionRepository.cs`（新）、`servers/common/Persistence/MongoDbInitializer.cs`、`servers/common/Services/CookiePoolService.cs` | 1.2/1.5 转绿；既有测试无回归 |
| 2.2 | crawler 发布器：`ICookieInvalidEventPublisher.PublishAsync(ObjectId cookieId, string reason, CancellationToken)`；`RedisCookieInvalidEventPublisher` 仿 `RedisLiveEventPublisher`——内部经 `ICookieAccountInfoRepository.GetByCookieIdsAsync` 反查 Mid/Uname（查不到为 null），构造 literal `RedisChannel(CookieEventChannels.Invalid)`，默认 options 序列化，`GetSubscriber().PublishAsync`，catch-all 记 warning 绝不外抛；`Program.cs` 注册 | `servers/crawler/Services/RedisCookieInvalidEventPublisher.cs`（新）、`servers/crawler/Program.cs` | 1.3 转绿 |
| 2.3 | crawler 挂接（依赖 2.1/2.2）：① `CookieNavProbeService.ProbeAsync` offline 分支在 `MarkOfflineAsync` 之后 `await` 发布（reason 取探活失败语义如 `nav_probe_failed`），保持「先落库、后发布」；② `BilibiliAccountProvider` 失效分支先 `FindByCookieAsync` 反查记录，命中则发布（reason 如 `account_provider_offline`），未命中记日志跳过；③ 代码注释标注「后续新增 `MarkOfflineAsync` 调用点需同步挂接发布」 | `servers/crawler/Services/CookieNavProbeService.cs`、`servers/crawler/Crawlers/BilibiliAccountProvider.cs` | 1.4 转绿；既有用例无回归 |
| 2.4 | notifier 骨架（与 2.2/2.3 并行）：`servers/notifier/Z.Vtuber.Notifier.csproj`（Worker SDK + `FrameworkReference Microsoft.AspNetCore.App` + ProjectReference common + `Aspire.StackExchange.Redis`、`Aspire.MongoDB.Driver`，**不引用任何 Satori 第三方 SDK**）；`Program.cs`：`WebApplication` 宿主、`AddRedisClient("messaging")`/`AddMongoDBClient("mongo")` + `IMongoDatabase` 工厂、`SatoriOptions` 显式映射 `Z_VTUBER_SATORI_ENDPOINT/TOKEN/PLATFORM/SELF_ID` 并 `Validate(endpoint 非空).ValidateOnStart()`、注册仓储/后台服务/控制器、启动执行 `EnsureIndexesAsync`、`AddHealthChecks` + `MapHealthChecks("/health")`（8080） | `servers/notifier/Z.Vtuber.Notifier.csproj`（新）、`servers/notifier/Program.cs`（新）、`servers/notifier/Satori/SatoriOptions.cs`（新） | 缺 endpoint 启动失败用例（1.8⑥）转绿 |
| 2.5 | notifier Satori 协议客户端：`ISatoriApiClient`/`SatoriApiClient` 以 `HttpClient`（BaseAddress=endpoint）+ `System.Text.Json` 直连协议 v1 HTTP API——POST `/v1/guild.list`、`/v1/channel.list`（配置 API 频道查询使用）与 POST `/v1/message.create`（投递使用）；请求头 `Authorization: Bearer {token}`（token 可空则不携带）、`Satori-Platform: {platform}`、`Satori-User-ID: {selfId}`；请求/响应均 snake_case；`guild.list`/`channel.list` 按分页 `List` 对象的 `next` 游标循环翻页；`message.create` 的 `content` 为消息元素字符串，纯文本按协议做 XML 转义（`&` `<` `>` `"`）；`Platform`/`SelfId` 缺失时对应方法抛明确异常由上层转 503/降级；401/404 映射为配置/平台错误，5xx/网络异常抛出由调用方处理。**无 `SatoriClientHostedService`**——不消费 Satori 事件流、不建立 WebSocket 连接，无任何连接生命周期管理 | `servers/notifier/Satori/SatoriApiClient.cs`（新）、`servers/notifier/Satori/SatoriModels.cs`（新：Guild/Channel/分页 List 响应模型） | 1.9 转绿 |
| 2.6 | notifier 订阅泵与投递：`CookieInvalidEventSubscriber`（`BackgroundService`）仿 `LiveEventBus` 泵——启动即 `SubscribeAsync` 得 `ChannelMessageQueue`，`await foreach` 读消息反序列化 `CookieInvalidEvent` 转发 dispatch，脏消息记 warning 丢弃，断线 2s 重订阅，停止时 queue 级 `UnsubscribeAsync`；`NotificationDispatchService` 按 `event_type` 查 Mongo 订阅，无订阅记 debug 忽略，逐条经 `ISatoriApiClient` 的 `message.create(channelId, 文案)` 发送，单条失败仅记日志继续下一条；文案按提案 3.2（账号/时间/提示语，不含敏感信息，账号信息缺失降级 CookieId 尾段） | `servers/notifier/Events/CookieInvalidEventSubscriber.cs`（新）、`servers/notifier/Dispatch/NotificationDispatchService.cs`（新） | 1.6/1.7 转绿 |
| 2.7 | notifier 配置 API：`NotifyConfigController` 统一前缀 `/api/notify`——GET `/channels` 聚合 `ISatoriApiClient` 的 guild.list + 各 guild channel.list 结果（Satori 不可用/未登录 503 + 明确消息）；GET `/subscriptions` 全量列表；POST `/subscriptions` 校验 `eventType == "cookie_invalid"`（否则 400）、`(eventType, platform, channelId)` 重复按幂等返回已有（捕获唯一索引冲突转查已有）；DELETE `/subscriptions/{id}` 存在 204 / 不存在 404；响应与契约一致 | `servers/notifier/Controllers/NotifyConfigController.cs`（新） | 1.8①-⑤ 转绿 |
| 2.8 | 前端实现（与后端并行，仅依赖 Phase 0 契约）：① `package.json` codegen 扩展为两条命令，生成 `src/api/schema-notify.d.ts`；② `client.ts` 新增 `listNotifyChannels`/`listNotificationBindings`/`createNotificationBinding`/`deleteNotificationBinding`（路径 `/notify/...`，复用 `request<T>`）；③ `src/pages/notification/NotificationPage.tsx`：绑定列表表格（通知类型/平台/群组/频道/绑定时间/行内删除两阶段确认）、空态、「新增绑定」弹窗（打开时加载频道下拉，label `{guildName ?? guildId} / {channelName ?? channelId}`，提交体含完整定位信息；加载失败错误态 + 重试；创建成功含幂等 200 关闭刷新；重复绑定提示「该频道已绑定」）、页面级加载失败提示；④ `App.tsx` 扩展 `PageKey` 与 `menuItems` 新增「通知」 | `apps/web/package.json`、`apps/web/src/api/client.ts`、`apps/web/src/api/schema-notify.d.ts`（生成）、`apps/web/src/pages/notification/NotificationPage.tsx`（新）、`apps/web/src/App.tsx` | 1.10 转绿；`npm run lint`/`npm run build` 通过 |

### Phase 3: 工程接入（编排 / Docker / CI / 代理）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 3.1 | slnx 接入：`/servers/` 文件夹新增 `notifier/Z.Vtuber.Notifier.csproj` | `servers/Z.Vtuber.slnx` | `dotnet build servers/Z.Vtuber.slnx` 通过 |
| 3.2 | Aspire 编排：AppHost.csproj 新增 notifier ProjectReference（**保留 CRLF 行尾**）；`AppHost.cs` 新增 `AddParameter("satori-endpoint", secret: true)` 等 Satori 参数（首次引入 `AddParameter`，参数值经环境/user-secrets 提供）+ `AddProject<Projects.Z_Vtuber_Notifier>("notifier").WithReference(mongo).WaitFor(mongo).WithReference(messaging).WaitFor(messaging).WithEnvironment("Z_VTUBER_SATORI_ENDPOINT", ...).WithHttpHealthCheck("/health")`；web 资源追加 `.WithReference(notifier).WithEnvironment("VITE_NOTIFY_URL", notifier.GetEndpoint("http"))` | `infra/apphost/Z.Vtuber.AppHost.csproj`、`infra/apphost/AppHost.cs` | Aspire 编排可一键启动 notifier 与既有服务 |
| 3.3 | Dockerfile：`servers/notifier/Dockerfile` 照抄 crawler 两阶段模板（restore 前同时拷贝 common 与 notifier csproj → publish → `aspnet:10.0`，`EXPOSE 8080`，入口 `Z.Vtuber.Notifier.dll`） | `servers/notifier/Dockerfile`（新） | `docker build` 成功 |
| 3.4 | CI：`.github/workflows/build-notifier-image.yml` 复用 `build-crawler-image.yml` 模板（buildx + 私有 registry 登录 + `zero-tools/z-vtuber-notifier:{sha},:latest`，`provenance/sbom: false`），路径过滤 `servers/notifier/**` 与 `servers/common/**` | `.github/workflows/build-notifier-image.yml`（新） | workflow 语法有效（actionlint/yamllint 或 review 确认） |
| 3.5 | 前端代理接入：① `vite.config.ts` 在 `/api` 之前新增 `'/api/notify': { target: process.env.VITE_NOTIFY_URL \|\| 'http://localhost:5100', changeOrigin: true }`；② `nginx.conf.template` 在 `location /api` 之前新增 `location /api/notify { proxy_pass http://${NOTIFY_BACKEND}; }`（沿用 Upgrade/Host 头）；③ `docker-entrypoint.sh` envsubst 变量增加 `NOTIFY_BACKEND`（默认 `notifier:8080`） | `apps/web/vite.config.ts`、`apps/web/nginx.conf.template`、`apps/web/docker-entrypoint.sh` | vite dev 与 nginx 容器两套路由均可达 notifier（随 4.2 验证） |

### Phase 4: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 4.1 | `dotnet test tests/Z.Vtuber.Tests` 全绿（含 `CookieNavProbeServiceTests`/`CookiePoolServiceTests` 等既有回归）；发布器/订阅泵/投递/配置 API/Satori 客户端核心路径覆盖率 ≥ 80%；`npm run test` / `npm run lint` / `npm run build` 全绿 | 全部通过 |
| 4.2 | 编排手工验证（z-aspire-orchestrator）：本地 Aspire 拉起全量服务 → 缺 `Z_VTUBER_SATORI_ENDPOINT` 时 notifier 启动失败且日志明确 → 注入可用 Satori 后经 web「通知」模块查询频道、新增绑定 → 将测试 cookie 置失效，观察目标频道秒级收到通知（含昵称/MID/时间，无敏感信息）→ 无订阅时失效事件静默 → 删除绑定二次确认生效、列表刷新 → vite dev 代理同步验证 | 手工验证记录 |
| 4.3 | AGENTS.md 同步：`servers/crawler/AGENTS.md`（`Services/` 补 `RedisCookieInvalidEventPublisher`、事件通道语义节补 `cookie-invalid` 频道与挂接点约束）；`servers/common/AGENTS.md`（`Live/` 补事件模型与频道常量、`Persistence/` 补订阅实体与仓储、`Services/` 补 `FindByCookieAsync`）；新建 `servers/notifier/AGENTS.md`（技术栈/模块结构/裸部署配置/开发流程，对齐 api/crawler 格式）；`apps/web/AGENTS.md`（目录结构补 notification 页与 `schema-notify.d.ts`、API 代理配置节补 `/api/notify` 与 `VITE_NOTIFY_URL`） | 文档与实现一致 |

## 4. 并行策略

- Phase 0（契约）串行先行——前端 codegen 与后端控制器均依赖契约。
- Phase 1（测试）按子项目并行：1.2-1.4（common+crawler）、1.5-1.9（notifier，依赖 1.1 的夹具扩展先行）、1.10（web）三组互不依赖，可拆 subagent 并行编写；1.1 串行先行。
- Phase 2 中 2.1（common）串行先行——2.2/2.3（crawler）与 2.4-2.7（notifier）均依赖 common 类型；2.1 完成后三组（crawler / notifier / web 2.8）分属不同子项目，按 z-coding 规范拆 subagent 并行实现，各 subagent 负责「代码实现 → 提交 → 单元测试修复 → 提交」闭环。
- Phase 3 的 3.1/3.3/3.4（slnx/Docker/CI）与 3.5（前端代理）互不依赖可并行；3.2（AppHost）依赖 notifier 项目存在（2.4 之后）。
- Phase 4 串行收口。全部提交在需求总分支 `feat/cookie-invalid-notification`。

## 5. 验收对照

| PRD 功能点 | 实现任务 | 验证方式 |
|-----------|---------|---------|
| 功能点 1：通知服务接入 Satori（endpoint 注入、缺失 fail-fast、Aspire、镜像 CI） | 2.4、2.5、3.1-3.4 | 1.8⑥ + 编排验证（4.2）+ `docker build`（3.3） |
| 功能点 2：可用频道查询 API（含平台/群组/频道定位，Satori 不可用明确报错） | 0.1、2.5、2.7 | 1.8④⑤、1.9①-③⑤ + 编排验证（4.2） |
| 功能点 3：通知订阅配置 API（增删查、幂等、持久化、即时生效） | 0.1、2.1、2.7 | 1.5、1.8①-③ + 重启后订阅仍在（4.2） |
| 功能点 4：失效事件发布与通知投递（多频道、无订阅静默、无敏感信息、fire-and-forget） | 2.1、2.2、2.3、2.5、2.6 | 1.3、1.4、1.6、1.7、1.9④ + 失效 cookie 端到端验证（4.2） |
| 功能点 5：后台「通知」管理模块（列表/空态/新增绑定弹窗/删除二次确认/错误态） | 2.8、3.5 | 1.10 + 界面手工验证（4.2） |
| 非功能：crawler 既有行为不变、发布失败不影响落库与采集 | 2.2、2.3 | 1.3④、1.4 + 既有测试全绿（4.1） |
| 非功能：核心逻辑覆盖率 ≥ 80%、全量测试绿 | 4.1 | `dotnet test` / `npm run test` |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| BilibiliAccountProvider 拿不到 cookie id | 该分支仅有 cookie 字符串，仓储仅支持 ObjectId 反查（1.0 核实） | `ICookiePoolService.FindByCookieAsync` 新增反查（2.1⑦）；查不到记日志跳过（2.3②），1.2/1.4 用例锁定 |
| Satori 服务器实现差异 | 不同 Satori 服务器实现的鉴权与分页行为可能有差异（提案 5.2 实现点 2 风险点） | 单元测试以 stub `HttpMessageHandler` 锁定请求头、请求体与翻页逻辑（1.9）；集成与手工验证以真实服务器为准（4.2） |
| Bot 登录信息无法自动发现 | 本服务不建立 WebSocket 连接，无法从事件流 ready 信号自动发现登录信息（提案 6.1） | `Platform`/`SelfId` 显式环境变量配置；缺失时频道查询 503、投递降级记警告（2.5/2.6），endpoint 仍是唯一 fail-fast 项 |
| Satori 服务器可用性 | 外部依赖，本地验证需真实实例 | 单元/集成测试以 stub `HttpMessageHandler` 与 fake `ISatoriApiClient` 覆盖（1.7/1.8/1.9）；手工验证依赖可用 Satori（4.2） |
| nginx 前缀匹配顺序 | `location /api/notify` 若置于 `location /api` 之后会被错误分流 | 3.5② 明确顺序约束，4.2 容器路由验证 |
| 新挂接点遗漏 | 后续新增 `MarkOfflineAsync` 调用点可能忘记发布 | 2.3③ 代码注释 + crawler AGENTS.md 事件通道语义节标注（4.3） |
| CRLF 行尾 | `Z.Vtuber.AppHost.csproj` 为 CRLF | 3.2 编辑时保留行尾 |
| 依赖 | Satori 协议 v1（`HttpClient` + `System.Text.Json` 实现，**无新增第三方包**）；既有 `CookieAccountInfoRepository`/`MongoDbInitializer`；Testcontainers mongo/redis 夹具 | 已核实（1.0） |

## 7. 相关文档

- PRD：`docs/product/draft/20260904-cookie-invalid-notification-prd.md`（v1.0）
- 技术提案：`docs/engineering/proposals/20260904-proposal-cookie-invalid-notification.md`（v1.0，commit 2d4450b 协议直连修订）
- 契约：`contracts/notifier.openapi.yaml`（Phase 0 产出）
- 参考实现：`servers/crawler/Live/RedisLiveEventPublisher.cs`（发布模式）、`servers/api/Live/LiveEventBus.cs`（订阅泵模式）
- Satori 协议 v1：https://satori.js.org/zh-CN/protocol/ （API 规范：https://satori.js.org/zh-CN/protocol/api.html ）；Satori.NET 评估不采用的记录见提案 6.1「Satori 接入方式」决策
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`docs/engineering/frontend-rules.md`、`servers/common/AGENTS.md`、`servers/crawler/AGENTS.md`、`apps/web/AGENTS.md`
