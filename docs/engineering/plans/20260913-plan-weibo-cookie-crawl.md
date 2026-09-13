---
description: 微博 Cookie 抓取（weibo-cookie-crawl）实现 Plan——基于 PRD v1.0 与技术提案（需求总分支 prd/weibo-cookie-crawl @04ed9cf）的实施阶段划分与任务分解；契约先行（cookie 契约新增 /api/cookie/weibo/* 平行端点、notifier 契约 eventType 按平台拆分），cookie 服务经 ICookiePlatformProfile 多平台化（Bilibili 行为迁入、新增微博 Profile：passport.weibo.com 扫码 + m.weibo.cn/api/config 登录态检测 + SUB/SUBP 提取，Mongo 并行集合 weibo_cookies/weibo_cookie_sessions），crawler 新增 WeiboLoginCookieProvider 主凭证 + 匿名兜底与风控自愈重试一次，notifier dispatch 按 platform 映射 eventType 并启动迁移存量订阅，web「Cookie 管理」双按钮 + 通知类型下拉
type: Permanent
---

# 微博 Cookie 抓取（weibo-cookie-crawl）- 实现 Plan

**功能名称**: 微博 Cookie 抓取
**关联 PRD**: `docs/product/draft/20260913-weibo-cookie-crawl-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20260913-proposal-weibo-cookie-crawl.md`
**Plan 版本**: v1.0
**创建日期**: 2026-09-13
**作者**: Workflow Agent（代起草）
**需求总分支**: `prd/weibo-cookie-crawl`（已推送 origin；需求总 PR：<https://github.com/Deliay/z-vtuber/pull/88>）

## 1. 概述

本 Plan 基于 PRD v1.0 与技术提案编写，指导「微博 Cookie 抓取」的编码实施。改动分六块：① 契约先行——`contracts/cookie.openapi.yaml` 新增 `/api/cookie/weibo/*` 平行端点（bilibili 端点不动）+ 列表项补 `platform` 字段，`contracts/notifier.openapi.yaml` eventType enum 扩展为 `bilibili_cookie_invalid`/`weibo_cookie_invalid`；② common——`CookieInvalidEvent` 增加 `platform` 字段、`MongoDbInitializer` 增补微博集合索引与 eventType 拆分迁移方法；③ cookie 服务多平台化——`ICookiePlatformProfile` 抽象、Bilibili 行为迁入 `BilibiliCookiePlatformProfile`（语义不变）、新增 `WeiboCookiePlatformProfile`，仓储/调度按集合名参数化并 keyed 注册，新增 `/api/cookie/weibo/*` 控制器；④ crawler——`CookieServiceClient` 增加微博方法组、新增 `WeiboLoginCookieProvider`（主）+ `WeiboVisitorCookieProvider`（兜底）、`WeiboTimelineJob` 风控分支接入 report-invalid 自愈重试一次；⑤ notifier——dispatch 按 `platform` 映射 eventType、文案带平台、eventType 校验放开、启动迁移存量订阅；⑥ web——`CookiePage` 双按钮 + 平台标识列、`NotificationPage` 通知类型下拉。实施全程遵循 `docs/engineering/common-rules.md`（TDD、契约式开发、核心逻辑覆盖率 ≥ 80%）、`backend-rules.md`、`frontend-rules.md` 及各子项目 AGENTS.md。

### 1.0 上游核实结论（代码现状复查）

对需求总分支 `prd/weibo-cookie-crawl`（HEAD `04ed9cf`）代码核实，提案技术路径**整体可行**，以下现状细节与提案的偏差/提案未覆盖点已在对应任务中给出处理：

- **cookie 服务现状与提案 1.1 描述一致**：`BrowserSessionManager`（`servers/cookie/Browser/BrowserSessionManager.cs`）硬编码 B 站——登录 URL :38、二维码选择器 `[class*='login-scan__qrcode']` :40/:45（fallback 整页截图 :54）、nav 检测 :61-77（登录等待）与 :162-184（刷新重挂）、cookie 提取域与保活着陆页 `https://www.bilibili.com` :126/:190；接口 `IBrowserSessionManager.cs:3-10` 无平台参数。`LoginSessionWorker.cs` 5s 轮询 pending session（:57）、锁 `lock:cookie-login:{session.Id}`（:72）、登录成功硬编码解析 nav JSON `data.mid`/`data.uname`（:117-137）。`SessionRefreshJob.cs` 60s 轮询、锁 `lock:cookie-refresh:{session.Id}`（:74）、连续失败阈值默认 3（`CookieServiceOptions.cs:11`）、失效走 `InvalidateSessionAsync`（:133-157，reason `refresh_nav_missing`）。`InvalidationService.cs` 阻塞宣告：抢锁失败即 504、dedup 经 `LastRefreshAt` 比对（:93-112）、即时路径单次失败即判失效（reason `session_invalid`），与提案一致。
- **提案未覆盖——自愈成功也发事件**：`InvalidationService.cs:137` 在 report-invalid 自愈成功时同样发布 `cookie-invalid`（reason `refreshed`），notifier dispatch 不按 reason 过滤，即每次自愈都会投递通知。**处理**：微博链路沿用同一发布语义（与 bilibili 对称、notifier 零改动），本期不按 reason 过滤；若告警噪音大后续迭代再加 reason 过滤（见第 6 节风险表）。
- **仓储与 DI 需参数化**：cookie 服务使用 common 实体（`CookieSessionRecord`/`CookieRecord`），无服务私有实体；集合名硬编码——`CookieSessionRepository.cs:13` 字面量 `"bilibili_cookie_sessions"`、`CookiePoolService` 走 `MongoCollectionNames.Cookies`（`MongoDbInitializer.cs:9-10` = `"bilibili_cookies"`）；`CookieDispatchService.cs:21-25` 直接注入 `IMongoDatabase` 取固定集合；`Program.cs:30-59` 全部单例注册。**处理**：仓储/调度/锁 key 按集合名（平台）参数化，DI 改 keyed/named 注册（net10.0 keyed services），bilibili 实例行为与锁 key 保持现状不变。
- **事件发布反查路径**：`RedisCookieInvalidEventPublisher.cs:32-51` 按 cookieId 反查 `bilibili_cookie_account_info` 回填 mid/uname，事件无 `platform` 字段。**处理**：publisher 增加 `platform` 填充；微博侧不新建 account-info 集合，直接由 session 记录（Mid/Uname）回填事件帐号信息。
- **crawler 微博链路现状与提案一致，两处偏差**：① `WeiboApiClient` 接口与实现同文件于 `servers/crawler/Weibo/IWeiboApiClient.cs:12-85`（提案项目结构写 `WeiboApiClient.cs`，Plan 按现状引用）；② `IWeiboApiClient.GetUserTimelineAsync` 无 cookieId 回传通道，与「对外接口不变 + 风控后按 cookieId 上报」存在张力。**处理**：`WeiboRiskControlException` 增加 `CookieId?`/`IsLoginCookie` 属性，由 `WeiboApiClient` 抛出时从当前 cookie 提供方填充——异常作为带外通道，`IWeiboApiClient` 方法签名不变（任务 2.4）。
- **风控分类器缺口确认**：`WeiboRiskControlClassifier.cs` 3xx 仅认 `passport.weibo.com`（:25-30，未含 `login.sina.com.cn`）；非 JSON body（HTML）直接放行（:38-41）。按提案补充两类特征，保持「仅 JSON msg 字段判词防误伤」约束（任务 2.5）。
- **`WeiboTimelineJob` 风控分支现状**：`:85-91` 仅 `_backoff.ReportRiskControl("weibo:{uid}")` + `RenewCookieSafelyAsync`（:154-164）后 return，无 report-invalid/重试；Job 直接依赖 `IWeiboVisitorCookieProvider`（:15/:25）。测试锚点 `WeiboTimelineJobTests.cs:145`（`RunOnce_RiskControl_ReportsBackoff_AndRenewsCookie`）。
- **`CookieServiceClient` 现状**：`CheckoutAsync`（:26，404→`NoAvailableCookieException`）/`ReportInvalidAsync`（:41，200/410/404/504 四态）/`CooldownAsync`（:94），全部打 `/api/cookie/*` 无平台维度；`CookieCheckoutResult`（:102-106）无 `AccountId` 字段（服务端响应有）。**处理**：新增微博方法组 `CheckoutWeiboAsync`/`ReportWeiboInvalidAsync`/`CooldownWeiboAsync`，checkout 404 不抛异常而返回「无可用」信号（区别于 bilibili 语义，因微博有匿名兜底）。
- **`CookieCallGate` 可直接复用**：已是 `IDistributedLockProvider` 包装（`Services/CookieCallGate.cs:9-33`），key `cookie-call:{cookieId}`（无 `lock:` 前缀，与 cookie 服务锁风格不同，保持各自现状），微博 per-cookie 互斥直接注入复用。
- **`WeiboCrawlerOptions` 现状**（`Configuration/WeiboCrawlerOptions.cs:7-17`）：`TimelineIntervalSeconds/HttpTimeoutSeconds/CookieRefreshIntervalMinutes/MaxPagesPerCrawl/UserAgent/BackoffBaseSeconds/BackoffMaxMinutes/CooldownMinutes/RiskControlCooldownThreshold`，无 `UseLoginCookie`/`LoginCookieRetryOnce`，确认需新增（默认均 `true`）。
- **common 现状**：`CookieInvalidEvent.cs:5-21` 字段 cookie_id/mid/uname/occurred_at/reason，无 `platform`；`CookieEventChannels.cs:5` 单频道 `cookie-invalid`；`CookieSessionRecord` 身份字段为 `Mid/Uname`（微博 uid 为数值型，直接复用 Mid 承载 uid、Uname 承载昵称，集合级隔离已足够，不新增字段）；`MongoDbInitializer` cookie 索引仅覆盖 bilibili 集合（:68-94）；迁移先例 `MigrateSubscriptionTypeSplitAsync`（:651-731，marker + 幂等 BulkWrite + `MarkMigrationAppliedAsync`）。
- **notifier 现状**：订阅泵 `CookieInvalidEventSubscriber.cs:30-47` 原样转发，零改动（与提案一致）；`NotificationDispatchService.cs:30` 硬编码 `GetByEventTypeAsync("cookie_invalid")`、`FormatMessage`（:54-66）无平台维度；`NotifyConfigController.cs:69-72` eventType 硬编码校验。**偏差**：notifier 无 `Persistence/` 目录且启动不跑迁移（`Program.cs:52-56` 仅 `EnsureIndexesAsync`），迁移先例在 common `MongoDbInitializer`。**处理**：迁移方法 `MigrateCookieInvalidEventTypeSplitAsync` 落 common（遵循先例），notifier `Program.cs` 启动调用（幂等，crawler/api 无需调用）。
- **契约现状**：`contracts/cookie.openapi.yaml` 8 端点无 weibo 前缀，`AccountItem`（:154-177）无 `platform`；`contracts/notifier.openapi.yaml` eventType enum `[cookie_invalid]` 两处（:115/:141）。另发现控制器 `AccountItem` 含 `CookieId` 而契约未暴露——维持现状不顺带对齐（最小改动）。
- **web 现状**：`client.ts` cookie 函数 7 个（:179-217）均指向 `/cookie/*`，无微博组、无 cooldown；`CookiePage.tsx`（400 行）单按钮（:179-188）、弹窗文案硬编码「请使用 B 站客户端扫描二维码」（:354）、列表无平台列、轮询逻辑 `:70-125` 可泛化；`NotificationPage.tsx` 的通知类型是**禁用文本框**（:222-227）而非下拉，`eventType: 'cookie_invalid'` 硬编码（:80）——「类型下拉」为全新 select 组件；`package.json:12` codegen 已含 cookie 契约，改契约后跑 `npm run codegen` 即可。
- **测试设施现状**：`Fakes.cs` 可用 `FakeBrowserSessionManager`(:76)、`FakeDistributedLockProvider`(:121)、`FakeCookieServiceClient`(:266-292)、`FakeWeiboVisitorCookieProvider`(:294)、`FakeWeiboApiClient`(:310)；散落的 `FakeCookieSessionRepository`（CookieAccountControllerTests.cs:154）、`FakeCookieInvalidEventPublisher`（SessionRefreshJobTests.cs:192）等复用时注意不在 Fakes.cs；无独立 `CookieServiceClientTests.cs`（本期新建）；迁移测试先例 `SubscriptionTypeSplitMigrationTests.cs`；Testcontainers 夹具 `MongoDbFixture`/`RedisFixture` 沿用。
- **工程接入零改动确认**：slnx 已含 cookie 项目（`Z.Vtuber.slnx:8`）；AppHost 已编排 cookie（`AppHost.cs:18-25`）且 crawler 已注入 `CookieService:BaseUrl`（:34）、web 已注入 `VITE_COOKIE_URL`（:52）；vite（`vite.config.ts:16-19`）与 nginx（`nginx.conf.template:32-39`）已有 `/api/cookie` 代理，新端点 `/api/cookie/weibo/*` 落在既有前缀内，无需改动。

### 1.1 实施范围

- **做**：
  - 契约：cookie 契约新增 `/api/cookie/weibo/*` 8 端点 + `AccountItem` 补 `platform`；notifier 契约 eventType enum 扩展；web codegen 重新生成。
  - common：`CookieInvalidEvent` + `platform`（可空，空按 bilibili）；`MongoDbInitializer` 增补 `weibo_cookies`/`weibo_cookie_sessions` 索引 + 新增 `MigrateCookieInvalidEventTypeSplitAsync`（marker `cookie_invalid_platform_split_v1`）。
  - cookie 服务：`Platform/` Profile 抽象与双实现；`BrowserSessionManager`/`LoginSessionWorker`/`SessionRefreshJob`/`InvalidationService` Profile 化；仓储与 `CookieDispatchService` 集合名参数化；DI keyed 注册；新增 `WeiboCookieAccountController`/`WeiboCookieDispatchController`；publisher 填充 `platform`；weibo 锁 key 带平台段（bilibili 锁 key 不变）。
  - crawler：`CookieServiceClient` 微博方法组；`WeiboLoginCookieProvider`（checkout + `CookieCallGate` per-cookie 互斥 + 404 兜底信号）；`WeiboApiClient` cookie 来源抽象（主/兜底组合）；`WeiboRiskControlException` 携带 cookieId；`WeiboTimelineJob` 风控分支（report-invalid 自愈重试一次 / 匿名维持现状）；`WeiboRiskControlClassifier` 补充特征；`WeiboCrawlerOptions` + `UseLoginCookie`/`LoginCookieRetryOnce`。
  - notifier：dispatch `platform → eventType` 映射 + 文案带平台；`NotifyConfigController` 校验放开；`Program.cs` 启动迁移调用。
  - web：`client.ts` 微博 cookie 客户端函数；`CookiePage` 双按钮 + 平台标识列 + 弹窗/轮询泛化；`NotificationPage` 通知类型 select + 中文名映射。
  - 测试：全部新增/改写测试，核心逻辑覆盖率 ≥ 80%，bilibili 全链路零回归。
- **不做**：bilibili 链路任何行为变更（仅共享代码被抽象，语义不变）；微博 App 二次验证（短信/滑块）自动处理；代理池/UA 轮换等进阶风控；`cooldown` 的频率型风控细分启用（v1 全部走 report-invalid，提案决策 8）；api 服务改动；新服务/新容器/新代理配置；按 reason 过滤通知（见 1.0）。

### 1.2 现状基线

- **Profile 抽象范本**：cookie 服务既有模块边界清晰（Browser/Refresh/Pool/Controllers），平台差异点已在 1.0 逐项定位（登录 URL、二维码选择器、登录态检测、cookie 域、身份解析），抽象为 `ICookiePlatformProfile` 后 Bilibili 行为原样迁入。
- **并行集合复用**：`weibo_cookies`/`weibo_cookie_sessions` 与 bilibili 集合结构相同（复用 common 实体），仓储逻辑经集合名参数化复用，bilibili 读写路径零改动（提案决策 3）。
- **round-robin / 宣告 / 锁语义平移**：`CookieDispatchService` 的 `FindOneAndUpdate`、`InvalidationService` 的阻塞宣告与 dedup、`SessionRefreshJob` 的失败计数状态机全部平台无关，按 Profile + 平台实例复用。
- **前端先例**：`CookiePage` 既有弹窗/轮询/过期重试形态泛化复用；`NotificationPage` 参照既有弹窗模式新增 select。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash）与 Conventional Commits：

- 本需求开发全程只使用需求总分支 `prd/weibo-cookie-crawl`：契约、实现、测试、文档全部直接在该分支提交，不切开发子分支（沿用近两期需求既定做法；提案头部的 `feat/weibo-cookie-crawl` 以本 Plan 与黑板为准，统一使用 `prd/weibo-cookie-crawl`）。
- 需求总分支已推送 origin，需求总 PR 已存在（#88），提交照常推送更新分支。
- 提交信息使用 Conventional Commits：契约 `feat:`、测试先行 `test:`、实现 `feat:`、文档 `docs:`；测试与实现分开提交。
- 并行 subagent 按子项目划分（cookie 服务 / crawler / notifier / apps-web），共享文件（`Fakes.cs`、common 实体、契约）归入主串行线先行，避免并行冲突（见第 4 节）。

## 3. 实施阶段

按「契约先行 → TDD 红-绿 → 收口验证」推进。Phase 0 串行先行；Phase 1 测试先行（设施 1.1 先行，其余按子项目并行）；Phase 2 common（2.1）串行先行，随后 cookie 服务 / crawler / notifier / web 四线并行；Phase 3 串行收口。本期无新增工程接入（slnx/AppHost/Dockerfile/CI/代理均已就绪，见 1.0），故不设独立工程 Phase。

### Phase 0: 契约先行

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | `contracts/cookie.openapi.yaml` 新增微博平行端点组（语义与 bilibili 对应端点逐一一致）：① `POST /api/cookie/weibo/accounts`（202：`accountId/status/qrCodeImage/expiresAt`；429）；② `GET /api/cookie/weibo/accounts/{accountId}`（200：`status ∈ pending_login/active/expired/invalid`；404）；③ `GET /api/cookie/weibo/accounts`（列表不含 cookie 原文）；④ `DELETE /api/cookie/weibo/accounts/{accountId}`（204）；⑤ `GET /api/cookie/weibo/available`；⑥ `POST /api/cookie/weibo/checkout`（200：`cookieId/accountId/cookie`；404 `no_available_cookie`）；⑦ `POST /api/cookie/weibo/report-invalid`（200 `refreshed: true` / 410 `session_invalid` / 404 / 504）；⑧ `POST /api/cookie/weibo/cooldown`（`cookieId` + `durationSeconds`，204/404）。同时给 `AccountItem` 增加 `platform` 字段（`bilibili`/`weibo`，bilibili 列表响应同样携带）；既有 `/api/cookie/*` 端点定义不动 | `contracts/cookie.openapi.yaml` | `npx openapi-typescript` 可成功生成；8 端点齐备且与 bilibili 组字段级对称 |
| 0.2 | `contracts/notifier.openapi.yaml` 两处 eventType enum（:115 响应 / :141 请求）由 `[cookie_invalid]` 改为 `[bilibili_cookie_invalid, weibo_cookie_invalid]`（`cookie_invalid` 从契约移除，存量数据由迁移兜底） | `contracts/notifier.openapi.yaml` | codegen 通过 |
| 0.3 | `npm run codegen` 重新生成 `schema-cookie.d.ts` / `schema-notify.d.ts`（勿手改） | `apps/web/src/api/schema-cookie.d.ts`、`apps/web/src/api/schema-notify.d.ts` | 生成物与契约一致；`npm run build` 通过 |

### Phase 1: 测试先行（红）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | 测试设施（串行先行）：`Fakes.cs` 新增 `FakeCookiePlatformProfile`（可注入登录态判定序列/身份提取结果/二维码选择器）、`FakeWeiboLoginCookieProvider` 所需契约 fake；散落的 `FakeCookieSessionRepository`/`FakeCookieDispatchService`/`FakeCookieInvalidEventPublisher` 按集合名参数化适配（或提升入 Fakes.cs，实现期定夺）；`FakeCookieServiceClient` 增补微博方法组 | `tests/Z.Vtuber.Tests/Fakes.cs` 及散落 fake 所在文件 | 编译通过（引用点随后续任务切换） |
| 1.2 | `WeiboCookiePlatformProfileTests`（新，mock Playwright 接口层）：① `api/config` 响应 `data.login==true/false` 判定；② cookie 提取拼接（核心 SUB/SUBP，其余字段保留）；③ 身份回填（uid/昵称取自 `api/config` 响应）；④ 二维码选择器 fallback 整页截图 | `tests/Z.Vtuber.Tests/WeiboCookiePlatformProfileTests.cs`（新） | 红 |
| 1.3 | cookie 服务微博端点测试（沿用既有 bilibili 测试形态，fake 浏览器层 + Testcontainers Mongo）：① `POST /api/cookie/weibo/accounts` 202 + 二维码 + `expiresAt`；并发 pending 上限 429；② 登录成功（Profile 判定 true）→ session active + 回填 uid/昵称 + cookie upsert 入 `weibo_cookies`；③ 超时未扫码 → expired + 上下文释放；④ `GET /available`、`GET /accounts` 不含 cookie 原文且列表项带 `platform=weibo`；⑤ checkout round-robin + 冷却过滤 + 无可用 404；⑥ report-invalid：200 自愈返回新 cookie / 410 会话失效 + 事件 `platform=weibo` / 404 / 504；⑦ `DELETE` → cookie offline + 事件 reason `account_removed`；⑧ 微博操作不触碰 bilibili 集合（双向隔离断言） | `tests/Z.Vtuber.Tests/WeiboCookieAccountControllerTests.cs`（新）、`WeiboCookieDispatchTests.cs`（新）、`WeiboCookieReportInvalidTests.cs`（新） | 红 |
| 1.4 | bilibili 回归锚定：既有 `CookieAccountControllerTests`/`CookieDispatchTests`/`CookieReportInvalidTests`/`SessionRefreshJobTests` 全部保持绿（Profile 迁入后语义不变）；`MongoDbInitializerTests` 增补——`weibo_cookies`/`weibo_cookie_sessions` 索引创建、重复执行幂等 | 既有测试文件、`MongoDbInitializerTests.cs` | 既有用例不改动即绿（红仅来自新增断言） |
| 1.5 | `CookieServiceClientTests`（新，stub `HttpMessageHandler`）：① `CheckoutWeiboAsync` 200 映射 cookieId/cookie；404 返回「无可用」信号（不抛 `NoAvailableCookieException`）；② `ReportWeiboInvalidAsync` 200/410/404/504 四态；③ `CooldownWeiboAsync` 请求路径与体；④ 既有 bilibili 三方法行为不变 | `tests/Z.Vtuber.Tests/CookieServiceClientTests.cs`（新） | 红 |
| 1.6 | `WeiboLoginCookieProviderTests`（新）：① checkout 成功返回 cookie 头 + cookieId；② 404 → 返回「无可用」信号供上层兜底（不抛异常）；③ 同 cookieId 经 `CookieCallGate` 互斥、不同 cookieId 并行（fake 锁验证 acquire/release） | `tests/Z.Vtuber.Tests/WeiboLoginCookieProviderTests.cs`（新） | 红 |
| 1.7 | `WeiboTimelineJobTests` 风控分支改写 + `WeiboApiClientTests` 适配：① 登录 cookie 命中风控 → `ReportWeiboInvalidAsync` 阻塞宣告，200 拿新 cookie **重试一次**成功；② 410/504/404 → 放弃本轮 + `ReportRiskControl("weibo:{uid}")` 退避；③ 重试一次后仍风控 → 不再宣告，直接退避；④ 匿名凭证命中 → 维持现状（`RenewAsync` + 退避，既有用例语义保留）；⑤ 无可用登录 cookie → 回退匿名并记 warning 日志；⑥ `UseLoginCookie=false` 纯匿名（不调用 checkout）；⑦ `WeiboRiskControlException` 携带 `CookieId`/`IsLoginCookie` | `tests/Z.Vtuber.Tests/WeiboTimelineJobTests.cs`、`WeiboApiClientTests.cs` | 红 |
| 1.8 | `WeiboRiskControlClassifier` 补充特征测试：① 3xx location 含 `login.sina.com.cn` 判风控；② 预期 JSON 但返回 HTML（非 JSON body 且状态码可疑路径）判风控；③ 既有判词用例不回归（仅 JSON msg 字段判词、不误伤正常数据） | `tests/Z.Vtuber.Tests/WeiboParserClassifierOptionsTests.cs`（或拆分新文件） | 红 |
| 1.9 | notifier 测试：① `NotificationDispatchServiceTests` 改写——`platform=weibo` → 仅匹配 `weibo_cookie_invalid` 订阅、`platform=bilibili`/空 → 仅匹配 `bilibili_cookie_invalid`；文案含平台名（「bilibili 帐号失效通知」/「微博帐号失效通知」）与帐号标识；② `NotifyConfigControllerTests`——两个新 eventType 通过校验、非法值 400、`cookie_invalid` 不再接受；③ `CookieInvalidEventTypeSplitMigrationTests`（新，参照 `SubscriptionTypeSplitMigrationTests`）——存量 `cookie_invalid` → `bilibili_cookie_invalid`、幂等可重入、其他类型不受影响 | `tests/Z.Vtuber.Tests/NotificationDispatchServiceTests.cs`、`NotifyConfigControllerTests.cs`、`CookieInvalidEventTypeSplitMigrationTests.cs`（新） | 红 |
| 1.10 | 前端测试：① `client.test.ts`——微博 cookie 客户端函数路径/方法（`/cookie/weibo/accounts*` 等）；② `CookiePage.test.tsx`——双按钮渲染、各自调对应平台创建接口、弹窗按平台展示对应文案（微博/B站）、轮询收敛复用、列表平台标识列、服务不可用明确错误态；③ `NotificationPage.test.tsx`——类型下拉含两个新类型、提交映射正确 eventType、绑定列表展示中文名 | `apps/web/src/api/client.test.ts`、`apps/web/src/pages/cookie/CookiePage.test.tsx`、`apps/web/src/pages/notification/NotificationPage.test.tsx` | `npm run test` 红 |

### Phase 2: 实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | common 演进（串行先行）：① `Live/CookieInvalidEvent.cs` 增加 `platform`（`[JsonPropertyName("platform")]`，可空，消费侧空按 bilibili）；② `MongoDbInitializer` 新增 `EnsureWeiboCookieIndexesAsync`（`weibo_cookies`：`(Status, LastUsedAt)` + `AccountId_1`；`weibo_cookie_sessions`：`Status_1` + `CookieId_1`，集合名常量入 `MongoCollectionNames`）并挂 `EnsureIndexesAsync`；③ 新增 `MigrateCookieInvalidEventTypeSplitAsync`（先例 `MigrateSubscriptionTypeSplitAsync`：`zvtuber_meta` marker `cookie_invalid_platform_split_v1` + `event_type == "cookie_invalid"` → `bilibili_cookie_invalid` 幂等 updateMany + `MarkMigrationAppliedAsync`） | `servers/common/Live/CookieInvalidEvent.cs`、`servers/common/Persistence/MongoDbInitializer.cs` | 1.4 中 MongoDbInitializer 新增断言转绿；既有测试无回归 |
| 2.2 | cookie 服务平台抽象：① 新增 `Platform/ICookiePlatformProfile.cs`（`Platform`/`LoginUrl`/`QrCodeSelector`/`HomeUrl`/`CookieDomainUrl`/`IsLoggedInAsync`/`ExtractIdentityAsync`，签名按提案实现点 1）；② `Platform/BilibiliCookiePlatformProfile.cs`——现有行为原样迁入（登录页/二维码选择器/nav `isLogin:true` 检测/nav JSON `data.mid`+`data.uname` 身份解析/`www.bilibili.com` 域）；③ `Platform/WeiboCookiePlatformProfile.cs`——登录页 `https://passport.weibo.com/`、二维码元素截图（选择器实现期按实际页面校准，fallback 整页截图二维码区域）、登录态检测 `m.weibo.cn/api/config` 的 `data.login == true`、cookie 提取域 `https://m.weibo.cn`（SUB/SUBP 核心）、身份回填取 `api/config` 响应 uid；④ `BrowserSessionManager`/`LoginSessionWorker` 改 Profile 驱动（nav 硬编码判定与身份解析全部下沉 Profile），bilibili 路径语义零变化 | `servers/cookie/Platform/`（新）、`servers/cookie/Browser/` | 1.2、1.3①-③ 转绿；1.4 保持绿 |
| 2.3 | cookie 服务多平台装配：① `CookieSessionRepository`/`CookiePoolService` 使用点/`CookieDispatchService` 按集合名参数化；② `SessionRefreshJob`/`InvalidationService` 按平台实例化，weibo 锁 key 带平台段（`lock:cookie-refresh:weibo:{session.Id}`、`lock:cookie-login:weibo:{session.Id}`），bilibili 锁 key 保持现状；③ `RedisCookieInvalidEventPublisher` 按 Profile 填充 `platform`，微博事件帐号信息由 session 记录回填（不新建 weibo account-info 集合）；④ `Program.cs` 改 keyed/named 注册双平台实例（浏览器 `IBrowserType` 共享），`SessionRefreshJob`/`LoginSessionWorker` 覆盖双平台 | `servers/cookie/Persistence/`、`servers/cookie/Pool/CookieDispatchService.cs`、`servers/cookie/Refresh/`、`servers/cookie/Program.cs` | 1.3⑤-⑧ 转绿；双平台隔离断言通过 |
| 2.4 | cookie 服务微博控制器：新增 `Controllers/WeiboCookieAccountController.cs`（`/api/cookie/weibo/accounts*` 四端点，429 上限复用 `MaxPendingLoginSessions`）+ `Controllers/WeiboCookieDispatchController.cs`（`/api/cookie/weibo/{available,checkout,report-invalid,cooldown}`，状态码映射与 bilibili 一致）；`CookieAccountController` 的 `AccountItem` 补 `platform="bilibili"`，微博列表项 `platform="weibo"`；共享同一套服务实现（按 keyed 实例注入） | `servers/cookie/Controllers/WeiboCookieAccountController.cs`（新）、`WeiboCookieDispatchController.cs`（新）、`CookieAccountController.cs` | 1.3 全绿 |
| 2.5 | crawler 微博 cookie 链路：① `Services/CookieServiceClient.cs` 新增 `CheckoutWeiboAsync`（404 返回无可用信号，不抛异常）/`ReportWeiboInvalidAsync`/`CooldownWeiboAsync`；② 新增 `Weibo/WeiboLoginCookieProvider.cs`（checkout 取用 + 按 cookieId 复用 `CookieCallGate` 互斥 + 无可用信号透出）；③ `Weibo/IWeiboApiClient.cs` 中 `WeiboApiClient` cookie 来源抽象为「主（登录）+ 兜底（访客）」组合——`UseLoginCookie=true` 且 checkout 有货走登录 cookie，否则回退 `WeiboVisitorCookieProvider` 并记 warning；`IWeiboApiClient` 方法签名不变；④ `WeiboRiskControlException` 增加 `CookieId?`/`IsLoginCookie`，抛出时由当前 cookie 来源填充 | `servers/crawler/Services/CookieServiceClient.cs`、`servers/crawler/Weibo/WeiboLoginCookieProvider.cs`（新）、`servers/crawler/Weibo/IWeiboApiClient.cs` | 1.5、1.6、1.7⑦ 转绿 |
| 2.6 | crawler 风控分支与配置：① `Jobs/WeiboTimelineJob.cs` 风控 catch 分支：`IsLoginCookie` → `ReportWeiboInvalidAsync`（最多一次）——200 用新 cookie 重试一次 / 410/504/404 放弃本轮 + `ReportRiskControl("weibo:{uid}")`；匿名 → 维持 `RenewCookieSafelyAsync` + 退避；重试仍风控不再宣告直接退避；② `Configuration/WeiboCrawlerOptions.cs` + `UseLoginCookie`（默认 true）/`LoginCookieRetryOnce`（默认 true）；③ `WeiboRiskControlClassifier` 补充 `login.sina.com.cn` 3xx 特征与非 JSON body 特征；④ `Program.cs` 注册 `WeiboLoginCookieProvider` 与组合 cookie 来源；抓取间隔/翻页/退避参数全部不变 | `servers/crawler/Jobs/WeiboTimelineJob.cs`、`servers/crawler/Configuration/WeiboCrawlerOptions.cs`、`servers/crawler/Weibo/WeiboRiskControlClassifier.cs`、`servers/crawler/Program.cs` | 1.7、1.8 转绿 |
| 2.7 | notifier 平台化：① `NotificationDispatchService.DispatchAsync` 按事件 `platform` 映射 eventType（`bilibili`/空 → `bilibili_cookie_invalid`，`weibo` → `weibo_cookie_invalid`），`FormatMessage` 文案带平台名与帐号标识（「【Z.Vtuber】bilibili 帐号失效通知 …」/「【Z.Vtuber】微博帐号失效通知 …」，微博事件经 session 回填的 uid/昵称展示）；② `NotifyConfigController` eventType 校验放开为两个新值；③ `Program.cs` 启动调用 `MigrateCookieInvalidEventTypeSplitAsync`（`EnsureIndexesAsync` 之后）；订阅泵零改动 | `servers/notifier/Dispatch/NotificationDispatchService.cs`、`servers/notifier/Controllers/NotifyConfigController.cs`、`servers/notifier/Program.cs` | 1.9 转绿 |
| 2.8 | web 改造（仅依赖 Phase 0 契约，可与后端并行）：① `client.ts` 新增微博 cookie 客户端函数（`createWeiboCookieAccount`/`getWeiboCookieAccount`/`listWeiboCookieAccounts`/`deleteWeiboCookieAccount`/`listAvailableWeiboCookies`，指向 `/cookie/weibo/*`）；② `CookiePage.tsx`——「新增帐号」拆为「新增微博帐号」「新增Bilibili帐号」两按钮，创建/轮询/删除逻辑泛化带平台参数，弹窗文案按平台区分（微博 App / B 站客户端），列表加「平台」列（依赖 `platform` 字段）；③ `NotificationPage.tsx`——新增绑定弹窗的通知类型改 select（「bilibili帐号失效通知」→ `bilibili_cookie_invalid`、「微博帐号失效通知」→ `weibo_cookie_invalid`），绑定列表类型中文名映射，存量 `cookie_invalid` 数据显示按 bilibili 处理（迁移完成后不出现） | `apps/web/src/api/client.ts`、`apps/web/src/pages/cookie/CookiePage.tsx`、`apps/web/src/pages/notification/NotificationPage.tsx` | 1.10 转绿；`npm run lint`/`npm run build` 通过 |

### Phase 3: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 3.1 | `dotnet test tests/Z.Vtuber.Tests` 全绿（含 bilibili 全量回归）；cookie 服务平台化与 crawler 风控分支核心逻辑覆盖率 ≥ 80%；`npm run test`/`lint`/`build` 全绿；grep 确认：cookie 服务无 bilibili 字面量残留于 Profile 之外（登录 URL/选择器/nav 判定全部归 `BilibiliCookiePlatformProfile`）、`/api/cookie/weibo` 端点与契约一致 | 全部通过 |
| 3.2 | 编排手工验证（z-aspire-orchestrator）：本地 Aspire 拉起全量服务 → web「Cookie 管理」点「新增微博帐号」弹微博二维码 → 手机扫码秒级判登录成功、cookie 入 `weibo_cookies` → 微博时间线抓取携带登录 cookie → 宣告失效：session 有效拿新 cookie 重试成功 / session 失效 410 + 订阅「微博帐号失效通知」的群收到含平台与帐号的通知 → 服务重启微博 session 恢复无需重扫 → 无可用微博 cookie 时匿名兜底且日志可观测 → bilibili 全链路（扫码/保鲜/checkout/宣告/通知）零回归 | 手工验证记录 |
| 3.3 | AGENTS.md 同步：`servers/cookie/AGENTS.md`（Platform 抽象、双平台端点表、keyed DI）；`servers/crawler/AGENTS.md`（`WeiboLoginCookieProvider`、cookie 来源组合、风控分支语义、新配置项）；`servers/common/AGENTS.md`（`CookieInvalidEvent.platform`、微博集合索引、迁移方法）；`servers/notifier/AGENTS.md`（eventType 双平台、启动迁移）；`apps/web/AGENTS.md`（双按钮、平台列、通知类型下拉）；`docs/engineering/backend-rules.md` 项目结构图如涉及则同步 | 文档与实现一致 |

## 4. 并行策略

- Phase 0（契约）串行先行——前端 codegen、cookie 微博控制器、crawler client 均依赖契约。
- Phase 1：1.1（测试设施）串行先行；随后 cookie 服务测试（1.2-1.4）、crawler 测试（1.5-1.8）、notifier 测试（1.9）、前端测试（1.10）互不依赖，可拆 subagent 并行。
- Phase 2：2.1（common）串行先行——四线均依赖 common 实体/迁移方法；2.1 完成后 cookie 服务线（2.2 → 2.3 → 2.4，线内大体串行）、crawler 线（2.5 → 2.6）、notifier 线（2.7）、web 线（2.8）分属不同子项目，按 z-coding 规范拆 subagent 并行，各 subagent 负责「实现 → 提交 → 测试修复 → 提交」闭环。
- Phase 3 串行收口。全部提交在需求总分支 `prd/weibo-cookie-crawl`。

## 5. 验收对照

| PRD 功能点 | 实现任务 | 验证方式 |
|-----------|---------|---------|
| 功能点 1：新增微博帐号（扫码登录） | 0.1、2.2、2.4 | 1.2、1.3①-③ + 扫码全链路手工验证（3.2） |
| 功能点 2：微博 cookie 定时保鲜与失效自愈 | 2.2、2.3 | 1.3⑥、1.5② + 手工验证（3.2） |
| 功能点 3：微博时间线登录态抓取与风险控制 | 0.1、2.5、2.6 | 1.5-1.8 + 手工验证（3.2） |
| 功能点 4：分平台帐号失效通知 | 0.2、2.1③、2.7 | 1.9 + 通知投递手工验证（3.2） |
| 功能点 5：后台「Cookie 管理」页按平台改造 | 0.1、0.3、2.8 | 1.10 + 界面手工验证（3.2） |
| 兼容性：bilibili 零回归；存量订阅迁移不丢 | 2.2-2.4（语义迁入）、2.1③、2.7③ | 1.4、1.9③ + bilibili 全链路回归（3.2） |
| 约束：平台隔离（池/锁/状态）；宣告阻塞式；不提高请求频率 | 2.3②、2.5③、2.6 | 1.3⑧、1.6③、1.7 |
| 非功能：核心逻辑覆盖率 ≥ 80%、全量测试绿 | 3.1 | `dotnet test` / `npm run test` |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| 自愈成功也投递通知（现状语义） | `InvalidationService` 在 report-invalid 自愈成功时发布 reason `refreshed` 事件，notifier 不按 reason 过滤；微博风控命中频率可能高于 bilibili，告警噪音风险 | 本期沿用对称语义（bilibili 零回归、notifier 零改动）；运维反馈噪音后后续迭代按 reason 白名单过滤（仅投递 `session_invalid`/`account_removed`/`refresh_nav_missing`） |
| 微博登录页/二维码选择器稳定性 | 微博网页版扫码入口 DOM 变化会破坏登录流程（登录态检测走 `api/config` 不受影响） | 选择器集中在 `WeiboCookiePlatformProfile`，失败返回明确错误并记日志；fallback 整页截图二维码区域；约束「登录态以接口信号为准」不变 |
| 微博二次验证（短信/滑块） | 无头浏览器无法自动完成，扫码确认后登录态迟迟不成立 | 按二维码超时过期处理（既有状态机覆盖），提示运维重试或更换帐号；PRD 3.5 已声明为人工事项 |
| cookieId 带外通道 | `IWeiboApiClient` 签名不变前提下，风控上报依赖异常携带 cookieId | `WeiboRiskControlException.CookieId/IsLoginCookie` 由 `WeiboApiClient` 抛出时填充（2.5④），1.7⑦ 锁定行为；匿名路径 `CookieId=null` 走既有 Renew 逻辑 |
| 微博 uid 与 `CookieSessionRecord.Mid` 类型 | Mid 为数值型（bilibili mid），微博 uid 同为数值型可直接复用；若遇非数值标识场景需泛化字段 | 实现期验证 uid 取值范围；如需泛化（改 string）需同步 bilibili 读写路径——走 review 把关，原则上不泛化 |
| 存量订阅迁移并发 | api/crawler/notifier 多服务启动均可能执行初始化 | 迁移方法遵循 `MigrateSubscriptionTypeSplitAsync` 先例（marker + 幂等 updateMany），仅 notifier Program.cs 调用；并发安全由幂等性保证 |
| 锁 key 平台段 | bilibili 锁 key 改名将释放瞬间的并发保护语义（锁为易失状态，无持久影响） | bilibili 锁 key 保持现状不变；仅 weibo 新 key 带平台段（`lock:cookie-refresh:weibo:{id}`），零回归 |
| 依赖 | 无新增依赖：Playwright/Mongo/Redis/Testcontainers/openapi-typescript 全部沿用既有版本 | 已核实（1.0） |

## 7. 相关文档

- PRD：`docs/product/draft/20260913-weibo-cookie-crawl-prd.md`（v1.0）
- 技术提案：`docs/engineering/proposals/20260913-proposal-weibo-cookie-crawl.md`
- 契约：`contracts/cookie.openapi.yaml`、`contracts/notifier.openapi.yaml`（Phase 0 更新）
- 前序 Plan：[[20260911-plan-cookie-service]]（Cookie 服务，本期做多平台扩展）
- 前序 Plan：`docs/engineering/plans/20260909-plan-weibo-crawler.md`（微博时间线抓取现状）
- 前序 Plan：[[20260905-plan-cookie-invalid-notification]]（失效事件链路）
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`docs/engineering/frontend-rules.md`、`servers/cookie/AGENTS.md`、`servers/crawler/AGENTS.md`、`servers/notifier/AGENTS.md`、`servers/common/AGENTS.md`、`apps/web/AGENTS.md`
