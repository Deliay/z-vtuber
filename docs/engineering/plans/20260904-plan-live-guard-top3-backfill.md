---
description: 舰长 Top3 历史补录实现 Plan——基于 PRD v1.0 与技术提案 v1.0 的实施阶段划分、任务分解与验收对照；抽取 LiveGuardListFetcher 共用翻页抓取（Job 行为零变化），补录服务按 mid 重抓取 top10 逐天补齐历史快照，meta per-mid 进度标记幂等可重跑
type: Permanent
---

# 舰长 Top3 历史补录 - 实现 Plan

**功能名称**: 舰长 Top3 历史补录
**关联 PRD**: `docs/product/draft/20260904-live-guard-top3-backfill-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20260904-proposal-live-guard-top3-backfill.md`（v1.0）
**Plan 版本**: v1.0
**创建日期**: 2026-09-04
**作者**: Deliay
**feat-branch**: `feat/live-guard-top3-backfill`（需求总分支）

## 1. 概述

本 Plan 基于 PRD v1.0 与技术提案 v1.0 编写，指导舰长 Top3 历史补录的编码实施。改动分四块：① 从 `LiveGuardJob` 抽取共用翻页抓取组件 `LiveGuardListFetcher`（重构，Job 行为零变化）；② `LiveGuardRepository` 新增三个补录方法；③ top10 选取与差异计算纯函数；④ `LiveGuardTop3BackfillService` 补录编排 + crawler 启动时一次性执行 + per-mid meta 进度标记。实体、契约、索引、配置**零变更**。实施全程遵循 `docs/engineering/common-rules.md`（TDD、核心逻辑覆盖率 ≥ 80%）与 `backend-rules.md`。

### 1.0 上游核实结论（代码现状复查）

对当前需求总分支代码核实，提案技术路径**可行**，关键假设与现状一致：

- `LiveGuardJob.FetchAllPagesAsync`（`servers/crawler/Jobs/LiveGuardJob.cs:179-225`）：翻页合并循环含 cookie 传入、`GuardMaxPages` 上限、首页 top3 并入（:207-213，上期需求已落地）、页间随机延迟、`CrawlBackoffActiveException`/异常返回 `null` 语义——与提案「原样抽取」的前提一致，可纯搬移。
- `DelayBetweenPagesAsync`（:227-231）为 `internal virtual`，测试覆写点随抽取迁入 fetcher；`ToEntry(GuardUserInfo)`（:233-240）一并迁移。
- `LiveGuardRepository`（`servers/common/Persistence/LiveGuardRepository.cs`）现有 `UpsertDailySnapshotAsync` / `GetMidsWithSnapshotAsync` / `GetOnboardGuardEntriesAsync`，新增方法与既有风格（`Builders` filter/update）兼容，无结构冲突。
- meta 进度标记模式现成：`MetaRecord`（`servers/common/Persistence/MongoDbInitializer.cs:675-679`，`Key` 为 `BsonId`）+ `zvtuber_meta` 集合 + upsert 标记（`MarkMigrationAppliedAsync` :585-590）；补录服务直接 `GetCollection<MetaRecord>` 读写，不走 `MongoDbInitializer`。
- `Program.cs` 启动块（:78-91）在 `app.Run()` 前同步 await 既有索引初始化与回填；补录涉及网络抓取、耗时长，**不得**进入该同步块，须以 fire-and-forget 任务（异常捕获记日志）异步执行，不阻塞 `/health` 与各 HostedService。
- 测试设施现成：`MongoDbFixture` + `[Collection("mongo")]`；`FakeBiliApiClient.RoomGuardListProvider`（`tests/Z.Vtuber.Tests/Fakes.cs:34-39`）按 `(roomId, page)` 返回 `GuardInfo`，重抓用例零改动复用；`FakeCookiePoolService` 可直接注入。
- `LiveGuardEntry` 字段（`servers/common/Persistence/Entities/LiveGuardRecord.cs:27-39`）：`Uid` / `UserName` / `GuardLevel` / `MedalName` / `MedalLevel`，top10 排序所需的 `MedalLevel`/`GuardLevel`/`Uid` 齐备。

### 1.1 实施范围

- **做**：
  - `LiveGuardListFetcher`（crawler/Services，新）：翻页合并 + 首页 top3 并入 + 页间延迟 + 退避/失败返回 `null`，自 `LiveGuardJob` 原样抽取；`LiveGuardJob` 改为注入调用，行为零变化。
  - `LiveGuardRepository` 新增 `GetDistinctMidsAsync` / `GetSnapshotsByMidAsync` / `BackfillMissingEntriesAsync`。
  - `LiveGuardTop3BackfillPlanner`（crawler/Services，新）：`SelectTop10` / `ComputeMissing` 纯函数静态方法。
  - `LiveGuardTop3BackfillService`（crawler/Services，新）：补录编排（distinct mids → 过滤已标记 → 逐 mid 重抓 → top10 → 逐天补齐 → 写标记）+ 汇总日志；`Program.cs` DI 注册与启动一次性异步执行。
  - 单元测试与 Testcontainers 集成测试。
- **不做**：实体/契约/索引/配置变更；对外 API 或后台界面；当日快照处理；补入条目来源标注；常驻周期任务。

### 1.2 现状基线

- **Job 依赖**（`LiveGuardJob.cs:27-50`）：构造函数注入 `IBiliApiClient` 等 10 项；抽取后 `_bili` 与 `_random` 仅服务于翻页逻辑，随 `FetchAllPagesAsync` 迁出，Job 改为注入 `LiveGuardListFetcher`（`_options` 保留：周期/时刻参数仍由 Job 使用）。
- **快照落库**（`LiveGuardJob.cs:166-176`）：`UpsertDailySnapshotAsync` 当日快照路径不触碰；补录只写 `Date < 今日` 的历史快照，两者写入集合不相交。
- **roomId 解析**（`LiveGuardJob.cs:148-158`）：`AccountBasicInfoRepository.GetLatestRoomIdByMidAsync` 缺失则 `IAccountInfoCrawler.CrawlAsync` 后重查，仍缺记日志跳过——补录服务复用同一路径（直接拷贝该段语义，不另行抽象，两处共十余行）。
- **cookie 获取**（`LiveGuardJob.cs:137-146`）：`ICookiePoolService.NextAsync`，`NoAvailableCookieException` 记警告返回——补录整轮获取一次、逐 mid 复用同一 cookie 语义与 Job 单 mid 一致；无可用 cookie 时整轮记日志退出。
- **既有回填先例**（`MongoDbInitializer.BackfillLiveEventSenderAsync` 等）：「meta 标记 + 残余检查」模式，补录的「per-mid 标记 + distinct mids 差集」与之同构。
- **测试基线**（`tests/Z.Vtuber.Tests/LiveGuardJobTests.cs`）：`CountingGuardJob`（:35-60）覆写 `DelayBetweenPagesAsync` 计数延迟；翻页/合并/top3/退避/页数上限用例挂在 Job 上——抽取后这些用例迁移至 fetcher 测试，Job 测试以 fake fetcher 锁定编排语义。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash）与 Conventional Commits：

- 本需求开发全程只使用需求总分支 `feat/live-guard-top3-backfill`：实现、测试、文档全部直接在该分支提交，不切开发子分支（沿用近两期需求的既定做法；提案头部「实现分支另行切出」以本 Plan 的既定做法为准）。
- 需求总分支已推送 origin（最新提交 `89ef161`），需求总 PR 已存在：`https://github.com/Deliay/z-vtuber/pull/66`，提交照常推送更新分支。
- 提交信息使用 Conventional Commits（`test:` / `refactor:` / `feat:` / `docs:`），测试先行与实现分开提交；fetcher 抽取为纯重构，单独以 `refactor:` 提交。

## 3. 实施阶段

按 TDD 红-绿-重构推进。Phase 1 各测试文件互不依赖，可并行；Phase 2 中 2.1（抽取重构）先行串行，2.2 与 2.3 可并行；Phase 3 串行收口。

### Phase 1: 测试先行（红）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | 新建 `LiveGuardListFetcherTests`：自 `LiveGuardJobTests` 迁移翻页合并语义用例并锁定 fetcher 契约——① 多页合并 uid 去重；② 首页 top3 并入（含已下船用户）、与分页名单重叠去重、`Top3` 为 null/空两种形态、后续页 `Top3` 不并入；③ 翻页终止条件（`List` 空 / 达 `PageCount` / 达 `GuardMaxPages`）；④ 页间延迟次数 = 页数 - 1（覆写 `DelayBetweenPagesAsync` 计数，对齐 `CountingGuardJob` 模式）；⑤ 单页 `CrawlBackoffActiveException` 返回 `null`；⑥ 单页其他异常返回 `null` | `tests/Z.Vtuber.Tests/LiveGuardListFetcherTests.cs`（新） | 编译通过、对未抽取的实现运行失败（红；fetcher 类型尚不存在时以编译错误形态呈现，可先注释占位或直接随 2.1 同提交窗口转绿） |
| 1.2 | 重构 `LiveGuardJobTests`：Job 构造改注入 fetcher fake（固定返回名单或 `null`），删除已迁移的翻页细节用例，保留编排语义——`Run_BeforeDailyHour_DoesNotCrawl`、pending 差集、快照落库字段、fetcher 返回 `null` 不落库、凌晨补抓路径 | `tests/Z.Vtuber.Tests/LiveGuardJobTests.cs` | 适配后编译通过；语义等价于现状 |
| 1.3 | 新建 `LiveGuardTop3BackfillPlannerTests`：`SelectTop10`——① MedalLevel 主序倒序；② 同级按 GuardLevel 倒序；③ 再同级按 Uid 升序兜底（重跑不抖动）；④ 不足 10 人取全部；⑤ 恰好/超过 10 人截断。`ComputeMissing`——⑥ top10 全部缺失；⑦ 部分缺失按 top10 顺序返回；⑧ 全部已存在返回空 | `tests/Z.Vtuber.Tests/LiveGuardTop3BackfillPlannerTests.cs`（新） | 对未实现的 planner 运行失败（红） |
| 1.4 | `LiveGuardRepositoryTests` 增补集成用例：① `GetDistinctMidsAsync` 多 mid 多日去重返回；② `GetSnapshotsByMidAsync` 仅返回该 mid、`Date < beforeDateExclusive`（排除当日）、按 Date 升序；③ `BackfillMissingEntriesAsync` 追加缺失条目、`GuardCount` 同步、既有条目字段不变（只增不改）、其他 mid/日期文档不受影响；④ 重跑同参数 `missing` 为空语义由服务层保证，仓储层再验证「追加同一批条目两次」不产生重复由服务层 uid 过滤负责（本层不重复约束） | `tests/Z.Vtuber.Tests/LiveGuardRepositoryTests.cs` | 对未实现的方法编译失败/运行失败（红） |
| 1.5 | 新建 `LiveGuardTop3BackfillServiceTests`（mongo fixture + `FakeBiliApiClient` + `FakeCookiePoolService`）：① 端到端——造不含 top3 的历史快照（两日），重抓名单含 top3，执行后两日快照均补齐 top10 缺失用户、`GuardCount` 一致、既有条目未改；② 重抓结果不写入当日快照（当日文档不存在/不被创建）；③ 抓取失败（provider 抛异常）——该 mid 跳过、不写 meta 标记、不影响后续 mid；④ 成功后写入 `live_guard_top3_backfill_v1:{mid}` 标记；⑤ 重跑已标记 mid 不再发起抓取（provider 调用计数为 0）；⑥ 未标记 mid 重跑幂等（第二遍 `missing` 为空、无写、结果一致）；⑦ roomId 缺失且补抓后仍缺——跳过不写标记；⑧ 无可用 cookie——整轮退出记日志 | `tests/Z.Vtuber.Tests/LiveGuardTop3BackfillServiceTests.cs`（新） | 对未实现的服务运行失败（红） |

### Phase 2: 实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | 抽取 `LiveGuardListFetcher`：`FetchAllPagesAsync`（`LiveGuardJob.cs:179-225`）、`DelayBetweenPagesAsync`（:227-231，保留 `internal virtual`）、`ToEntry`（:233-240）原样迁入，依赖 `IBiliApiClient` / `BilibiliCrawlerOptions` / `ILogger<LiveGuardListFetcher>`，公开 `Task<List<LiveGuardEntry>?> FetchAllAsync(string cookie, long mid, long roomId, CancellationToken ct)`；`LiveGuardJob` 删除三处私有实现、构造改注入 fetcher 并改调 `FetchAllAsync`（:160）；`Program.cs` 注册 `AddSingleton<LiveGuardListFetcher>()`。**纯搬移，不改任何分支逻辑** | `servers/crawler/Services/LiveGuardListFetcher.cs`（新）、`servers/crawler/Jobs/LiveGuardJob.cs`、`servers/crawler/Program.cs` | 1.1/1.2 全部用例转绿；既有 Job 行为零变化 |
| 2.2 | `LiveGuardRepository` 新增三方法（签名按提案 3.2）：`GetDistinctMidsAsync`（`Distinct` mid）；`GetSnapshotsByMidAsync`（`Mid` eq + `Date` lt `beforeDateExclusive`，`SortBy Date`）；`BackfillMissingEntriesAsync`（`(Mid, Date)` filter，`$set Guards = 既有 + missing` 与 `GuardCount`，`missing` 为空时不发起写——由调用方或服务层短路，二者取其一并在代码注释锁定） | `servers/common/Persistence/LiveGuardRepository.cs` | 1.4 全部用例转绿；既有仓储用例无回归 |
| 2.3 | ① `LiveGuardTop3BackfillPlanner` 纯函数：`SelectTop10` = `OrderByDescending(MedalLevel).ThenByDescending(GuardLevel).ThenBy(Uid).Take(10)`；`ComputeMissing` 按快照 uid 集合过滤、保持 top10 顺序。② `LiveGuardTop3BackfillService.ExecuteAsync(ct)`：`GetDistinctMidsAsync` → 过滤 meta 已标记 → 逐 mid：解析 roomId（同 Job 路径）→ `FetchAllAsync`（失败/退避记日志跳过不写标记）→ `SelectTop10` → `GetSnapshotsByMidAsync(mid, 今日北京日期)` → 逐快照 `ComputeMissing`，非空调 `BackfillMissingEntriesAsync` → upsert `live_guard_top3_backfill_v1:{mid}` → 结束输出汇总日志（处理/跳过/失败/补入条数）；注入 `TimeProvider` 计算北京当日。③ `Program.cs`：注册服务，启动块之后在 `app.Run()` 前以 fire-and-forget 任务调用（`_ = Task.Run` + try/catch 记日志），**不进入同步 await 块**，不阻塞 `/health` | `servers/crawler/Services/LiveGuardTop3BackfillPlanner.cs`（新）、`servers/crawler/Services/LiveGuardTop3BackfillService.cs`（新）、`servers/crawler/Program.cs` | 1.3/1.5 全部用例转绿 |

### Phase 3: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 3.1 | `dotnet test tests/Z.Vtuber.Tests` 全绿（含既有回归）；top10 选取、差异计算、补写、进度标记核心逻辑覆盖率 ≥ 80% | 全部通过 |
| 3.2 | 编排手工验证（z-aspire-orchestrator）：本地 Aspire 拉起，MongoDB 造数（某 mid 两日历史快照、名单不含 top3 头部用户）→ 启动 crawler → 日志确认逐 mid 处理与补入条数、汇总输出 → 抽查历史快照已补齐、既有条目未改、当日快照未生成/未触碰 → 重启 crawler 确认已标记 mid 幂等跳过（无重复抓取日志） | 手工验证记录 |
| 3.3 | AGENTS.md 同步：`servers/crawler/AGENTS.md` 的 `Services/` 模块清单补 `LiveGuardListFetcher` / `LiveGuardTop3BackfillService`（含 planner）、`Jobs/` 行注明翻页逻辑已抽取至 fetcher、`Program.cs` 行补启动一次性补录；`servers/common/AGENTS.md` 的 `Persistence/` 行补 `LiveGuardRepository` 补录方法 | 文档与实现一致 |

## 4. 并行策略

- Phase 1（测试）可按文件并行：1.1+1.2（crawler 抓取语义）、1.3（纯函数）、1.4（仓储集成）、1.5（服务编排）四组互不依赖，可拆 subagent 并行编写。
- Phase 2 中 2.1（fetcher 抽取）串行先行——2.3 的补录服务依赖 `LiveGuardListFetcher` 类型；2.1 完成后 2.2（common 仓储）与 2.3（crawler planner/service/wiring）分属不同项目文件，可并行。
- Phase 3 串行收口。测试与实现分开提交（`test:` 先行、`refactor:`/`feat:` 随后），全部提交在需求总分支 `feat/live-guard-top3-backfill`。

## 5. 验收对照

| PRD 功能点 | 实现任务 | 验证方式 |
|-----------|---------|---------|
| 功能点 1：补录源生成（按 mid 去重、每主播一次重抓含 top3、不落库、粉丝牌倒序 top10、不足 10 人取全部） | 1.1、1.3①-⑤、1.5②、2.1、2.3 | FetcherTests / PlannerTests / ServiceTests + 编排验证（3.2） |
| 功能点 2：历史快照逐天补齐（缺失补入、uid 单日不重复、只增不改、GuardCount 一致、当日快照不处理） | 1.3⑥-⑧、1.4、1.5①②、2.2、2.3 | PlannerTests / RepositoryTests / ServiceTests + MongoDB 抽查（3.2） |
| 功能点 3：断点续跑与幂等（已标记跳过、重跑一致、单 mid 失败不阻断） | 1.5③-⑦、2.3 | ServiceTests + 重启验证（3.2） |
| 非功能：抓取纪律一致（每 mid 一轮、页间延迟/退避不变、不提高频率） | 1.1④-⑥、2.1 | FetcherTests + 既有 Job 用例回归（3.1） |
| 非功能：正常抓取链路无回归 | 1.2、2.1 | `LiveGuardJobTests` 全绿（3.1） |
| 非功能：核心逻辑覆盖率 ≥ 80%、全量测试绿 | 3.1 | `dotnet test` |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| 重构行为漂移 | fetcher 抽取若改动分支逻辑会污染每日抓取链路 | 纯搬移 + `refactor:` 单独提交；1.1 迁移用例先行锁定语义（2.1） |
| 补录阻塞启动 | 补录含网络抓取，同步 await 会拖延 `/health` 与 HostedService | fire-and-forget + 异常捕获记日志（2.3③）；1.5 覆盖服务自身异常路径 |
| 与正常抓取写冲突 | 补录与 `LiveGuardJob` 并发写同一文档 | 补录仅写 `Date < 今日`，Job 仅写当日，集合不相交（提案 5.2 决策）；单文档更新原子，无需事务 |
| 风控退避常态化 | 退避期重抓必失败 | 失败 mid 不写标记、记日志、下轮启动自动重试（1.5③） |
| mid 数量大 | 启动后补录耗时长 | 每 mid 仅一轮抓取、量级同单日正常抓取；异步执行不影响服务可用性（提案 5.2） |
| 依赖 | Mikibot.Crawler 1.0.9 `GuardInfo.Top3`；既有抓取链路（cookie 池/退避/roomId 解析） | 已核实（1.0）；升级抓取包时复查字段 |

## 7. 相关文档

- PRD：`docs/product/draft/20260904-live-guard-top3-backfill-prd.md`（v1.0）
- 技术提案：`docs/engineering/proposals/20260904-proposal-live-guard-top3-backfill.md`（v1.0）
- 前置 Plan：`docs/engineering/plans/20260903-plan-live-guard-top3.md`（舰长 Top3 落库）、`docs/engineering/plans/20260831-plan-live-guard-crawl.md`（舰长数据抓取）
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`
