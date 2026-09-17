---
description: 无头浏览器页面生命周期优化实现 Plan——基于 PRD v1.0 与技术提案（feat/cookie-page-lifecycle）的实施阶段划分与任务分解；BrowserSessionManager 改「惰性打开 + 空闲延时关闭」：新增 IdleCloseScheduler 可测组件与 per-account Gate，ExtractCookiesAsync/RefreshSessionAsync 惰性拉起 persistent context，CookieServiceOptions 新增 PageIdleCloseDelaySeconds（默认 60），TDD 测试先行（fake 浏览器接口），对外契约零变更
type: Permanent
---

# 无头浏览器页面生命周期优化 - 实现 Plan

**功能名称**: 无头浏览器页面生命周期优化
**关联 PRD**: `docs/product/draft/20260918-cookie-page-lifecycle-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20260918-proposal-cookie-page-lifecycle.md`
**Plan 版本**: v1.0
**创建日期**: 2026-09-18
**作者**: Workflow Agent（代起草）
**feat-branch**: `feat/cookie-page-lifecycle`（需求总分支，已推送 origin；需求总 PR：<https://github.com/Deliay/z-vtuber/pull/120>）

## 1. 概述

本 Plan 基于 PRD v1.0 与技术提案编写，指导「无头浏览器页面生命周期优化」的编码实施。改动分三块：① 测试先行——固化 `IdleCloseScheduler` 的「到期关闭/使用顺延/取消关闭/异常仅记日志」行为与 `BrowserSessionManager` 的「惰性打开/用后计时/删除即关」行为（红）；② 实现——新增 `IdleCloseScheduler`、`CookieServiceOptions.PageIdleCloseDelaySeconds` 配置与校验，`BrowserSessionManager` 改造为惰性打开 + per-account Gate + 空闲延时关闭；③ 收口——全量测试回归、改动面核对、手工验证与 `servers/cookie/AGENTS.md` 同步。无 OpenAPI 契约变更、无数据库结构变更、无 web 前端改动。实施全程遵循 `docs/engineering/common-rules.md`（TDD、核心逻辑覆盖率 ≥ 80%）、`backend-rules.md` 及 `servers/cookie/AGENTS.md`。

### 1.0 上游核实结论（代码现状复查）

对需求总分支 `feat/cookie-page-lifecycle` 代码核实，提案技术路径**可行**，现状细节如下，处理方式在对应任务中给出：

- **context 预存 + 直抛点确认**：`BrowserSessionManager._contexts`（`servers/cookie/Browser/BrowserSessionManager.cs:14`）为 `ConcurrentDictionary<string, IBrowserContext>`，仅在 `CreateLoginSessionAsync`（:107）写入；`ExtractCookiesAsync`（:200-203）、`RefreshSessionAsync` 探测路径（:240-243）与导航路径（:264-267）三处在 context 缺失时抛 `InvalidOperationException`。**处理**：三处统一改经私有 `GetOrLaunchContextAsync(accountId)` 惰性获取（任务 2.3）。
- **启动逻辑提取边界**：`CreateLoginSessionAsync`（:33-120）中 `LaunchPersistentContextAsync` 仅 :35-42 三行（userDataDir 拼路径 + `Directory.CreateDirectory` + 启动），其后 page 创建/QR 截图/nav 监听均为扫码登录专属。**处理**：只把 :35-42 提取为 `GetOrLaunchContextAsync`，page 创建留在登录路径（任务 2.3）。
- **bilibili 刷新依赖既有 page**：导航路径取 `context.Pages[0]`（:269-275），context 重开后 `Pages` 为空，现状会抛 `InvalidOperationException`（:270-273）。**处理**：惰性打开后若 `Pages` 为空则 `NewPageAsync` 新建页面再导航（提案实现点 1 已明确，任务 2.3 落实）。
- **登录成功路径自动纳入空闲管理**：`LoginSessionWorker.MonitorLoginAsync` 在登录成功后调用 `ExtractCookiesAsync`（`servers/cookie/Browser/LoginSessionWorker.cs:108`）。`ExtractCookiesAsync` 改为「使用后启动/重置空闲计时」后，登录成功首次提取即自然纳入空闲关闭，**`LoginSessionWorker` 零改动**，与提案 §2.3 一致。
- **pending 阶段不参与空闲关闭**：`CreateLoginSessionAsync` 写入 `_contexts` 但不启动计时；`WaitForNavResponseAsync` 的探测分支直读 `_contexts`（:139）仅 pending 阶段调用，context 必然存在，保持不变。
- **当前无进程内 per-account 互斥**：现状仅靠 Redis 分布式锁跨实例串行，进程内无 per-account 信号量。**处理**：新增 per-account 状态条目 `{ Context, SemaphoreSlim Gate, IdleCloseCts }`（提案实现点 2），Gate 持有范围限定「获取 context → 完成本次操作」，空闲计时在 Gate 外等待（任务 2.3）。
- **配置与校验链位置确认**：`CookieServiceOptions`（`servers/cookie/Configuration/CookieServiceOptions.cs`）无 `PageIdleCloseDelaySeconds`；`Program.cs:30-34` 为既有 `AddOptions<CookieServiceOptions>().BindConfiguration(...).Validate(...).ValidateOnStart()` 链。**处理**：选项类追加属性（默认 60），校验链追加一条 `.Validate(o => o.PageIdleCloseDelaySeconds > 0, ...)`（任务 2.2）。
- **双实例注册点确认**：`BrowserSessionManager` 在 `Program.cs:64-68`（bilibili 非 keyed）与 :92-96（weibo keyed）各构造一次，构造参数相同。**处理**：若构造函数新增依赖（如 `IdleCloseScheduler` 工厂/延时参数），两处注册同步修改（任务 2.3）。
- **接口签名不变则测试基建零改动**：`FakeBrowserSessionManager`（`tests/Z.Vtuber.Tests/Fakes.cs:76`）实现 `IBrowserSessionManager` 五个方法；`IBrowserSessionManager`（`servers/cookie/Browser/IBrowserSessionManager.cs`）签名不变，故 `SessionRefreshJobTests`、`CookieReportInvalidTests`、`WeiboCookieReportInvalidTests`、两个 Dispatch/AccountController 测试全部零改动即应通过。
- **浏览器接口可 fake**：`IBrowserType`/`IBrowserContext`/`IPage`/`IAPIRequestContext` 均为 Playwright 接口，`BrowserSessionManager` 仅经 `_browserType.LaunchPersistentContextAsync` 入口触达浏览器，可手写 fake 屏蔽真实浏览器做生命周期单测（任务 1.2）。
- **无契约/前端波及面**：grep 确认本次不改 `contracts/`、`apps/web/`、`servers/crawler`；`SessionRefreshJob`/`InvalidationService`/各 Controller 均不改代码。

### 1.1 实施范围

- **做**：
  - 测试：`IdleCloseSchedulerTests`（新增，到期/顺延/取消/异常兜底）；`BrowserSessionManagerTests`（新增，fake 浏览器接口验证惰性打开/用后计时/删除取消计时/双平台路径）；配置校验测试（`PageIdleCloseDelaySeconds > 0`）。
  - cookie 服务：新增 `Browser/IdleCloseScheduler.cs`；`CookieServiceOptions` 新增 `PageIdleCloseDelaySeconds`（默认 60）；`Program.cs` 校验链补新配置；`BrowserSessionManager` 惰性打开 + per-account Gate + 空闲延时关闭改造；`IBrowserSessionManager` 注释明确「按需打开」语义。
  - 文档：`servers/cookie/AGENTS.md` 的 `Browser/` 模块描述同步「按需打开、空闲延时关闭」。
- **不做**：对外 API/契约（`contracts/cookie.openapi.yaml`）变更；刷新节奏（`RefreshIntervalMinutes=10`）、失败计数与失效判定策略调整；浏览器进程池化/跨帐号共享；weibo 探测路径改页面导航（维持仅 `APIRequest`）；`SessionRefreshJob`/`InvalidationService`/`LoginSessionWorker`/各 Controller 代码改动；AppHost/部署配置改动。

### 1.2 现状基线

- **浏览器管理**：`BrowserSessionManager.cs`（:33-120 登录创建、:198-220 提取 cookie、:222-234 删除、:236-329 刷新、:331-354 Dispose）。
- **配置**：`CookieServiceOptions.cs` 全量 11 个既有选项；`Program.cs:30-34` 校验链范本。
- **测试基建**：`tests/Z.Vtuber.Tests/Fakes.cs:76` `FakeBrowserSessionManager`（接口 fake 范本）；`SessionRefreshJobTests.cs`、`CookieReportInvalidTests.cs`、`WeiboCookieReportInvalidTests.cs`（回归基准）；xUnit + 手写 fake 为项目既定模式。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash）与 Conventional Commits：

- 本需求开发全程只使用需求总分支 `feat/cookie-page-lifecycle`：实现、测试、文档全部直接在该分支提交，不切开发子分支（沿用上期需求既定做法）。
- 需求总分支已推送 origin，需求总 PR 已存在（#120），提交照常推送更新分支。
- 提交信息使用 Conventional Commits：测试先行 `test:`、实现 `feat:`、文档 `docs:`；测试与实现分开提交。
- 改动集中在 `servers/cookie` 与 `tests/Z.Vtuber.Tests` 两处，单线串行实施即可，不拆并行 subagent（见第 4 节）。

## 3. 实施阶段

按「TDD 红 → 绿 → 收口验证」推进。Phase 1 测试先行；Phase 2 实现；Phase 3 串行收口。

### Phase 1: 测试先行（红）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | `IdleCloseScheduler` 测试（新文件）：① **到期关闭**——以短延时（如 100ms）调度后，到期触发该 key 的关闭回调一次；② **使用顺延**——等待期间再次 `Touch(key)`，首个到期点不触发，从最后一次 `Touch` 起算到期才触发；③ **取消关闭**——`Touch` 后 `Cancel(key)`，到期不触发；④ **异常兜底**——关闭回调抛异常时仅记日志、不向外抛出、不影响其他 key 的调度；⑤ **并发安全**——多 key 并发 `Touch`/`Cancel` 无异常、各自独立计时 | `tests/Z.Vtuber.Tests/IdleCloseSchedulerTests.cs`（增） | 编译失败（类型尚不存在）→ 占位最小声明后红 |
| 1.2 | `BrowserSessionManager` 生命周期测试（新文件，手写 fake `IBrowserType`/`IBrowserContext`/`IPage`/`IAPIRequestContext`，`PageIdleCloseDelaySeconds` 配短延时）：① **惰性打开**——`ExtractCookiesAsync` 在 context 不存在时调用 `LaunchPersistentContextAsync` 一次并成功返回 cookie（替代现状抛 `InvalidOperationException`）；② **复用**——连续两次调用 launcher 仅一次；③ **用后计时关闭**——使用后到期调用 `context.CloseAsync()`；④ **顺延**——等待关闭期间再次使用，`CloseAsync` 不在首个到期点触发；⑤ **删除即关**——`DeleteSessionAsync` 取消未触发计时并立即关闭，到期不再二次关闭；⑥ **weibo 探测路径**——profile 配 `LoginStateProbeUrl` 时 `RefreshSessionAsync` 惰性打开且仅走 `APIRequest` 不建 page；⑦ **bilibili 导航路径**——重开后 `Pages` 为空时自动 `NewPageAsync` 再导航 | `tests/Z.Vtuber.Tests/BrowserSessionManagerTests.cs`（增） | 红（惰性打开/计时逻辑尚不存在） |
| 1.3 | 配置校验测试：参照 `BilibiliCrawlerOptionsTests` 模式，断言 `PageIdleCloseDelaySeconds` 默认 60、为 0/负数时 options 校验失败 | `tests/Z.Vtuber.Tests/CookieServiceOptionsTests.cs`（增） | 编译失败（属性尚不存在）→ 占位后红 |

### Phase 2: 实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | 新增 `IdleCloseScheduler`：内部组件，注入延时时长、`ILogger` 与关闭回调 `Func<string, CancellationToken, Task>`；维护 per-key `CancellationTokenSource`，`Touch(key)` 取消旧 CTS 并新建 `Task.Delay(delay, ct)` 到期任务（到期触发回调，异常仅记日志），`Cancel(key)` 取消并移除；`IAsyncDisposable` 清理全部未触发计时 | `servers/cookie/Browser/IdleCloseScheduler.cs`（增） | 1.1 转绿 |
| 2.2 | 配置项：`CookieServiceOptions` 追加 `public int PageIdleCloseDelaySeconds { get; set; } = 60;`（置于 `RefreshNavTimeoutSeconds` 旁）；`Program.cs:30-34` 校验链追加 `.Validate(o => o.PageIdleCloseDelaySeconds > 0, "PageIdleCloseDelaySeconds 必须大于 0")` | `servers/cookie/Configuration/CookieServiceOptions.cs`、`servers/cookie/Program.cs` | 1.3 转绿 |
| 2.3 | `BrowserSessionManager` 改造：① `_contexts` 改为 per-account 状态条目 `{ IBrowserContext Context, SemaphoreSlim Gate }` 字典 + 注入/自建 `IdleCloseScheduler`；② 提取 `CreateLoginSessionAsync` 中 :35-42 为私有 `GetOrLaunchContextAsync(accountId)`（命中直接返回，未命中冷启动）；③ `ExtractCookiesAsync`/`RefreshSessionAsync` 改经 Gate 互斥 + `GetOrLaunchContextAsync` 惰性获取，进入时 `Cancel` 空闲计时、完成后 `Touch` 启动/重置计时；④ bilibili 导航路径在 `Pages` 为空时 `NewPageAsync`；⑤ `CreateLoginSessionAsync` 行为不变（不启动空闲计时）；⑥ `DeleteSessionAsync`/`DisposeAsync` 先 `Cancel` 计时再关闭，关闭幂等兜底；⑦ `Program.cs:64-68` 与 :92-96 两处注册随构造函数签名同步 | `servers/cookie/Browser/BrowserSessionManager.cs`、`servers/cookie/Program.cs` | 1.2 转绿；编译通过 |
| 2.4 | `IBrowserSessionManager` 注释升级：明确 `ExtractCookiesAsync`/`RefreshSessionAsync` 为「按需打开、用后空闲延时关闭」语义（方法签名不变） | `servers/cookie/Browser/IBrowserSessionManager.cs` | 注释与实现一致 |

### Phase 3: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 3.1 | 全量测试回归：`dotnet test tests/Z.Vtuber.Tests` 全绿（既有 `SessionRefreshJobTests`/`CookieReportInvalidTests`/`WeiboCookieReportInvalidTests` 等零改动通过）；`IdleCloseScheduler` 与 `BrowserSessionManager` 生命周期改动覆盖率 ≥ 80% | 全部通过 |
| 3.2 | 改动面核对：`git diff origin/main --stat` 确认 `contracts/`、`apps/web/`、`servers/crawler` 零改动；`SessionRefreshJob.cs`/`InvalidationService.cs`/`LoginSessionWorker.cs`/Controllers 零改动 | 核对通过 |
| 3.3 | 编排手工验证（z-aspire-orchestrator，有条件就做）：本地 Aspire 拉起 cookie 服务 → 扫码登录成功后约 1 分钟观察 Firefox 进程退出；等待 10 分钟定时刷新自动拉起并完成、约 1 分钟后再次关闭；调用 `report-invalid` 同步返回新 Cookie 且总耗时在 60s 预算内；`CookieService:PageIdleCloseDelaySeconds` 改短后验证关闭节奏 | 手工验证记录（环境不可用时以 1.1/1.2 单测结论替代并在 PR 描述注明） |
| 3.4 | `servers/cookie/AGENTS.md` 同步：`Browser/` 模块的 `BrowserSessionManager` 描述补充「按需惰性打开 + 空闲延时关闭（`PageIdleCloseDelaySeconds`，默认 60s）」，`Configuration` 节补新配置项 | 文档与实现一致 |

## 4. 并行策略

- 改动集中（`servers/cookie` 4 个源码文件 + `tests/Z.Vtuber.Tests` 3 个测试文件 + 1 个 AGENTS.md），**单线串行实施**，不拆 subagent：Phase 1（1.1 → 1.2 → 1.3）→ Phase 2（2.1 → 2.2 → 2.3 → 2.4）→ Phase 3 顺序执行。
- 2.1 与 2.2 互不依赖，但均为小改动，串行成本可忽略；提交粒度保持测试/实现分离（`test:` 先行，`feat:` 随后）。
- 全部提交在需求总分支 `feat/cookie-page-lifecycle`。

## 5. 验收对照

| PRD 功能点 | 实现任务 | 验证方式 |
|-----------|---------|---------|
| 功能点 1：定时刷新按需打开、用后延时关闭 | 2.1、2.3 | 1.2③⑥⑦ + 3.1 回归 + 3.3 手工验证 |
| 功能点 2：失效自愈按需打开、用后延时关闭 | 2.3（`ExtractCookiesAsync`/`RefreshSessionAsync` 惰性化，`InvalidationService` 零改动） | 1.2① + `CookieReportInvalidTests`/`WeiboCookieReportInvalidTests` 回归 |
| 功能点 3：延时关闭期间使用顺延 | 2.1（`Touch` 重置）、2.3（Gate 保证不关闭使用中的 context） | 1.1② + 1.2④ |
| 功能点 4：扫码登录流程不受影响 | 2.3⑤（pending 不计时；登录成功提取经 `ExtractCookiesAsync` 自然纳入空闲管理） | 1.2⑤ + 既有 AccountController 测试回归 + 3.3 |
| 非功能：延时可配置、对外零变更、双平台一致 | 2.2、2.4 | 1.3 + 3.2 改动面核对 |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| 冷启动耗时逼近超时预算 | Firefox 冷启动 + userDataDir 加载计入 `ReportInvalidTimeoutSeconds`(60s)/`RefreshLockTtlSeconds`(90s) | 预期秒级；3.3 手工验证实测，若逼近预算再调大配置默认值（提案决策，不改默认则不动） |
| fake 浏览器接口成本 | `IBrowserContext`/`IPage` 接口面较大，手写 fake 只实现用到的成员 | fake 以 `NotImplementedException` 兜底未用成员；仅覆盖生命周期断言所需的 `LaunchPersistentContextAsync`/`CookiesAsync`/`Pages`/`NewPageAsync`/`APIRequest`/`CloseAsync` |
| 关闭与使用竞态 | 到期关闭与新一轮使用并发 | per-account Gate：到期任务先抢 Gate 再关闭；「使用开始」持 Gate 且先 `Cancel` 计时，顺延语义自然成立；Gate 持有范围不含 60s 空闲等待（提案实现点 3） |
| 延时任务泄漏/崩溃 | 进程退出或回调异常 | 未触发任务随进程消失无泄漏；回调异常仅记日志（1.1④ 锁定）；`DisposeAsync` 统一取消并清理 |
| 双平台行为漂移 | bilibili/weibo 两个 `BrowserSessionManager` 实例 | 同一套惰性/空闲逻辑 + 同一配置节；`Program.cs` 两处注册同步修改（2.3⑦） |
| 依赖 | Playwright persistent context 磁盘持久化（既有能力）；xUnit + 手写 fake（既有模式） | 现状已核实（§1.0） |

## 7. 相关文档

- PRD：`docs/product/draft/20260918-cookie-page-lifecycle-prd.md`（v1.0）
- 技术提案：`docs/engineering/proposals/20260918-proposal-cookie-page-lifecycle.md`
- 需求总 PR：<https://github.com/Deliay/z-vtuber/pull/120>
- 关联 Plan：[[20260911-plan-cookie-service]]（Cookie 服务初建）、[[20260913-plan-weibo-cookie-crawl]]（双平台化）
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`servers/cookie/AGENTS.md`
