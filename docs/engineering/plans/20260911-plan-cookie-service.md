---
description: 无头浏览器 Cookie 服务（z-vtuber-cookie）实现 Plan——基于 PRD v1.0 与技术提案（feat/cookie-service @093e870）的实施阶段划分与任务分解；契约先行（含 Plan 补齐的 cooldown 端点），common 实体演进与 IDistributedLockProvider 契约落地，新建 servers/cookie（Playwright headless Firefox 扫码登录 + 10 分钟保鲜 + 阻塞式宣告自愈），crawler cookie 取用/失效宣告/风控冷却全部切换 HTTP API 且 CookieCallGate/CrawlLockProvider 迁移 Redis 分布式锁、CookieProbeJob 下线，web「Cookie 管理」页扫码化改造，Aspire/Dockerfile/CI/nginx 工程接入
type: Permanent
---

# 无头浏览器 Cookie 服务（z-vtuber-cookie）- 实现 Plan

**功能名称**: 无头浏览器 Cookie 服务
**关联 PRD**: `docs/product/draft/20260911-cookie-service-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20260911-proposal-cookie-service.md`
**Plan 版本**: v1.0
**创建日期**: 2026-09-11
**作者**: Workflow Agent（代起草）
**feat-branch**: `feat/cookie-service`（需求总分支，已推送 origin；需求总 PR：<https://github.com/Deliay/z-vtuber/pull/78>）

## 1. 概述

本 Plan 基于 PRD v1.0 与技术提案编写，指导「无头浏览器 Cookie 服务」的编码实施。改动分六块：① 契约 `contracts/cookie.openapi.yaml` 新建（先于实现）；② common 演进——`CookieRecord` 新增 `AccountId`/`LastRefreshAt`、`IDistributedLockProvider` 分布式锁契约入 common、删除 `ICookieNavProbeService` 与 `CookiePoolService.NextAsync`；③ 新建 `servers/cookie`（Z.Vtuber.Cookie）：扫码登录浏览器编排、10 分钟定时保鲜、阻塞式失效宣告、cookie 检出分发，全部互斥走 Redis 分布式锁；④ crawler 改造——cookie 取用/失效宣告/风控冷却切换 `CookieServiceClient`（HTTP），`CookieCallGate`/`CrawlLockProvider` 迁移 Redis 分布式锁，`CookieProbeJob` 下线；⑤ web「Cookie 管理」页改造为扫码新增帐号 + session 状态视图；⑥ 工程接入（slnx/Aspire/Dockerfile/CI/nginx/vite）。实施全程遵循 `docs/engineering/common-rules.md`（TDD、契约式开发、核心逻辑覆盖率 ≥ 80%）、`backend-rules.md`、`frontend-rules.md` 及各子项目 AGENTS.md。

### 1.0 上游核实结论（代码现状复查）

对需求总分支 `feat/cookie-service`（HEAD `093e870`）代码核实，提案技术路径**整体可行**，现状细节如下，处理方式在对应任务中给出：

- **cookie 生命周期现状与提案 1.1 描述一致**：`CookiePoolService`（`servers/common/Services/CookiePoolService.cs:8-143`）提供 `AddAsync`/`NextAsync`（`FindOneAndUpdate` 按 `LastUsedAt` 升序原子检出，:60-77）/`MarkOfflineAsync`（原子条件更新返回 `bool`，:108-118）/`GetActiveAsync`/`MarkCooldownAsync`/`FindByCookieAsync`；「冷却」非独立状态，是 `active` + `CooldownUntil` 时间戳（`CookieRecord.cs:25-29` 仅 `active`/`offline`）；无可用时抛 `NoAvailableCookieException`（:18-24）。`NextAsync` 内含首次使用前惰性 nav 探测分支（:84-105，经 `IServiceProvider` 惰性解析 `ICookieNavProbeService` 规避循环依赖）。
- **失效事件发布在 crawler 两处**：`CookieNavProbeService.cs:65-79`（reason `nav_probe_failed`）与 `BilibiliAccountProvider.cs:76-95`（reason `account_provider_offline`）；发布器 `RedisCookieInvalidEventPublisher`（`servers/crawler/Services/RedisCookieInvalidEventPublisher.cs:14-48`）发布前经 `ICookieAccountInfoRepository` 反查 mid/uname；频道常量 `servers/common/Live/CookieEventChannels.cs:5`。cookie 服务实现失效发布时沿用同一格式与反查逻辑（事件实体 `CookieInvalidEvent` 在 common，零改动复用）。
- **crawler cookie 调用点全清单**（迁移面）：`NextAsync` 5 处——`Jobs/LiveStatusJob.cs:108`、`Jobs/SpaceTimelineJob.cs:76`、`Jobs/LiveGuardJob.cs:139`、`Live/LiveEventSource.cs:109`、`Services/LiveGuardTop3BackfillService.cs:61`；`MarkOfflineAsync` 2 处——`CookieNavProbeService.cs:65`（随探测下线删除）、`BilibiliAccountProvider.cs:76`（改宣告 API）；`MarkCooldownAsync` 2 处——`CookieNavProbeService.cs:59`（删除）、`ResilientBiliApiClient.cs:176`（改冷却 API，见 1.0 末条）；`FindByCookieAsync` 1 处——`BilibiliAccountProvider.cs:81`（随宣告改造）；`GetActiveAsync` 1 处——`Jobs/CookieProbeJob.cs:17`（随 Job 下线删除）。
- **风控冷却语义是提案未覆盖的缺口**：`ResilientBiliApiClient.ReportRiskControl`（:171-182）对命中风控的 cookie 调 `MarkCooldownAsync` 使其在冷却期内不被 `NextAsync` 检出。提案的 checkout 契约未含冷却能力，直接切换会让风控中的 cookie 继续被分发。**处理**：本 Plan 在契约中补齐 `POST /api/cookie/cooldown` 端点（见任务 0.1），cookie 服务检出沿用「过滤 `CooldownUntil`」语义（任务 2.7），crawler 冷却调用点切换该端点（任务 2.8）。属实现期细化，不改提案既定架构。
- **既有进程内锁现状**：`CookieCallGate`（`servers/crawler/Services/CookieCallGate.cs:9-39`）`ConcurrentDictionary<string, SemaphoreSlim(1,1)>` 无淘汰，`AcquireAsync` 返回幂等 `IDisposable`；持锁点 3 处——`BilibiliAccountProvider.GetProviderAsync`（:36-40 全程持锁）、`DanmakuClientFactory.CreateWithLockAsync`（`servers/crawler/Live/DanmakuClientFactory.cs:22-39` 返回 `(Client, Release)`）、`LiveEventSource.RunRoomAsync`（:109-117，`ConnectAsync` 成功后立即释放，事件循环不持锁）。`CrawlLockProvider`（`servers/crawler/Jobs/CrawlLockProvider.cs:5-10`）`GetLock(key)` 直接返回 `SemaphoreSlim`，使用点 5 处——`AccountInfoCrawler.cs:23`（`account:{mid}`）、`LiveStatusJob.cs:92`（`live:{roomId}`）、`LiveGuardJob.cs:122`（`guard:{mid}`）、`SpaceTimelineJob.cs:59`（`space:{mid}`）、`WeiboTimelineJob.cs:60`（`weibo:{uid}`）。迁移后调用点接口形态尽量不变（见任务 2.9）。
- **notifier 工程范本可直接复制**：`servers/notifier/Z.Vtuber.Notifier.csproj`（Worker SDK、net10.0、`InternalsVisibleTo Z.Vtuber.Tests`、Aspire.MongoDB.Driver 13.4.6 / MongoDB.Driver 3.9.0 / Aspire.StackExchange.Redis 13.4.6 内联精确版本）、`Program.cs`（`AddMongoDBClient("mongo")`/`AddRedisClient("messaging")` + Options `ValidateOnStart` + 启动 `EnsureIndexesAsync` + `MapHealthChecks("/health")`）、`Dockerfile`（两阶段）、`.github/workflows/build-notifier-image.yml`（push `release/**` + 路径过滤 + buildx 推送 `{endpoint}/zero-tools/z-vtuber-notifier:{sha}`）。差异：cookie 服务是 Web API（`Microsoft.NET.Sdk.Web` + Controllers + OpenApi，参照 api），且运行时需 Playwright Firefox（见任务 3.1）。
- **包管理与 Playwright 可用性已核实**：全仓库无 `Directory.Packages.props`/`packages.lock.json`，包版本内联 csproj 精确版本——cookie 服务 csproj 固定 `Microsoft.Playwright` **1.62.0**（NuGet 最新稳定版；包 target 为 netstandard2.0，与 net10.0 兼容，已核）。代码中无任何既有 Playwright 引用。
- **数据层现状**：`CookieRecord`（`servers/common/Persistence/Entities/CookieRecord.cs:6-23`）无 `[BsonElement]` 标注、日期字段带 `[BsonDateTimeOptions(Kind = Utc)]`——新增字段沿用同形态；`EnsureCookiesIndexesAsync`（`MongoDbInitializer.cs:65-72`）现为 `(Status, LastUsedAt)` 复合索引，挂 `EnsureIndexesAsync`（:30）；api/crawler/notifier 启动均执行 `EnsureIndexesAsync`（幂等、多服务并发安全），cookie 服务沿用同一模式。测试夹具 `MongoDbFixture`/`RedisFixture`（Testcontainers 4.13.0）均已存在，可直接复用。
- **api 侧兼容保留**：`BilibiliCookieController`（`servers/api/Controllers/BilibiliCookieController.cs:7-40`，`GET/POST /api/b/cookie`）与 `BilibiliBackstageController` 的 cookie 聚合（:41-64）本期保留不动（PRD 4.4）；api 不使用 `NextAsync`/`MarkOfflineAsync`，common 收缩对 api 无影响。
- **web 侧现状**：`client.ts` cookie 函数 `listCookies`/`addCookie`（:87-99）与 `listBackstageCookies`（:110-115）；`CookiePage.tsx`（1-163）为 textarea 粘贴录入 + 列表（数据源 `listBackstageCookies`）；`vite.config.ts:11-20` 已有 `/api/notify` 先于 `/api` 的代理先例；nginx 仅模板 `apps/web/nginx.conf.template`（`/api/notify` :23-30 先于 `/api` :32-39）+ `docker-entrypoint.sh:4-7` envsubst（`API_BACKEND`/`NOTIFY_BACKEND`）；`package.json:12` codegen 串行生成三份 schema，cookie 契约追加同命令。
- **测试设施现状**：`tests/Z.Vtuber.Tests` csproj 已引用 api/common/crawler/notifier 四项目（需追加 cookie）；`Fakes.cs` 含 `FakeCookiePoolService`/`FakeCookieNavProbeService`/`ProbeServiceProvider`/`MockCookiePoolService`/`FakeCookieInvalidEventPublisher` 等待适配；既有相关测试文件：`CookiePoolServiceTests`、`CookieCallGateTests`、`CrawlLockProviderTests`、`CookieNavProbeServiceTests`、`CookieProbeJobTests`、`BilibiliAccountProviderTests`、`LiveEventSourceLockTimingTests`、`RedisCookieInvalidEventPublisherTests`、`MongoDbInitializerTests` 等。
- **slnx/AppHost**：`servers/Z.Vtuber.slnx`（Folder 结构，需加 cookie）；`infra/apphost/AppHost.cs`（:1-41）现编排 mongo（`WithDataVolume`）/messaging/api/crawler/notifier/web（`VITE_API_URL`/`VITE_NOTIFY_URL`），业务服务无卷挂载先例——cookie 服务的 session 持久化卷为首例（`WithDataVolume` 或 bind mount，实现期按 Aspire API 定）。

### 1.1 实施范围

- **做**：
  - 契约：`contracts/cookie.openapi.yaml` 新建 v1.0.0（含 Plan 补齐的 cooldown 端点），先于实现。
  - common：`CookieRecord` + `AccountId`/`LastRefreshAt`；`MongoDbInitializer` 增补索引；`Locking/IDistributedLockProvider.cs` 契约；删除 `ICookieNavProbeService` 契约与 `CookiePoolService.NextAsync`（含惰性 probe 分支）。
  - cookie 服务（新建 `servers/cookie`）：骨架 + `Browser/`（扫码登录、nav XHR 监听、持久化上下文恢复）+ `Refresh/`（定时保鲜 + 宣告即时刷新，Redis 分布式锁串行化）+ `Pool/`（round-robin 检出 + 冷却过滤）+ `Locking/RedisDistributedLockProvider` + `Persistence/`（session 实体/仓储）+ Controllers + Options。
  - crawler：`CookieServiceClient`（checkout/report-invalid/cooldown）；上述 1.0 调用点全部切换；`CookieCallGate`/`CrawlLockProvider` 迁移 Redis 分布式锁；`CookieProbeJob`/`CookieNavProbeService` 删除下线。
  - web：codegen 生成 `schema-cookie.d.ts`；`client.ts` 新增 cookie 服务客户端；`CookiePage.tsx` 扫码化改造。
  - 工程：slnx、AppHost（cookie 编排 + session 卷 + `VITE_COOKIE_URL`）、Dockerfile、`build-cookie-image.yml`、vite/nginx/envsubst 代理、codegen 脚本。
  - 测试：全部新增/改写测试，核心逻辑覆盖率 ≥ 80%，无回归。
- **不做**：notifier 任何改动（事件格式不变）；crawler 数据抓取链路（`BiliApiClient`/`BrowserishBiliApiClient`）改动；浏览器参与抓取；api 服务 `/api/b/cookie` 与 backstage cookie 聚合端点下线（本期保留兼容）；多副本 session 迁移（浏览器 session 绑定持有副本，提案决策 7）；watchdog 锁续期（v1 不做，提案决策 7）；统一鉴权。

### 1.2 现状基线

- **新服务范本**：notifier 的 csproj/Program.cs/Dockerfile/CI 四件套直接复制改名；Web API 形态（Controllers/OpenApi）参照 api。
- **round-robin 语义来源**：`CookiePoolService.NextAsync` 的 `FindOneAndUpdate`（`Status==active` + 冷却过滤 + `LastUsedAt` 升序 + `ReturnDocument.Before`）平移至 cookie 服务 `CookieDispatchService`，去除惰性 probe 分支。
- **分布式锁先例**：无既有先例，按提案实现点 5 新建——契约 `IDistributedLockProvider`/`IDistributedLockHandle` 入 common（common 不引用 Redis 包），实现 `RedisDistributedLockProvider`（StackExchange.Redis `LockTakeAsync`/`LockReleaseAsync`，token GUID，TTL 兜底）在 cookie 与 crawler 各自服务内（同构双份，提案明确）。
- **事件发布先例**：cookie 服务的失效发布复刻 `RedisCookieInvalidEventPublisher`（频道/实体/反查昵称/fire-and-forget），reason 扩展 `session_invalid`/`account_removed`/`refresh_nav_missing`。
- **前端先例**：`/api/notify` 代理顺序、codegen 脚本、`NotificationPage` 的弹窗/轮询/错误态交互形态均可对照。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash）与 Conventional Commits：

- 本需求开发全程只使用需求总分支 `feat/cookie-service`：契约、实现、测试、文档全部直接在该分支提交，不切开发子分支（沿用近两期需求既定做法）。
- 需求总分支已推送 origin，需求总 PR 已存在（#78），提交照常推送更新分支。
- 提交信息使用 Conventional Commits：契约 `feat:`、测试先行 `test:`、实现 `feat:`、工程接入 `chore:`、文档 `docs:`；测试与实现分开提交。
- 并行 subagent 按子项目划分（cookie 服务 / crawler / apps-web），共享文件（`Fakes.cs`、common 实体、测试 csproj）归入主串行线先行，避免并行冲突（见第 4 节）。

## 3. 实施阶段

按「契约先行 → TDD 红-绿 → 工程接入 → 收口验证」推进。Phase 0 串行先行；Phase 1 测试先行（设施 1.1 先行，其余按子项目并行）；Phase 2 common（2.1）串行先行，随后 cookie 服务 / crawler / web 三线并行；Phase 3 与实现可部分并行；Phase 4 串行收口。

### Phase 0: 契约先行

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | 新建 `contracts/cookie.openapi.yaml` v1.0.0，统一前缀 `/api/cookie`：① `POST /accounts`（202：`accountId/status/qrCodeImage/expiresAt`；并发登录 session 上限 429）；② `GET /accounts/{accountId}`（200：`status ∈ pending_login/active/expired/invalid`，pending 附 qrCodeImage；404）；③ `GET /accounts`（列表含 `cookieStatus/lastRefreshAt/lastLoginAt/createdAt`，**不含 cookie 原文**）；④ `DELETE /accounts/{accountId}`（204）；⑤ `GET /available`（不含 cookie 原文）；⑥ `POST /checkout`（200：`cookieId/accountId/cookie`；404 `no_available_cookie`）；⑦ `POST /report-invalid`（请求 `cookieId`；200 返回新 cookie `refreshed: true`；410 `session_invalid`；404；504）；⑧ **`POST /cooldown`（Plan 补齐**：请求 `cookieId` + `durationSeconds`，204；404——对应 `ResilientBiliApiClient` 风控冷却语义，见 1.0）；各 schema 与提案 3.1 逐字段一致 | `contracts/cookie.openapi.yaml` | `npx openapi-typescript` 可成功生成类型；8 端点齐备 |

### Phase 1: 测试先行（红）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | 测试设施（串行先行）：tests csproj 追加 cookie 项目引用；`Fakes.cs` 新增 `FakeBrowserSessionManager`（可注入二维码/nav 捕获结果/刷新成败序列）、`FakeDistributedLockProvider`（可控获取成功/超时，记录 acquire/release）；删除 `FakeCookieNavProbeService`/`ProbeServiceProvider`（引用点随 1.10 切换）；`MongoDbFixture`/`RedisFixture` 沿用 | `tests/Z.Vtuber.Tests/Z.Vtuber.Tests.csproj`、`tests/Z.Vtuber.Tests/Fakes.cs` | 编译通过（引用点随后续任务切换） |
| 1.2 | `RedisDistributedLockProviderTests`（Redis 集成）：① 获取/释放后他人可获取；② 持锁期间他人 `waitTimeout` 内未获得返回 null；③ TTL 到期自动释放（短 TTL 实测）；④ 非持有者释放无效（token 比对）；⑤ **跨实例互斥**：两个 `RedisDistributedLockProvider` 实例（模拟多副本）同 key 互斥；⑥ `DisposeAsync` 幂等 | `tests/Z.Vtuber.Tests/RedisDistributedLockProviderTests.cs`（新） | 红 |
| 1.3 | 持久化测试：① `CookieSessionRepositoryTests`——session 增删查、状态迁移、`Status_1`/`CookieId_1` 索引生效；② `MongoDbInitializerTests` 增补——`bilibili_cookies` 新增 `AccountId_1` 索引、`bilibili_cookie_sessions` 索引创建、重复执行幂等 | `tests/Z.Vtuber.Tests/CookieSessionRepositoryTests.cs`（新）、`MongoDbInitializerTests.cs` | 红 |
| 1.4 | 登录流程测试（mock 浏览器层）：① `POST /accounts` 返回 202 + 二维码 + `expiresAt`；② 并发 pending 上限（默认 3）触发 429；③ nav 捕获后 session `pending_login → active`、回填 mid/uname、cookie upsert（active + AccountId + 原文）、`CookieAccountInfo` 同步 upsert；④ `expiresAt` 内未扫码 → `expired`、上下文释放；⑤ `GET /accounts/{id}` 各状态响应形状、404 | `tests/Z.Vtuber.Tests/CookieAccountControllerTests.cs`（新）等 | 红 |
| 1.5 | 定时刷新测试：① 距上次刷新 ≥ 10 分钟的 active session 被触发、未到期不触发；② 刷新成功更新 cookie 原文 + `LastRefreshAt`、失败计数清零；③ 无 nav 访问计数 +1，连续 3 次才判失效：session → invalid、cookie → offline、发布 `cookie-invalid`（reason `refresh_nav_missing`）；④ 单 session 异常不影响其他 session；⑤ 同 session 刷新经 `lock:cookie-refresh:{accountId}` 串行（锁未获得跳过本轮） | `tests/Z.Vtuber.Tests/SessionRefreshJobTests.cs`（新） | 红 |
| 1.6 | 宣告失效测试：① 刷新成功 200 返回新 cookie（`refreshed: true`）；② session 失效 410 + cookie offline + 事件（reason `session_invalid`）；③ cookieId 不存在 404；④ 并发 N 个宣告只触发一次实际刷新、后到者经 `LastRefreshAt` 比对短路获得同一结果；⑤ 等待锁 + 刷新超上限（默认 60s）返回 504 且后台流程继续；⑥ 历史手工 cookie（`AccountId == null`）直接 offline + 事件（兼容旧 reason）返回 410 | `tests/Z.Vtuber.Tests/CookieReportInvalidTests.cs`（新） | 红 |
| 1.7 | 检出与冷却测试：① checkout round-robin 顺序（`LastUsedAt` 升序原子更新）；② 冷却中（`CooldownUntil` 未过期）cookie 不被检出；③ 无可用 404 `no_available_cookie`；④ `POST /cooldown` 写入 `CooldownUntil`（204/404）；⑤ `GET /available`、`GET /accounts` 响应不含 cookie 原文；⑥ `DELETE /accounts/{id}` 清理上下文/目录、cookie offline + 事件（reason `account_removed`） | `tests/Z.Vtuber.Tests/CookieDispatchTests.cs`（新） | 红 |
| 1.8 | crawler `CookieServiceClient` 测试（stub `HttpMessageHandler`）：① checkout 200 映射 cookieId/cookie；404 → 抛既有 `NoAvailableCookieException`；② report-invalid 200 返回新 cookie / 410 返回 session 失效信号 / 404 与 504 分支；③ cooldown 调用路径与请求体；④ 超时与网络异常抛出语义 | `tests/Z.Vtuber.Tests/CookieServiceClientTests.cs`（新） | 红 |
| 1.9 | crawler 锁迁移测试：① `CookieCallGateTests` 改写——Redis 实现下同 cookie 串行、不同 cookie 并行；② `CrawlLockProviderTests` 改写——同 key 互斥跨实例生效；③ `LiveEventSourceLockTimingTests` 适配——`ConnectAsync` 成功前持锁、成功后释放（句柄改 `IAsyncDisposable`） | `tests/Z.Vtuber.Tests/CookieCallGateTests.cs`、`CrawlLockProviderTests.cs`、`LiveEventSourceLockTimingTests.cs` | 红 |
| 1.10 | crawler 调用点切换测试改写：① `BilibiliAccountProviderTests`——构建失败 → 宣告 → 200 拿新 cookie 重建重试一次 / 410 放弃本轮并抛出让 Job 跳过；② `CookieProbeJobTests`/`CookieNavProbeServiceTests` 删除（随下线）；③ `CookiePoolServiceTests` 改写——删除 `NextAsync`/probe 用例，保留 `AddAsync`/`GetActiveAsync`/`MarkOfflineAsync` 原子迁移/`MarkCooldownAsync`；④ 各 Job（LiveStatus/SpaceTimeline/LiveGuard/LiveEventSource/LiveGuardTop3Backfill）取 cookie 切换 `CookieServiceClient` 的测试适配（fake 替换） | 对应测试文件 | 红 |
| 1.11 | 前端测试：① `client.test.ts`——cookie 服务客户端函数路径/方法（`/cookie/accounts*`、`/cookie/available`，经 `VITE_COOKIE_URL` 代理前缀 `/api/cookie`）；② `CookiePage.test.tsx`——session 列表渲染（昵称/状态/时间）与空态、「新增帐号」弹窗展示二维码、2s 轮询至 active/expired、成功提示并刷新列表、过期重新生成、服务不可用明确报错不白屏 | `apps/web/src/api/client.test.ts`、`apps/web/src/pages/cookie/CookiePage.test.tsx` | `npm run test` 红 |

### Phase 2: 实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | common 演进（串行先行）：① `CookieRecord` 新增 `AccountId`（string?）与 `LastRefreshAt`（DateTime?，`[BsonDateTimeOptions(Kind = Utc)]`，沿用无 `[BsonElement]` 现状）；② `MongoDbInitializer.EnsureCookiesIndexesAsync` 增补 `AccountId_1` 普通索引，新增 `EnsureCookieSessionIndexesAsync`（`bilibili_cookie_sessions`：`Status_1`/`CookieId_1`，集合名字符串定义、不依赖 cookie 服务私有实体）并挂 `EnsureIndexesAsync`；③ 新增 `Locking/IDistributedLockProvider.cs`（`AcquireAsync(key, ttl, waitTimeout, ct)` → `IDistributedLockHandle?`；handle `IAsyncDisposable` + `Key`；common 不引用 Redis 包）；④ 删除 `Services/CookieNavProbeService.cs` 契约（`ICookieNavProbeService`/`CookieProbeOutcome`）与 `CookiePoolService.NextAsync`（含惰性 probe 分支与 `IServiceProvider` 解析）；`NoAvailableCookieException` 保留于 common（crawler client 复用） | `servers/common/Persistence/Entities/CookieRecord.cs`、`servers/common/Persistence/MongoDbInitializer.cs`、`servers/common/Locking/IDistributedLockProvider.cs`（新）、`servers/common/Services/CookiePoolService.cs`、`servers/common/Services/CookieNavProbeService.cs`（删） | 1.3② 转绿；1.10③ 转绿；既有测试无回归 |
| 2.2 | cookie 服务骨架：`servers/cookie/Z.Vtuber.Cookie.csproj`（`Microsoft.NET.Sdk.Web`、net10.0、`InternalsVisibleTo Z.Vtuber.Tests`、`Microsoft.Playwright` **1.62.0** 固定 + Aspire.MongoDB/Redis 同 notifier 版本、引用 common）；`Program.cs`（Controllers/OpenApi/HealthChecks/`AddMongoDBClient("mongo")`/`AddRedisClient("messaging")`/`CookieServiceOptions` `ValidateOnStart`/仓储/服务/后台服务注册、启动 `EnsureIndexesAsync`、`MapHealthChecks("/health")`，范本 notifier + api）；`Configuration/CookieServiceOptions.cs`（提案实现点 7 全部配置项，节 `CookieService`）；slnx 加入项目 | `servers/cookie/`（新）、`servers/Z.Vtuber.slnx` | 编译通过；缺配置启动 fail-fast（参照 `StartupDiTests` 先例补断言） |
| 2.3 | cookie 服务分布式锁实现：`Locking/RedisDistributedLockProvider.cs`——`LockTakeAsync(key, Guid token, ttl)`，未获得按 50–200ms 随机间隔重试至 `waitTimeout`；`LockReleaseAsync(key, token)` 仅释放自持锁；handle `DisposeAsync` 幂等；key 调用点统一 `lock:` 前缀 | `servers/cookie/Locking/RedisDistributedLockProvider.cs`（新） | 1.2 转绿（cookie 服务侧实现跑同一测试套件；crawler 侧实现 2.9 复用） |
| 2.4 | cookie 服务持久化：`Persistence/CookieSessionRecord.cs`（提案 4.2 全字段）+ `Persistence/CookieSessionRepository.cs`（状态迁移原子更新、按状态查询、到期 pending 查询） | `servers/cookie/Persistence/`（新） | 1.3① 转绿 |
| 2.5 | 浏览器编排：`Browser/IBrowserSessionManager.cs` 抽象（便于 mock）+ `BrowserSessionManager.cs`（`IPlaywright`+Firefox 单例；`LaunchPersistentContextAsync(Path.Combine(dataDir, accountId))`；登录会话：goto bilibili.com → 点击登录入口 → `Locator("img[alt=\"登录二维码\"]").ScreenshotAsync()` 返回 PNG；挂 `context.Request` 匹配 `**/x/web-interface/nav`，捕获即判定登录成功并取请求头 cookie；`expiresAt` 超时 → expired + 释放上下文）+ `LoginSessionWorker.cs`（pending session 的 nav 监听后台任务，登录锁 `lock:cookie-login:{accountId}`）；重启恢复：扫描 active/invalid 记录**惰性重建**上下文、pending 记录清理；单上下文异常隔离捕获 | `servers/cookie/Browser/`（新） | 1.4 转绿（mock 层）；2.7 完成后端到端编译 |
| 2.6 | 刷新与宣告：`Refresh/SessionRefreshJob.cs`（60s 扫描 active、≥10 分钟触发、单轮内随机延迟数秒错峰；成功更新 cookie + `LastRefreshAt` 清零计数；失败计数 +1、连续 3 次判失效：session invalid + cookie offline + 发布 `cookie-invalid` reason `refresh_nav_missing`）+ `Refresh/InvalidationService.cs`（report-invalid 入口：cookieId 反查 session → `lock:cookie-refresh:{accountId}` 分布式锁 → 锁内 `LastRefreshAt` 晚于入队时间短路返回现值 → 否则即时刷新单次失败即判失效 reason `session_invalid`；历史 `AccountId == null` cookie 直接 offline + 事件返回 410；总超时 `ReportInvalidTimeoutSeconds` 60s → 504）；失效发布器复刻 `RedisCookieInvalidEventPublisher`（反查 mid/uname、fire-and-forget、日志不含原文） | `servers/cookie/Refresh/`（新） | 1.5、1.6 转绿 |
| 2.7 | 分发与控制器：`Pool/CookieDispatchService.cs`（`FindOneAndUpdate` round-robin：`Status==active` + 冷却过滤 + `LastUsedAt` 升序原子更新；无可用抛/返回 `no_available_cookie`；cooldown 写入 `CooldownUntil`）；`Controllers/CookieAccountController.cs`（accounts 四端点，登录并发上限 429）+ `Controllers/CookieDispatchController.cs`（available/checkout/report-invalid/cooldown）+ `Controllers/HealthController.cs`；`CookieAccountInfoRepository`（common 既有）在登录成功时同步 upsert 昵称，保持 api backstage 聚合可用 | `servers/cookie/Pool/`、`servers/cookie/Controllers/`（新） | 1.4-1.7 全绿 |
| 2.8 | crawler cookie 路径切换：① 新增 `Services/CookieServiceClient.cs`（typed HttpClient：checkout/report-invalid/cooldown；404 → `NoAvailableCookieException`；`CookieService` 节配置 BaseAddress， Aspire 经 `WithReference` 注入）；② 5 处 `NextAsync` 调用点改 `CheckoutAsync`（`LiveStatusJob`/`SpaceTimelineJob`/`LiveGuardJob`/`LiveEventSource`/`LiveGuardTop3BackfillService`），返回模型携带 cookieId；③ `BilibiliAccountProvider` 失效分支：构建失败 → `ReportInvalidAsync` 阻塞 → 200 用新 cookie 重建重试一次 / 410 移除缓存并放弃本轮（异常上抛，Job 按既有语义跳过）；④ `ResilientBiliApiClient.ReportRiskControl` 冷却改调 cooldown 端点（保留 cookieTag 日志）；⑤ 删除 `Jobs/CookieProbeJob.cs`、`Services/CookieNavProbeService.cs`（及注册）、`Services/RedisCookieInvalidEventPublisher.cs` 若无调用方则一并删除（cookie 失效发布职责移交 cookie 服务）；`Program.cs` 注册同步清理 | `servers/crawler/Services/CookieServiceClient.cs`（新）、上述调用点文件、`servers/crawler/Program.cs` | 1.8、1.10 转绿；crawler 编译无 `CookiePoolService` 读路径引用 |
| 2.9 | crawler 锁迁移：① 新增 `Locking/RedisDistributedLockProvider.cs`（与 2.3 同构，`ConnectionStrings:messaging`）；② `CookieCallGate` 改为包装 `IDistributedLockProvider`（key `lock:cookie-call:{cookieTag}`，TTL/等待配置化；`AcquireAsync` 返回句柄，`DanmakuClientFactory.CreateWithLockAsync` 的 `Release` 与 `LiveEventSource` 释放点改 `IAsyncDisposable`——`await using`/显式 `DisposeAsync`，时序不变：连接建立后释放）；③ `CrawlLockProvider` 改为 `AcquireAsync(key)` 返回分布式锁句柄（key `lock:crawl:{key}`），5 个使用点适配（`using` → `await using`） | `servers/crawler/Locking/RedisDistributedLockProvider.cs`（新）、`servers/crawler/Services/CookieCallGate.cs`、`servers/crawler/Jobs/CrawlLockProvider.cs`、`servers/crawler/Live/DanmakuClientFactory.cs`、`servers/crawler/Live/LiveEventSource.cs` 及 5 个使用点 | 1.9 转绿；全仓库 `SemaphoreSlim` 在锁用途清零（grep 验证，4.1） |
| 2.10 | web 改造（仅依赖 Phase 0 契约，与后端并行）：① `package.json` codegen 追加 cookie 契约 → `npm run codegen` 生成 `src/api/schema-cookie.d.ts`（勿手改）；② `client.ts` 新增 cookie 服务客户端（`createCookieAccount`/`getCookieAccount`/`listCookieAccounts`/`deleteCookieAccount`/`listAvailableCookies`，base `/api/cookie`）；③ `CookiePage.tsx` 改造：列表数据源切 `GET /api/cookie/accounts`（列：昵称/MID、session 状态、cookie 状态、最近刷新/登录时间，空态）；移除 textarea 录入；「新增帐号」按钮 → 弹窗展示 `qrCodeImage` → 每 2s 轮询 `GET /accounts/{id}` 至 active/expired；成功提示并刷新列表；过期「重新生成」；服务不可用明确错误态；页面风格与既有页面一致 | `apps/web/package.json`、`apps/web/src/api/schema-cookie.d.ts`（生成）、`apps/web/src/api/client.ts`、`apps/web/src/pages/cookie/CookiePage.tsx` | 1.11 转绿；`npm run lint`/`npm run build` 通过 |

### Phase 3: 工程接入

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 3.1 | Dockerfile 与 CI：`servers/cookie/Dockerfile` 两阶段（build 同 notifier；runtime 阶段安装 Firefox：首选 runtime 基底 `mcr.microsoft.com/playwright/dotnet:v1.62.0-noble`，或 aspnet:10.0 + 发布后执行 Playwright CLI `install --with-deps firefox`，实现期验证镜像可启动浏览器）；`EXPOSE 8080`；`.github/workflows/build-cookie-image.yml` 复制 notifier workflow，镜像名 `zero-tools/z-vtuber-cookie`，paths 含 `servers/cookie/**` 与 `servers/common/**` | `servers/cookie/Dockerfile`（新）、`.github/workflows/build-cookie-image.yml`（新） | 本地 `docker build` 成功；workflow 语法有效 |
| 3.2 | Aspire 编排：`AppHost.cs` 新增 `AddProject<Projects.Z_Vtuber_Cookie>()` + `WithReference(mongo)` + `WithReference(messaging)` + `WithHttpHealthCheck("/health")` + session 持久化卷（`WithDataVolume("browser-sessions")` 挂载 `/data/browser-sessions`，与 `CookieServiceOptions.BrowserSessionDataDir` 一致）；web 编排 `.WithReference(cookie)` + `WithEnvironment("VITE_COOKIE_URL", cookie.GetEndpoint("http"))` | `infra/apphost/AppHost.cs` | Aspire 一键拉起含 cookie 的全套环境 |
| 3.3 | web 代理接入：`vite.config.ts` 增加 `/api/cookie` → `VITE_COOKIE_URL \|\| http://localhost:5200`（置于 `/api` 之前）；`nginx.conf.template` 增加 `location /api/cookie { proxy_pass http://${COOKIE_BACKEND}; }`（**置于 `location /api` 之前**，同 `/api/notify` 先例）；`docker-entrypoint.sh` 增加 `COOKIE_BACKEND=cookie:8080` 默认值并加入 envsubst 变量列表 | `apps/web/vite.config.ts`、`apps/web/nginx.conf.template`、`apps/web/docker-entrypoint.sh` | dev 代理与生产 nginx 均正确转发 `/api/cookie` |

### Phase 4: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 4.1 | `dotnet test tests/Z.Vtuber.Tests` 全绿（含既有回归）；cookie 服务核心逻辑（登录/刷新状态机、宣告去重、检出、分布式锁）覆盖率 ≥ 80%；`npm run test`/`lint`/`build` 全绿；grep 确认：crawler 无 `CookiePoolService` 读路径残留、锁用途 `SemaphoreSlim` 清零、`ICookieNavProbeService`/`CookieProbeJob` 零引用 | 全部通过 |
| 4.2 | 编排手工验证（z-aspire-orchestrator）：本地 Aspire 拉起全量服务 → web「Cookie 管理」新增帐号弹二维码 → 手机扫码秒级判登录成功、cookie 入池 → crawler checkout 正常抓取 → 10 分钟定时刷新更新 cookie → 宣告失效：session 有效拿新 cookie 重试成功 / session 失效 410 + notifier 收到失效通知（reason 正确、无 cookie 原文）→ 服务重启 session 恢复无需重扫 → 并发宣告同一 cookie 仅一次刷新 | 手工验证记录 |
| 4.3 | AGENTS.md 同步：新建 `servers/cookie/AGENTS.md`（技术栈/模块结构/API 面/配置表/开发流程，对齐 api/crawler 格式）；更新 `servers/crawler/AGENTS.md`（CookieServiceClient、分布式锁迁移、CookieProbeJob 下线）、`servers/common/AGENTS.md`（CookieRecord 字段、`Locking/` 契约、NextAsync/探测契约删除）、`apps/web/AGENTS.md`（cookie 页扫码化、`schema-cookie.d.ts`、代理）、`docs/engineering/backend-rules.md` 项目结构图补 cookie 服务 | 文档与实现一致 |

## 4. 并行策略

- Phase 0（契约）串行先行——前端 codegen、cookie 控制器、crawler client 均依赖契约。
- Phase 1：1.1（测试设施）串行先行；随后后端测试（1.2-1.10）与前端测试（1.11）互不依赖，可拆 subagent 并行。
- Phase 2：2.1（common）串行先行——三线均依赖 common 实体/契约；2.1 完成后 cookie 服务线（2.2-2.7，线内 2.2 → 2.3/2.4 → 2.5-2.7 大体串行）、crawler 线（2.8/2.9，依赖 2.3 同构实现范式）、web 线（2.10）分属不同子项目，按 z-coding 规范拆 subagent 并行，各 subagent 负责「实现 → 提交 → 测试修复 → 提交」闭环。
- Phase 3（3.1-3.3）与 Phase 2 互不依赖可并行。
- Phase 4 串行收口。全部提交在需求总分支 `feat/cookie-service`。

## 5. 验收对照

| PRD 功能点 | 实现任务 | 验证方式 |
|-----------|---------|---------|
| 功能点 1：新增帐号（扫码登录） | 0.1、2.2、2.4、2.5、2.7 | 1.4 + 扫码全链路手工验证（4.2） |
| 功能点 2：定时刷新保活（10 分钟、连续失败判失效、失效事件） | 2.5、2.6 | 1.5 + 手工验证（4.2） |
| 功能点 3：cookie 获取 API（crawler 切换、round-robin、无可用明确失败） | 0.1、2.7、2.8 | 1.7①-③、1.8①、1.10④ + 手工验证（4.2） |
| 功能点 4：阻塞式失效宣告与即时刷新（去重/串行化/超时上限） | 0.1、2.3、2.6、2.8③ | 1.2、1.6 + 并发宣告手工验证（4.2） |
| 功能点 5：后台「Cookie 管理」页改造 | 0.1、2.10、3.3 | 1.11 + 界面手工验证（4.2） |
| 非功能 4.2：全部互斥走 Redis 分布式锁；crawler 既有进程内锁迁移 | 2.1③、2.3、2.9 | 1.2、1.9 + grep 清零（4.1） |
| 非功能 4.3：cookie 原文不出边界（仅 checkout/report-invalid 对内网返回） | 2.6、2.7 | 1.7⑤ + 事件 payload 断言（1.5/1.6） |
| 非功能 4.4：`cookie-invalid` 事件零改动复用；`/api/b/cookie` 保留兼容 | 2.6（发布器复刻） | 1.5③、1.6②⑥ + notifier 端到端（4.2） |
| 成功标准：重启 session 不丢失；Aspire 编排；CI 镜像 | 2.5、3.1、3.2 | 重启恢复手工验证（4.2） |
| 非功能：核心逻辑覆盖率 ≥ 80%、全量测试绿 | 4.1 | `dotnet test` / `npm run test` |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| 风控冷却语义缺口（提案未覆盖） | `ResilientBiliApiClient` 的 `MarkCooldownAsync` 在提案契约中无对应端点，直接切换会使风控中 cookie 继续分发 | 本 Plan 补齐 `POST /api/cookie/cooldown`（0.1⑧/2.7/2.8④），检出沿用 `CooldownUntil` 过滤（1.7② 锁定） |
| B 站登录页 DOM 依赖 | 二维码定位依赖 `img[alt="登录二维码"]`，B 站改版会破坏登录流程（登录态检测本身走 nav XHR 不受影响） | locator 失败返回明确错误并记日志；二维码截取失败不创建 session；约束 5.2 的「登录态以 nav 为准」不变 |
| Playwright 镜像体积 | Firefox + 依赖使镜像显著增大（数百 MB），registry/部署带宽压力 | 3.1 实现期验证最小可用镜像层；workflow 沿用现有 registry 凭据 |
| 锁无 watchdog | v1 不做续期，持锁超时（如浏览器卡死超过 TTL）后锁自动释放，可能并发刷新同一 session | TTL > 最大持锁时长（90s > nav 30s + 重建余量）配置化；正确性由 `LastRefreshAt` 比对与状态机兜底（提案决策 7）；1.2③ 锁定 TTL 行为 |
| 分布式锁同构双份实现 | common 不引用 Redis 包，cookie/crawler 各存一份 `RedisDistributedLockProvider` | 1.2 同一测试套件两侧复跑，防口径漂移；提案已明确该取舍 |
| session 绑定单副本 | user-data-dir 卷绑定持有副本，多副本 session 迁移不在本期 | 提案决策 7 已声明边界；锁层不构成扩展障碍 |
| 部署节点卷配置 | 生产部署清单（仓库外）需为 cookie 容器挂载 `browser-sessions` 卷并注入 mongo/messaging 连接串 | PR #78 描述显式列出；缺卷时 session 无法持久化按预期重启后需重扫（不 fail-fast） |
| 历史手工 cookie 兼容 | `AccountId == null` 数据无 session 可刷新 | 宣告走旧逻辑（offline + 事件）返回 410（1.6⑥）；引导运维扫码替代；`bilibili_cookies` 结构兼容性演进无迁移脚本 |
| 依赖 | `Microsoft.Playwright` 1.62.0（NuGet，netstandard2.0 target 兼容 net10.0，已核）；Firefox headless；StackExchange.Redis（经 Aspire.StackExchange.Redis 13.4.6）；Testcontainers mongo/redis 夹具；openapi-typescript ^7.13.0 | 已核实（1.0） |

## 7. 相关文档

- PRD：`docs/product/draft/20260911-cookie-service-prd.md`（v1.0）
- 技术提案：`docs/engineering/proposals/20260911-proposal-cookie-service.md`
- 契约：`contracts/cookie.openapi.yaml`（Phase 0 新建 v1.0.0）
- 前序 Plan（失效通知链路）：[[20260905-plan-cookie-invalid-notification]]
- 新服务工程范本 Plan：[[20260911-plan-notifier-milky]]
- 历史决策参照：`docs/engineering/proposals/20260820-proposal-bilibili-412-fix.md`（不引入浏览器栈的原决策，提案 6.1 决策 1 已重新论证边界）
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`docs/engineering/frontend-rules.md`、`servers/common/AGENTS.md`、`servers/crawler/AGENTS.md`、`apps/web/AGENTS.md`
