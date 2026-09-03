---
description: 舰长 Top3 落库实现 Plan——基于 PRD v1.1 与技术提案 v1.1 的实施阶段划分、任务分解与验收对照；LiveGuardJob 首页 Top3 按 uid 去重打平并入 guard_list，实体/仓储/契约零变更，TDD 单文件改动收口
type: Permanent
---

# 舰长 Top3 落库 - 实现 Plan

**功能名称**: 舰长 Top3 落库
**关联 PRD**: `docs/product/draft/20260903-live-guard-top3-prd.md`（v1.1）
**关联技术提案**: `docs/engineering/proposals/20260903-proposal-live-guard-top3.md`（v1.1）
**Plan 版本**: v1.0
**创建日期**: 2026-09-03
**作者**: Deliay
**feat-branch**: `feat/live-guard-top3`（需求总分支）

## 1. 概述

本 Plan 基于 PRD v1.1 与技术提案 v1.1 编写，指导舰长 Top3 落库的编码实施。改动收敛在 `LiveGuardJob.FetchAllPagesAsync` 一个循环分支内：首页响应的 `GuardInfo.Top3` 逐条 `TryAdd` 进既有合并字典，按 uid 天然去重，随快照覆盖写落库。实体、仓储、契约、索引、配置**零变更**。实施全程遵循 `docs/engineering/common-rules.md`（TDD、核心逻辑覆盖率 ≥ 80%）与 `backend-rules.md`。

### 1.0 上游核实结论（Mikibot 反编译复查）

对 `Mikibot.Crawler` 1.0.9（`~/.nuget/packages/mikibot.crawler/1.0.9/lib/net10.0/Mikibot.Crawler.dll`，ilspycmd 反编译）核实，提案技术路径**可行**：

- `GuardInfo` 为 struct：`[JsonPropertyName("top3")] public List<GuardUserInfo> Top3 { get; set; }`，与 `List` 条目同为 `GuardUserInfo`，提案所述「同构」成立，可直接复用现有 `ToEntry(GuardUserInfo)` 转换。
- `GuardInfo` 为 struct 且 `Top3` 为引用类型属性：当接口响应缺失 `top3` 字段时反序列化结果为 **null**（非空列表），实现时必须做 null/空防护（提案 5.2 已要求，实现与测试按 null 用例锁定）。
- **前一 Plan 假设作废**：`docs/engineering/plans/20260831-plan-live-guard-crawl.md` 1.0 曾以「Top3 仅首页返回且与 List 头部重复」为由不入库；本需求（PRD 功能点 1）明确 top3 可能含已下船（不在分页名单中）的用户，**必须照常并入**。实现时以本 Plan 与 PRD v1.1 为准，不再沿用旧假设。

### 1.1 实施范围

- **做**：
  - `LiveGuardJob.FetchAllPagesAsync`：`page == 1` 响应处理中，除 `info.List` 外将 `info.Top3` 逐条 `TryAdd` 进同一 `Dictionary<long, LiveGuardEntry>` 合并字典（uid 天然去重；`Top3` 为 null/空时跳过，不视为失败；后续页忽略）。
  - 测试：`LiveGuardJobTests` 增补 top3 用例；`LiveGuardRepositoryTests` 增补含并入 top3 名单的 upsert 覆盖用例。
- **不做**：实体/仓储/契约/索引/配置任何变更；top3 单独字段或来源/排名标记；对外查询 API；历史快照回填；抓取请求、调度、风控、补抓逻辑改动。

### 1.2 现状基线

- **合并逻辑**（`servers/crawler/Jobs/LiveGuardJob.cs:179-217`）：`FetchAllPagesAsync` 现有 `Dictionary<long, LiveGuardEntry> merged`，每页 `info.List` 逐条 `TryAdd`（:202-205），翻页终止条件「`info.List.Count == 0` 或 `page >= info.Info.PageCount` 或 `page >= GuardMaxPages`」（:207-210）。top3 并入点位于 `page == 1` 分支、`info.List` 合并之后。
- **转换函数**（`LiveGuardJob.cs:225-232`）：`ToEntry(GuardUserInfo)` 私有静态，top3 条目直接复用，零改动。
- **失败语义**（`LiveGuardJob.cs:186-200`）：`CrawlBackoffActiveException` 记信息返回 `null`、其他异常记错误返回 `null`，`null` = 整轮不落库；top3 并入在该结构内，不影响失败路径。
- **快照落库**（`LiveGuardJob.cs:166-176`）：`GuardCount = entries.Count` 自动随合并去重后的总人数，无需额外处理。
- **仓储**（`servers/common/Persistence/LiveGuardRepository.cs`）：`UpsertDailySnapshotAsync` 为 `{Mid, Date}` filter + `ReplaceOne(IsUpsert = true)` 整体替换，同日重复抓取天然整体覆盖，零改动。
- **测试设施**（`tests/Z.Vtuber.Tests/`）：`LiveGuardJobTests.cs`（mongo fixture，`CountingGuardJob` 注入 `FakeTimeProvider`/`FakeBiliApiClient`，`RoomGuardListProvider` 按 page 返回 `GuardInfo`，:35-60、:93-121）；`FakeBiliApiClient` 在 `Fakes.cs`，`GuardInfo` 为 struct 直接构造 `Top3` 属性即可，Fake 零改动；`LiveGuardRepositoryTests.cs` 为 Testcontainers 集成测试范本。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash）与 Conventional Commits：

- 本需求开发全程只使用需求总分支 `feat/live-guard-top3`：实现、测试、文档全部直接在该分支提交，不切开发子分支（沿用近两期需求的既定做法）。
- 需求总分支已推送 origin（最新提交 `dd51715`）；需求总 PR 因 PAT 权限不足未创建，后续可手动创建（`https://github.com/Deliay/z-vtuber/pull/new/feat/live-guard-top3`，base: main），提交照常推送更新分支。
- 提交信息使用 Conventional Commits（`test:` / `feat:` / `docs:`），测试先行与实现分开提交。

## 3. 实施阶段

改动极小（单文件单分支），按 TDD 红-绿-重构串行推进，不分并行阶段。

### Phase 1: 测试先行（红）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | `LiveGuardJobTests` 增补 top3 用例（构造 `GuardInfo.Top3` 即可，Fake 零改动）：① 首页 top3 并入 guard_list——分页名单 2 人 + top3 中 1 名已下船用户，落库 3 人且字段映射正确（复用既有断言风格，对齐 `Run_AfterDailyHour_SnapshotsPendingMid` :149-178）；② top3 与分页名单重叠 uid 去重——同一 uid 同时出现在 `List` 与 `Top3`，只保留一条；③ `Top3` 为 null 与空列表两种形态——仅落分页名单，正常落库不视为失败；④ `GuardCount` = 合并去重后总人数；⑤ 多页场景后续页 `Top3`（即使构造返回）不并入——top3 仅首页捕获 | `tests/Z.Vtuber.Tests/LiveGuardJobTests.cs` | 新用例编译通过、对未修改的实现运行失败（红） |
| 1.2 | `LiveGuardRepositoryTests` 增补集成用例：含并入 top3 条目（含已下船用户）的快照 upsert 落库字段完整；同日重复 upsert 整体覆盖、guard_list 无增量残留、仍为一行 | `tests/Z.Vtuber.Tests/LiveGuardRepositoryTests.cs` | 用例运行通过（仓储行为未变，本用例锁定语义防回归） |

### Phase 2: 实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | `FetchAllPagesAsync` 在 `page == 1` 响应处理中追加 top3 合并：`info.Top3` 非 null 时逐条 `merged.TryAdd(user.Uid, ToEntry(user))`（位于 `info.List` 合并之后、`break` 判断之前；`TryAdd` 保留先到条目，两者同构无语义差异）；后续页不处理 `Top3` | `servers/crawler/Jobs/LiveGuardJob.cs:202-210` | Phase 1 全部用例转绿；既有 `LiveGuardJobTests` 用例无回归 |

### Phase 3: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 3.1 | `dotnet test tests/Z.Vtuber.Tests` 全绿（含既有回归）；top3 捕获/去重/失败路径覆盖率 ≥ 80% | 全部通过 |
| 3.2 | 编排手工验证（z-aspire-orchestrator）：本地 Aspire 拉起后订阅真实主播 guard 类型，触发抓取（或临时调低 `GuardDailyCrawlHour`），在 MongoDB 确认 guard_list 含接口返回的 top3 用户（含已下船者）且无重复 uid | 手工验证记录 |
| 3.3 | AGENTS.md 检查：本需求无新模块/集合/配置/结构变化，确认 `servers/crawler/AGENTS.md`、`servers/common/AGENTS.md` 无需更新（如实现中出现偏差则同步更新） | 文档一致 |

## 4. 并行策略

- 本需求为单文件单分支改动，不拆分 subagent 并行：Phase 1（测试）→ Phase 2（实现）→ Phase 3（收口）串行推进。
- 测试与实现分开提交（`test:` 先行、`feat:` 随后），实现分支即需求总分支 `feat/live-guard-top3`。

## 5. 验收对照

| PRD 功能点 | 实现任务 | 验证方式 |
|-----------|---------|---------|
| 功能点 1：top3 打平并入舰长名单（含已下船并入、uid 去重、GuardCount 口径、空 top3 正常落库） | 1.1①-④、2.1 | LiveGuardJobTests 新增用例 + 编排验证（3.2） |
| 功能点 2：同日重复抓取覆盖更新（整体替换、失败不写半成品） | 1.2、2.1（失败路径零改动） | LiveGuardRepositoryTests 集成用例 + 既有 `Crawl_SinglePageFailure_DoesNotUpsert` 等回归 |
| 功能点 3：历史数据处理（不修改、不回填、读取兼容） | 无代码任务（实体结构不变） | 既有测试全绿（3.1） |
| 非功能：不新增抓取请求、抓取链路无回归 | 2.1（仅合并逻辑扩展） | 既有 Job 用例（翻页/延迟/退避/页数上限）全绿 |
| 非功能：核心逻辑覆盖率 ≥ 80%、全量测试绿 | 3.1 | `dotnet test` |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| 依赖 | Mikibot.Crawler 1.0.9 `GuardInfo.Top3: List<GuardUserInfo>` | 已反编译核实（1.0）；升级该包时复查字段 |
| `Top3` 反序列化为 null | struct 内引用属性，响应缺 `top3` 字段时为 null 而非空列表 | 实现 null 防护（2.1）；测试覆盖 null 与空两种形态（1.1③） |
| 翻页期榜单抖动 | top3 为实时榜单，翻页过程中可能变化 | 仅首页捕获（提案决策），测试锁定后续页 Top3 不并入（1.1⑤） |
| 旧 Plan 假设残留 | 20260831 Plan 1.0「Top3 与 List 头部重复不入库」结论已作废 | 本 Plan 1.0 明确作废，实现以 PRD v1.1 为准 |

## 7. 相关文档

- PRD：`docs/product/draft/20260903-live-guard-top3-prd.md`（v1.1）
- 技术提案：`docs/engineering/proposals/20260903-proposal-live-guard-top3.md`（v1.1）
- 前置 Plan：`docs/engineering/plans/20260831-plan-live-guard-crawl.md`（舰长数据抓取）
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`
