---
description: 微博 Cookie 抓取代理 XHR 增量（weibo-cookie-crawl）实现 Plan——基于 PRD v1.2 与技术提案 v1.2/v1.3（feat/weibo-cookie-crawl）的实施阶段划分与任务分解；契约先行（新增 POST /api/cookie/weibo/proxy 并补 AccountItem.cookieId 声明），cookie 服务 BrowserSessionManager 增加 GetOrRestoreContextAsync 上下文按需恢复与 ProxyRequestAsync 页面上下文 XHR、WeiboCookieDispatchController 挂 proxy 动作，crawler CookieServiceClient.ProxyWeiboAsync + WeiboApiClient 登录态分流改走代理、WeiboTimelineJob 代理异常分支，apphost 会话目录评估（容器内默认可写路径即可、不挂卷），TDD 全覆盖
type: Permanent
---

# 微博 Cookie 抓取·代理 XHR 增量（weibo-cookie-crawl）- 实现 Plan

**功能名称**: 微博 Cookie 抓取——登录态请求经 Cookie 服务代理 XHR（剩余增量）
**关联 PRD**: `docs/product/draft/20260913-weibo-cookie-crawl-prd.md`（v1.2）
**关联技术提案**: `docs/engineering/proposals/20260913-proposal-weibo-cookie-crawl.md`（v1.3）
**Plan 版本**: v2.1
**创建日期**: 2026-09-13（v2.0 修订：2026-09-14；v2.1 修订：2026-09-14，下游打回返工）
**作者**: Workflow Agent（代起草）
**feat-branch**: `feat/weibo-cookie-crawl`（需求总分支，已推送 origin；需求总 PR：<https://github.com/Deliay/z-vtuber/pull/96>）

> v2.1 变更（2026-09-14，下游打回返工）：**撤销 apphost 浏览器会话目录挂具名数据卷的设计**——经人类确认，apphost 无需挂数据卷，浏览器会话目录使用容器内默认可写路径即可满足需求（重启丢会话由既有失效状态机兜底：session invalid → cookie offline + 失效通知 → 运维重新扫码，属可接受行为）。相应修订 Phase 3 任务、验收标准与风险表（同步修订技术提案 v1.3：实现点 4 与决策 13 撤销）。其余内容不变。
>
> v2.0 变更（2026-09-14）：按 PRD/提案 v1.2 全面重写。v1.x 覆盖的多平台化全量范围已随 v1.0 实现上线（原需求总分支 `prd/weibo-cookie-crawl`，PR #88 已合并），本 Plan 范围收敛为**唯一剩余增量：cookie 服务代理 XHR 端点 + crawler 登录态请求改走代理 + 浏览器上下文按需恢复**。旧版全文见 git 历史。

## 1. 概述

本 Plan 基于 PRD v1.2 与技术提案 v1.3 编写，指导「微博登录态请求经 Cookie 服务代理 XHR」增量的编码实施。v1.0 能力（cookie 服务多平台化、微博扫码登录/保鲜/自愈、登录 cookie 取用、分平台失效通知、前端双按钮）已全量上线，**本期剩余增量仅三块**：① 契约 `contracts/cookie.openapi.yaml` 新增 `POST /api/cookie/weibo/proxy`（并顺带补 `AccountItem.cookieId` 声明，对齐既有实现）；② cookie 服务——`BrowserSessionManager` 新增 `GetOrRestoreContextAsync`（浏览器上下文按需恢复，修进程重启/上下文丢失后 `_contexts` 为空的既有缺口）与 `ProxyRequestAsync`（页面上下文内 fetch），`WeiboCookieDispatchController` 挂 `proxy` 动作（白名单 + 寻址 + 状态映射 + per-session 串行）；③ crawler——`CookieServiceClient.ProxyWeiboAsync`、`WeiboApiClient` 登录态路径改经代理（匿名访客直连不变）、`WeiboTimelineJob` 新增代理异常分支。另含 apphost 浏览器会话目录评估（结论：容器内默认可写路径即可，**不挂数据卷、编排零变更**，经人类确认）。实施全程遵循 `docs/engineering/common-rules.md`（TDD、契约式开发、核心逻辑覆盖率 ≥ 80%）、`backend-rules.md`、`frontend-rules.md` 及各子项目 AGENTS.md。

### 1.0 上游核实结论（代码现状复查）

对需求总分支 `feat/weibo-cookie-crawl` 代码核实（代码与 main 一致，v1.0 已上线），提案技术路径**整体可行**，现状细节与处理方式如下：

- **`_contexts` 恢复缺口属实**：`BrowserSessionManager._contexts`（`servers/cookie/Browser/BrowserSessionManager.cs:14`，`ConcurrentDictionary<string, IBrowserContext>`）唯一填充点在 `CreateLoginSessionAsync` 末尾（:107）；`RefreshSessionAsync`（:236-329）、`ExtractCookiesAsync`（:198-220）均 `_contexts.TryGetValue` miss 即抛 `InvalidOperationException("No browser context ...")`（:240-243/:200-203）。该异常经 `SessionRefreshJob.RefreshSessionForPlatformAsync`（`servers/cookie/Refresh/SessionRefreshJob.cs:127-162`）catch → `IncrementRefreshFailureAsync` → 连续 3 次 `InvalidateSessionAsync`（:164-184），以及 `InvalidationService.ReportInvalidAsync`（`servers/cookie/Refresh/InvalidationService.cs:168-191`）单次即判失效——上下文丢失后健康 session 会被误判 invalid。**处理**：新增 `GetOrRestoreContextAsync`（任务 2.2），三条路径统一改走该入口。**恢复生效的前提是 `UserDataDir` 尚存**：进程重启但目录保留（如 Aspire dev 同机临时目录、容器内进程级重启）可恢复；目录已随容器销毁时不恢复，走既有失效状态机兜底（见 6 风险表）。
- **BrowserSessionManager 无内部串行机制**：per-session 互斥全靠外部 Redis 分布式锁，锁 key 既有格式 ``$"lock:cookie-refresh:{profile.LockKeyPrefix}{session.Id}"``（`SessionRefreshJob.cs:101`、`InvalidationService.cs:90`；weibo `LockKeyPrefix="weibo:"`，bilibili 空前缀保持旧 key）。**处理**：proxy 路径复用同一 key 空间加锁（任务 2.3），与保鲜/宣告/恢复天然互斥，不新增锁空间、bilibili 行为零改动。
- **weibo 侧组件 keyed 注入**：`Program.cs:91-117` 以 `AddKeyedSingleton("weibo", ...)` 注册 `IBrowserSessionManager`/`ICookieSessionRepository`/`ICookiePoolService`/`ICookieDispatchService`/`IInvalidationService` 等，控制器经 `[FromKeyedServices("weibo")]` 消费（`WeiboCookieAccountController.cs:29-33`）；bilibili 非 keyed 注册保持既有行为。proxy 实现沿用同一注入方式，符合 `servers/cookie/AGENTS.md` 约定。
- **WeiboCookieDispatchController 现状**（`servers/cookie/Controllers/WeiboCookieDispatchController.cs:9-110`）：路由 `[Route("api/cookie/weibo")]`，已有 `GET available`/`POST checkout`/`POST report-invalid`/`POST cooldown` 四动作；DTO 定义在 `Controllers/CookieDispatchController.cs:109-126`。**处理**：proxy 作为该控制器第五动作挂载（任务 2.3），不新增控制器。
- **契约漂移属实**：`AccountItem` 实现实际返回 `cookieId`（`WeiboCookieAccountController.cs:63`、`CookieAccountController.cs:190`），但 `contracts/cookie.openapi.yaml` 的 `AccountItem` schema（:290-316）未声明该字段。契约中无 proxy 端点（weibo 组现有 8 端点，:145-280）。**处理**：任务 0.1 一并补齐。
- **`CookieServiceOptions` 无代理专用超时**（`servers/cookie/Configuration/CookieServiceOptions.cs:3-17`）：现有 `RefreshNavTimeoutSeconds=30`/`ReportInvalidTimeoutSeconds=60` 等均语义专属。**处理**：新增 `ProxyRequestTimeoutSeconds`（默认 30，任务 2.1）——属 cookie 服务内部配置、带默认值，存量部署零配置变更，不违背提案 §9.2「无新增配置项（crawler 面）」的本意。
- **crawler 登录态请求确为手工头直连**：`WeiboApiClient`（类定义于 `servers/crawler/Weibo/IWeiboApiClient.cs`）直连 `https://m.weibo.cn/api/container/getIndex`（:14），手工注入 `Cookie: lease.Cookie`（:61-64）与 UA/`Referer`/`x-requested-with`/`mweibo-pwa`/`sec-ch-*` 等头（:51-60），URL 含 `&.rnd=`（:43-48）；凭证分流委托 `IWeiboCookieSource`（`WeiboCookieSource.cs:47-75`：登录 provider 异常降级 warning + 匿名兜底），lease 携带 `IsLoginCookie`/`CookieId`。**处理**：登录 lease 路径改调 `ProxyWeiboAsync`（任务 2.4），匿名路径一行不动；`IWeiboApiClient` 对外接口不变。
- **WeiboTimelineJob 自愈重试无需改抽象**：`HandleRiskControlAsync`（`servers/crawler/Jobs/WeiboTimelineJob.cs:173-228`）在 `ReportInvalidAsync` 返回 `Refreshed` 后直接重调 `_api.GetUserTimelineAsync`（:217-220）重试一次——重试经 `WeiboCookieSource` 重新 checkout 拿到刷新后 cookie，`WeiboApiClient` 改造后该重试**自动走代理**，Job 侧仅需新增代理专用异常的捕获分支（任务 2.5）。
- **风控分类器无需补充**：`WeiboRiskControlClassifier.IsRiskControl`（`servers/crawler/Weibo/WeiboRiskControlClassifier.cs:22-69`）已覆盖 401/403、3xx 跳 `passport.weibo.com`/`login.sina.com.cn`、可疑状态码 + 非 JSON body、JSON `ok != 1`、msg 关键词五类特征，代理透传 `{status, body}` 后判定输入形态与直连一致。
- **`CookieServiceClient` 结果类型范式可复用**（`servers/crawler/Services/CookieServiceClient.cs`）：`ReportInvalidCoreAsync`（:92-143）以 `ReportInvalidResult{Status,...}` + `ReportInvalidStatus{Refreshed,SessionInvalid,NotFound,Timeout}` 透出非 200 语义；checkout 404 返回 `WeiboCookieCheckoutResult{Available=false}` 不抛异常（:55-80）。**处理**：`ProxyWeiboAsync` 沿用结果类型范式（任务 2.4）。
- **CookieCallGate 锁 key 实为 `cookie-call:{cookieId}`**（`servers/crawler/Services/CookieCallGate.cs:25`），per-cookie 互斥已覆盖代理调用（`WeiboApiClient` 在 lease 持锁期间完成 HTTP 调用，:41）；cookie 服务侧 per-session 串行由 proxy 动作自取分布式锁保证，双侧重合无害。
- **测试设施就绪**：浏览器层抽象在 `IBrowserSessionManager`（`servers/cookie/Browser/IBrowserSessionManager.cs:3-10`），`FakeBrowserSessionManager`（`tests/Z.Vtuber.Tests/Fakes.cs:76-120`）以 `*Func` 注入行为，测试从不触碰 Playwright 类型；`FakeCookieServiceClient`（:268-324）含 weibo 三方法；`FakeWeiboCookieSource`（:364-372）/`FakeWeiboApiClient`（:396-423，支持风控后重试成功序列）齐备；HTTP 层用 `RecordingHandler`/`StubHandler : HttpMessageHandler` 打桩。新增接口方法需同步扩展 fake（任务 1.1）。
- **apphost 会话目录现状与结论**：`infra/apphost/AppHost.cs:23-24` 将 `CookieService:BrowserSessionDataDir` 指向 `Path.GetTempPath()/z-vtuber-browser-sessions`（dev 同机临时目录），无卷挂载；容器内默认 `/data/browser-sessions`（`CookieServiceOptions.cs:7`）为容器可写路径。**经人类确认：无需挂数据卷**——dev 临时目录与容器内默认可写路径均满足需求；重启/重建丢会话由既有失效状态机兜底（连续刷新失败 → session invalid → cookie offline + 失效通知 → 运维重新扫码），属可接受行为。**处理**：编排零变更，仅记录评估结论（任务 3.1）。
- **配置现状**：`WeiboCrawlerOptions`（`servers/crawler/Configuration/WeiboCrawlerOptions.cs:3-24`）`UseLoginCookie=true`/`LoginCookieRetryOnce=true` 已存在，crawler 面本期无新增配置项。

### 1.1 实施范围

- **做**：
  - 契约：`contracts/cookie.openapi.yaml` 新增 `POST /api/cookie/weibo/proxy`（请求 `{cookieId, path}`；200 `{status, body}`；400 非白名单 / 404 cookieId 不存在或非 active / 410 会话失效 / 502 浏览器侧失败 / 504 超时）；`AccountItem` 补 `cookieId` 字段声明（仅补声明，无行为变更）。
  - cookie 服务：`CookieServiceOptions` + `ProxyRequestTimeoutSeconds`；`IBrowserSessionManager` + `ProxyRequestAsync`；`BrowserSessionManager` 实现 `GetOrRestoreContextAsync` 上下文按需恢复与页面上下文 XHR；`WeiboCookieDispatchController` + `proxy` 动作（白名单校验、cookieId → 池记录 → session 寻址、分布式锁串行、状态码映射）。
  - crawler：`CookieServiceClient` + `ProxyWeiboAsync`（结果类型透出 410/404/502/504）；`WeiboApiClient` 登录 lease 路径改经代理（匿名直连不变）；`WeiboTimelineJob` + 代理异常分支（410/404 → 放弃本轮 + 退避；502/504 → 退避不宣告）。
  - 工程：apphost 会话目录评估（**结论：不挂卷、编排零变更**，经人类确认；任务 3.1）；web codegen 再生成 `schema-cookie.d.ts`（无页面行为变更）。
  - 测试：全部新增/改写测试，核心逻辑覆盖率 ≥ 80%，既有用例保持绿。
- **不做**：已上线 v1.0 能力任何改动（bilibili 全链路、微博扫码/保鲜/事件/前端页面）；匿名访客兜底直连路径；`WeiboRiskControlClassifier` 特征库；**apphost 数据卷挂载（人类确认不需要）**；代理池/UA 轮换等进阶风控；多副本 session 迁移。

### 1.2 现状基线

- **控制器动作范本**：`WeiboCookieDispatchController` 既有四动作的 DTO/异常映射/日志形态直接对照；寻址前段（cookieId → session）与 `InvalidationService.ReportInvalidAsync`（`InvalidationService.cs:67-108`）同构，实现期抽取共享或就地复用既有仓储查询（`CookieSessionRepository.GetByCookieIdAsync` 等）。
- **恢复前提数据**：`CookieSessionRecord.UserDataDir` 已持久化（创建时回填，`WeiboCookieAccountController.cs:101`），`Status`/`LastRefreshAt` 齐备，惰性恢复判定所需数据全部现成。
- **crawler 结果类型范本**：`ReportInvalidResult`/`ReportInvalidStatus`（`CookieServiceClient.cs:161-175`）同构复制为代理结果类型。
- **分布式锁**：`IDistributedLockProvider` 契约与 cookie 服务内 `RedisDistributedLockProvider` 已上线，proxy 直接复用，key 沿用 `lock:cookie-refresh:{LockKeyPrefix}{sessionId}`。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash）与 Conventional Commits：

- 本需求开发全程只使用需求总分支 `feat/weibo-cookie-crawl`：契约、实现、测试、文档全部直接在该分支提交，不切开发子分支（沿用近两期需求既定做法）。
- 需求总分支已推送 origin，需求总 PR 已存在（#96），提交照常推送更新分支。
- 提交信息使用 Conventional Commits：契约 `feat:`、测试先行 `test:`、实现 `feat:`、工程接入 `chore:`、文档 `docs:`；测试与实现分开提交。
- 改动集中在 cookie / crawler 两个子项目加契约，规模有限，主线串行即可；如需并行，按「cookie 服务线 / crawler 线」拆 subagent（契约 Phase 0 与测试设施 1.1 串行先行，见第 4 节）。

## 3. 实施阶段

按「契约先行 → TDD 红-绿 → 工程接入 → 收口验证」推进。Phase 0 串行先行；Phase 1 测试先行（1.1 设施先行，后端 1.2-1.5 与前端 1.6 可并行）；Phase 2 按 cookie 服务（2.1-2.3）→ crawler（2.4-2.5）推进，crawler 线仅依赖契约与客户端约定，可与 2.2/2.3 并行；Phase 3 与实现可并行；Phase 4 串行收口。

### Phase 0: 契约先行

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | `contracts/cookie.openapi.yaml`：① 新增 `POST /api/cookie/weibo/proxy`——request `{cookieId: string, path: string}`；response `200 {status: integer, body: string}`（微博原始状态码与响应体原样透传，含风控响应）；`400 invalid_proxy_target`（path 非白名单：不以 `/api/` 开头、含 scheme/host、含回溯符）；`404`（cookieId 不存在或非 active）；`410 session_invalid`（关联会话非 active）；`502`（浏览器侧请求失败）；`504`（超时）；v1 仅支持 GET 语义（path 含 query，body 不接收）。② `AccountItem` schema 补 `cookieId: string?` 声明（对齐既有实现，无行为变更） | `contracts/cookie.openapi.yaml` | `npx openapi-typescript` 可成功生成类型；端点与字段齐备 |

### Phase 1: 测试先行（红）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | 测试设施（串行先行）：`FakeBrowserSessionManager` 扩展——`ProxyRequestAsync` 的可注入行为（返回 `{status, body}` 或抛 502/504 对应异常）与调用记录，以及上下文恢复行为的注入点（若恢复逻辑在实现内部则以 launch 工厂/`UserDataDir` 存在性为注入缝）；`FakeCookieServiceClient` + `ProxyWeiboAsync` 桩（200 透传 / 410 / 404 / 502 / 504 各分支）；`FakeDistributedLockProvider` 沿用 | `tests/Z.Vtuber.Tests/Fakes.cs` | 编译通过（接口方法随后续任务落地） |
| 1.2 | proxy 端点测试（mock 浏览器层）：① 白名单——`/api/container/getIndex?...` 放行；`https://evil.com/...`、`//evil.com/api/x`、`/api/../../etc`、`/status/x` 等变体 400 且不发起任何浏览器调用；② cookieId 不存在 / 池记录非 active → 404；③ session 非 active → 410；④ 正常路径 200 `{status, body}` 透传（含 403/412 等业务状态码原样透传不改写）；⑤ 浏览器侧网络失败 → 502、超时 → 504；⑥ 同 session 并发 proxy 经 `lock:cookie-refresh:weibo:{sessionId}` 串行（锁未获得 → 504）；⑦ 响应不含 cookie 原文、日志不含敏感字段 | `tests/Z.Vtuber.Tests/WeiboCookieProxyTests.cs`（新） | 红 |
| 1.3 | 上下文恢复测试：① 缓存命中不重建（launch 零调用）；② miss + session active + `UserDataDir` 目录存在 → 重新 launch 并回填 `_contexts`；③ session 非 active / 目录不存在 → 不恢复，按既有单次失败语义抛错（**不直接判失效**——目录随容器销毁场景走既有连续失败计数 → 状态机兜底，见 6）；④ 恢复失败（launch 抛异常）按单次失败计数路径上抛；⑤ `RefreshSessionAsync`/`ExtractCookiesAsync` 改走恢复入口后既有 `SessionRefreshJobTests`/`CookieReportInvalidTests`/`WeiboCookieReportInvalidTests` 行为不回归（fake 适配后转绿） | `tests/Z.Vtuber.Tests/BrowserSessionRestoreTests.cs`（新） | 红 |
| 1.4 | crawler `ProxyWeiboAsync` 测试（`StubHandler`）：① 200 → `{status, body}` 透传；② 410 → 会话失效结果；③ 404 → NotFound 结果；④ 502/504 → 故障/超时结果（区分）；⑤ 请求路径/方法/body 正确（`/api/cookie/weibo/proxy`、POST、`{cookieId, path}`） | `tests/Z.Vtuber.Tests/CookieServiceClientTests.cs` | 红 |
| 1.5 | `WeiboApiClient`/`WeiboTimelineJob` 测试：① 登录 lease → 断言走 `ProxyWeiboAsync`（RecordingHandler 零直连调用），path 含 `containerid=107603{uid}`/`since_id`/`rnd`；② 匿名 lease → 直连不变（既有头部构造零改动，既有用例保持绿）；③ 代理回传 200 + 风控 body → 既有分类器命中抛 `WeiboRiskControlException`（`CookieId`/`IsLoginCookie` 正确）；④ Job：自愈 `Refreshed` 后重试经代理成功；⑤ Job：代理 410/404 → 放弃本轮 + `ReportRiskControl` 退避、不二次宣告；⑥ Job：代理 502/504 → 退避、**不宣告 cookie 失效** | `tests/Z.Vtuber.Tests/WeiboApiClientTests.cs`、`WeiboTimelineJobTests.cs` | 红 |
| 1.6 | 前端测试适配：codegen 再生成后 `schema-cookie.d.ts` 类型对齐；`client.test.ts` 既有断言适配 `AccountItem.cookieId`（若有构造该类型的用例）；无页面行为变更 | `apps/web/src/api/client.test.ts` | `npm run test` 绿（无红阶段，随 0.1 后执行） |

### Phase 2: 实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | 配置与接口（串行先行）：① `CookieServiceOptions` 新增 `ProxyRequestTimeoutSeconds`（默认 30，`servers/cookie/appsettings.json` 可不覆盖）；② `IBrowserSessionManager` 新增 `Task<ProxyFetchResult> ProxyRequestAsync(string accountId, string path, CancellationToken ct)`（`ProxyFetchResult{Status, Body}`）；`GetOrRestoreContextAsync` 作为实现内部方法（不进接口，保持 fake 面最小——若测试需要再评估上提） | `servers/cookie/Configuration/CookieServiceOptions.cs`、`servers/cookie/Browser/IBrowserSessionManager.cs` | 编译通过 |
| 2.2 | `BrowserSessionManager` 改造：① `GetOrRestoreContextAsync(accountId, ct)`——`_contexts` miss 时，session `active` 且 `UserDataDir` 目录存在则 `LaunchPersistentContextAsync(userDataDir, Headless=true)` 恢复并回填缓存；非 active/目录缺失按既有 miss 语义抛 `InvalidOperationException`；launch 失败原样上抛（由调用方既有失败计数语义消化，不直接判失效）；② `RefreshSessionAsync`/`ExtractCookiesAsync` 改走该入口（bilibili 路径同享，属缺陷修复、对外语义不变）；③ `ProxyRequestAsync`——经恢复入口取上下文；取/建页面（`context.Pages` 无可用页则 `NewPageAsync`；当前页非同源则 `GotoAsync(HomeUrl)`）；`page.EvaluateAsync` 执行 `fetch(path)`（同源相对 URL，`credentials: 'same-origin'`），回传 `{status, body}`（body 取原始文本）；整体包 `ProxyRequestTimeoutSeconds` 超时——超时抛 `TimeoutException`、浏览器侧网络/执行失败抛专用异常（供控制器映射 502/504） | `servers/cookie/Browser/BrowserSessionManager.cs` | 1.3 转绿；既有刷新/宣告用例不回归 |
| 2.3 | proxy 端点：`WeiboCookieDispatchController` + `POST proxy` 动作——① 白名单校验（`/api/` 前缀、无 `://`、无 `..`、非 `//` 开头）→ 400；② 寻址：cookieId → weibo 池记录（不存在/非 active → 404）→ `AccountId` → session（非 active → 410）；③ 经 `IDistributedLockProvider` 取 `lock:cookie-refresh:weibo:{sessionId}`（TTL `RefreshLockTtlSeconds`、等待 `LockAcquireWaitMs`，未获得 → 504）后调 `ProxyRequestAsync`；④ 结果映射：成功 → 200 `{status, body}`；`TimeoutException` → 504；浏览器侧失败 → 502；⑤ DTO（`ProxyWeiboRequest`/`ProxyWeiboResponse`）入 `CookieDispatchController.cs` 既有 DTO 区；日志沿用短哈希惯例、不记 body 敏感字段 | `servers/cookie/Controllers/WeiboCookieDispatchController.cs`、`Controllers/CookieDispatchController.cs` | 1.2 转绿 |
| 2.4 | crawler 客户端与 ApiClient：① `CookieServiceClient` + `ProxyWeiboAsync(cookieId, path)`——200 → `WeiboProxyResult{Ok, Status, Body}`；410/404/502/504 → 结果类型透出（`WeiboProxyStatus{Ok, SessionInvalid, NotFound, Failed, Timeout}`），沿用 `ReportInvalidCoreAsync` 范式；② `WeiboApiClient`：登录 lease（`IsLoginCookie=true`）路径——构造 `path = /api/container/getIndex?containerid=107603{uid}[&since_id=...]&rnd=...`，调 `ProxyWeiboAsync`；`Ok` → `{status, body}` 走既有 `_classifier.IsRiskControl` 与 JSON 解析（与直连同一套后处理，可抽私有方法复用）；`SessionInvalid`/`NotFound`/`Failed`/`Timeout` → 抛对应专用异常（`WeiboProxySessionInvalidException`/`WeiboProxyUnavailableException` 等，携带 `CookieId`）；匿名 lease 路径一行不动 | `servers/crawler/Services/CookieServiceClient.cs`、`servers/crawler/Weibo/IWeiboApiClient.cs` | 1.4、1.5①-③ 转绿 |
| 2.5 | `WeiboTimelineJob` 代理异常分支：`HandleRiskControlAsync` 既有逻辑不变（重试经 ApiClient 自动走代理）；新增 catch——`WeiboProxySessionInvalidException`/NotFound → 记日志 + `CrawlBackoffRegistry.ReportRiskControl("weibo:{uid}")` 放弃本轮（cookie-invalid 事件已由 cookie 服务发布）；`WeiboProxyUnavailableException`（502/504）→ 退避不宣告；均不产生请求风暴 | `servers/crawler/Jobs/WeiboTimelineJob.cs` | 1.5④-⑥ 转绿 |

### Phase 3: 工程接入

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 3.1 | apphost 会话目录评估（**无编排变更**）：确认结论——dev（Aspire 同机临时目录）与容器部署（容器内默认可写路径 `/data/browser-sessions`，`CookieServiceOptions` 默认值）均**无需挂数据卷**即可满足需求（人类确认）；重启/重建丢会话由既有失效状态机兜底（连续刷新失败 → session invalid → cookie offline + 失效通知 → 运维重新扫码），属可接受行为；`AppHost.cs` 保持现状零改动；PR #96 描述中明确「无需为 cookie 服务配置浏览器会话卷」 | `infra/apphost/AppHost.cs`（零变更，仅评估） | 评估结论记录于本 Plan 与 PR 描述；确认 `AppHost.cs` 无 diff |
| 3.2 | web codegen：`npm run codegen` 再生成 `src/api/schema-cookie.d.ts`（勿手改）；确认无页面代码改动需要 | `apps/web/src/api/schema-cookie.d.ts`（生成） | `npm run lint`/`npm run build` 通过 |

### Phase 4: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 4.1 | `dotnet test tests/Z.Vtuber.Tests` 全绿（含既有回归：`WeiboCookieDispatchTests`/`WeiboCookieAccountControllerTests`/`WeiboCookieReportInvalidTests`/`SessionRefreshJobTests`/`CookieReportInvalidTests`/`WeiboApiClientTests`/`WeiboTimelineJobTests`/`CookieServiceClientTests` 等）；cookie/crawler 核心逻辑覆盖率 ≥ 80%；`npm run test`/`lint`/`build` 全绿；grep 确认：crawler 登录态路径无 `m.weibo.cn` 直连残留（匿名路径除外）、无手工 `Cookie` 头注入残留于登录路径；`git diff main -- infra/apphost/` 为空 | 全部通过 |
| 4.2 | 编排手工验证（z-aspire-orchestrator）：Aspire 拉起全量 → 微博帐号扫码登录 → crawler 登录态请求经 proxy 取到时间线（cookie 服务日志可见 proxy 调用，crawler 无直连微博登录态请求）→ 风控/失效自愈后经代理重试一次 → 重启 cookie **进程**（目录尚存场景）后无需重扫，保鲜与代理自动恢复（验证惰性恢复）→ 白名单外目标 400 → bilibili 全链路回归 | 手工验证记录 |
| 4.3 | AGENTS.md 同步：更新 `servers/cookie/AGENTS.md`（proxy 端点、`ProxyRequestAsync`/上下文恢复、per-session 串行约定、`ProxyRequestTimeoutSeconds`）、`servers/crawler/AGENTS.md`（登录态走代理、`ProxyWeiboAsync` 结果语义、代理异常处置）；技术提案状态改为已实现并回链本 Plan | 文档与实现一致 |

## 4. 并行策略

- Phase 0（契约）串行先行——cookie 控制器、crawler client、web codegen 均依赖契约。
- Phase 1：1.1（测试设施）串行先行；随后 cookie 侧测试（1.2/1.3）与 crawler 侧测试（1.4/1.5）互不依赖，可拆 subagent 并行；1.6 随契约完成后即可进行。
- Phase 2：2.1（配置与接口）串行先行；2.2 → 2.3 cookie 线内串行；crawler 线（2.4 → 2.5）仅依赖契约与客户端结果类型约定，可与 cookie 线并行——按 z-coding 规范拆 subagent 时各 subagent 负责「实现 → 提交 → 测试修复 → 提交」闭环；共享文件（`Fakes.cs`）归 1.1 主线先行，避免并行冲突。
- Phase 3（3.1 为纯评估零改动、3.2 codegen）与 Phase 2 互不依赖可并行。
- Phase 4 串行收口。全部提交在需求总分支 `feat/weibo-cookie-crawl`。

## 5. 验收对照

| PRD/提案验收项 | 实现任务 | 验证方式 |
|-----------|---------|---------|
| proxy 白名单生效（非 `m.weibo.cn/api/*` 400）、`{status, body}` 原样透传、400/404/410/502/504 语义正确 | 0.1、2.1、2.2③、2.3 | 1.2 + 手工验证（4.2） |
| 登录态请求全部经代理（crawler 不直连微博登录态接口）、风控自愈后代理重试一次、无请求风暴 | 2.4、2.5 | 1.5 + grep（4.1）+ 手工验证（4.2） |
| 无可用登录 cookie 回退匿名直连、可观测告警 | 既有 `WeiboCookieSource`（零改动） | 既有 `WeiboCookieSourceTests` 保持绿（4.1） |
| 目录尚存前提下 active 会话上下文按需恢复，代理/保鲜/宣告自动可用 | 2.2①② | 1.3 + 进程重启手工验证（4.2） |
| 同一帐号代理/保鲜/宣告/恢复串行执行 | 2.2①、2.3③ | 1.2⑥ + 手工验证（4.2） |
| 浏览器会话目录使用容器内默认可写路径即可（不挂卷，人类确认）；目录丢失时失效状态机自动兜底并通知 | 3.1（评估，零变更） | `AppHost.cs` 无 diff（4.1）+ 1.3③ |
| bilibili 链路、微博扫码/保鲜/事件/前端既有行为零回归 | 2.2②（恢复入口共享） | 既有测试全绿（4.1）+ bilibili 回归（4.2） |
| 核心逻辑覆盖率 ≥ 80%、全量测试绿 | 4.1 | `dotnet test` / `npm run test` |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| 页内 XHR 与导航互斥 | `page.EvaluateAsync` fetch 与保鲜 `GotoAsync`/宣告刷新并发操作同一上下文会相互干扰 | proxy 与保鲜/宣告/恢复共用 `lock:cookie-refresh:weibo:{sessionId}` 分布式锁（2.2①/2.3③）；crawler 侧 `CookieCallGate` per-cookie 互斥叠加（1.5① 锁定） |
| 恢复后页面态不确定 | 重启恢复出的持久化上下文可能无页面或停留在非 m.weibo.cn 页 | `ProxyRequestAsync` 内「取/建页面 + 非同源则 goto `HomeUrl`」归一化（2.2③）；1.3 用例覆盖 |
| 微博风控对页内 XHR 仍可能命中 | 代理提升通过率但不保证免疫 | 透传语义 + 既有分类器 + report-invalid 自愈一次 + 退避链路完整保留；匿名兜底不变 |
| 会话目录不持久（不挂卷，人类确认） | 容器重建/换机后 `UserDataDir` 丢失，Mongo 中 session 仍为 active 但无法恢复 | 恢复以目录存在为前提，缺失时按单次失败计数（1.3③）→ 定时路径连续失败触发既有失效状态机：session invalid + cookie offline + 失效通知，运维重新扫码接入——v1.0 既有行为，属已确认的可接受兜底；无新增告警需求 |
| `_contexts` 恢复失败被误判失效 | launch 失败若走「单次即判失效」路径会误伤健康 session | 恢复失败按**单次失败计数**语义上抛（定时路径连续 3 次才判失效）；即时宣告路径维持单次即判的既有语义（宣告本身要求强确定结论，提案决策 12 已声明） |
| 锁等待导致 proxy 长尾 | 保鲜/宣告持锁期间 proxy 等待 | 等待上限 `LockAcquireWaitMs`（默认 5s），超时映射 504，crawler 记退避不宣告；TTL `RefreshLockTtlSeconds`（90s）兜底释放 |
| 契约漂移防护 | `AccountItem.cookieId` 属「实现先行、契约补声明」 | 0.1 补齐；后续字段变更坚持契约先行（common-rules） |
| 依赖 | 无新增包/服务依赖；Playwright 1.62、Testcontainers 夹具、openapi-typescript 均既有 | 已核实（1.0） |

## 7. 相关文档

- PRD：`docs/product/draft/20260913-weibo-cookie-crawl-prd.md`（v1.2）
- 技术提案：`docs/engineering/proposals/20260913-proposal-weibo-cookie-crawl.md`（v1.3）
- 契约：`contracts/cookie.openapi.yaml`（Phase 0 增量）
- 前置 Plan：[[20260911-plan-cookie-service]]（Cookie 服务新建）、[[20260909-plan-weibo-crawler]]（微博时间线抓取）
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`docs/engineering/frontend-rules.md`、`servers/cookie/AGENTS.md`、`servers/crawler/AGENTS.md`、`servers/common/AGENTS.md`、`apps/web/AGENTS.md`
