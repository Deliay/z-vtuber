---
description: 录播列表接口获取数据方式更新实现 Plan——基于 PRD v1.0 与技术提案（feat/recording-list-info）的实施阶段划分与任务分解；契约先行（recording/bilibili-crawler 两份 OpenAPI 的 RecordingFile 增加 info 与 RecordingSessionInfo schema），common RecordingRepository 新增 ListByRoomAsync，recording RecordingController.ListRecordings 改为场次归档聚合（磁盘缺失跳过、孤儿文件不列出、录制中排除口径不变、场次 StartedAt 倒序场内 name 升序），api 代理纯透传无需改动，web 经 codegen 适配并展示场次标题
type: Permanent
---

# 录播列表接口获取数据方式更新 - 实现 Plan

**功能名称**: 录播列表接口获取数据方式更新
**关联 PRD**: `docs/product/draft/20260917-recording-list-info-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20260917-proposal-recording-list-info.md`
**Plan 版本**: v1.0
**创建日期**: 2026-09-17
**作者**: Workflow Agent（代起草）
**feat-branch**: `feat/recording-list-info`（需求总分支，已推送 origin；需求总 PR：<https://github.com/Deliay/z-vtuber/pull/111>）

## 1. 概述

本 Plan 基于 PRD v1.0 与技术提案编写，指导「录播列表接口获取数据方式更新」的编码实施。改动分四块：① 契约——`contracts/recording.openapi.yaml` 的 `RecordingFile` 追加 `info` 字段并新增 `RecordingSessionInfo` schema，`contracts/bilibili-crawler.openapi.yaml` 同步（先于实现）；② common——`RecordingRepository` 新增 `ListByRoomAsync(roomId)` 按房间全量查询（StartedAt 倒序，复用既有索引）；③ recording——`RecordingController.ListRecordings` 由磁盘枚举改为场次归档聚合，逐文件 `FileInfo` 补物理属性，元素新增 `info`；④ web——codegen 重新生成类型、录制状态页文件行展示所属场次标题。api 代理纯 JSON 透传，**无改动**。实施全程遵循 `docs/engineering/common-rules.md`（TDD、契约式开发、核心逻辑覆盖率 ≥ 80%）、`backend-rules.md`、`frontend-rules.md` 及各子项目 AGENTS.md。

### 1.0 上游核实结论（代码现状复查）

对需求总分支 `feat/recording-list-info`（基于 main，含已合入的录播与转写链路）代码核实，提案技术路径**整体可行**，现状细节如下：

- **控制器依赖注入点确认**：`RecordingController`（`servers/recording/Controllers/RecordingController.cs:11-14`）现注入 `RecordingStatusRepository`/`AudioStreamHub`/`RecordingOptions`，需追加 `RecordingRepository`；`Program.cs:33` 已 `AddSingleton<RecordingRepository>()`，无需新增 DI 注册。`ListRecordings`（:24-68）已为 `async Task<IActionResult>`，改造不改签名形态。
- **录制中排除设施现成**：`GetRecordingFilesAsync`（:143-157，state=recording 且心跳未超 `StaleStatusThresholdSeconds` → `Files` 集合）直接复用，与下载 400 判定（:87-91）共用同一集合，口径不变。
- **物理属性读取先例现成**：`FileInfo.Length` + `CreationTimeUtc` 回退 `LastWriteTimeUtc` + 逐文件 try/catch（`IOException`/`UnauthorizedAccessException`）跳过（:44-59），聚合循环中沿用同一容错逻辑；新增「文件不存在跳过」分支（`FileInfo.Exists` 判空）。
- **仓储方法落点确认**：`RecordingRepository`（`servers/common/Persistence/RecordingRepository.cs`）已有 `ListAsync(roomId, limit, before)`（:58-75，转写分页用，签名不动）；新增 `ListByRoomAsync` 走既有 `RoomId_1_StartedAt_-1` 索引，无新增索引。实体 `RecordingRecord`（`servers/common/Persistence/Entities/RecordingRecord.cs`）字段齐备：`Id`(ObjectId)/`RoomId`/`Mid`/`Title`/`Files`/`StartedAt`/`EndedAt`(nullable)。
- **契约锚点确认**：`contracts/recording.openapi.yaml` 列表端点 `$ref` 于 :60、`RecordingFileList` schema :192、`RecordingFile` schema :199；`contracts/bilibili-crawler.openapi.yaml` 列表 `$ref` 于 :297、`RecordingFileList` :1150。
- **web 锚点确认**：`listRecordings`（`apps/web/src/api/client.ts:137-142`）`return data.files ?? []` 形态不变，仅 codegen 类型变化；`RecordingStatusPage.tsx` 的 `recordingsByRoom`（:54）与 `renderFileList`（:111-112，行内已渲染 `file.name` + `formatBytes(file.size)` :129）为适配点。
- **测试设施现状**：`tests/Z.Vtuber.Tests/RecordingApiTests.cs`（:17，`MongoDbFixture` + `IAsyncLifetime`，直接构造控制器）已有列表用例（:80/:91/:115/:131）——因数据源语义变化，**既有列表用例需改造**（磁盘文件不再自动列出，须先种子 `recordings` 集合），控制器构造处同步注入 `RecordingRepository`；`BilibiliLiveRecordingControllerTests` 透传用例不回归即可，无需新增；web 侧 `client.test.ts`（:576-590 listRecordings 用例）与 `RecordingStatusPage.test.tsx`（:9 mock）同步改 mock 结构。

### 1.1 实施范围

- **做**：
  - 契约：两份 OpenAPI 的 `RecordingFile` 追加 `info`（required）+ 新增 `RecordingSessionInfo` schema（`recordingId`/`title`/`mid`/`startedAt`/`endedAt`，其中 `endedAt` nullable 且 required）；两份契约的列表 `200` 描述同步更新为「按场次归档聚合」口径。
  - common：`RecordingRepository` 新增 `ListByRoomAsync(long roomId, CancellationToken)`（`Find(RoomId == roomId).SortByDescending(StartedAt)`，无分页）。
  - recording：`ListRecordings` 改为场次聚合——`ListByRoomAsync` → 遍历各场次 `Files`（场内 name 升序）→ 跳过录制中集合/同名已见/磁盘缺失/读取失败 → 组装 `{ name, size, createdAt, info }`；「无目录提前返回」分支移除，无场次记录即 `{files:[]}`。
  - web：`npm run codegen` 重新生成 `schema.d.ts`/`schema-recording.d.ts`；`RecordingStatusPage` 文件行追加场次标题（`file.info.title`）展示。
  - 测试：recording 接口测试改造 + 新增聚合/排序/跳过/排除/info 用例；web 两个测试文件适配；核心逻辑覆盖率 ≥ 80%。
- **不做**：录制流程本身（ffmpeg、锁、状态上报、归档写入语义）；下载接口与音频流接口；api 代理层（纯透传）；转写服务既有 `ListAsync` 分页路径；列表分页；磁盘孤儿文件的重新归档；`info` 扩展字段（如时长、转写状态）。

### 1.2 现状基线

- **聚合排序口径**：`records` 按 `StartedAt` 倒序遍历、每场 `Files` 按 `StringComparer.Ordinal` 升序——天然实现「场次倒序、场内 name 升序」，无需后置全局排序。
- **同名去重兜底**：`HashSet<string>(StringComparer.Ordinal)` 按遍历序「先见先得」，与提案 §2.3 一致。
- **info 映射**：`recordingId = record.Id.ToString()`、`title/mid/startedAt/endedAt` 直取实体字段；响应沿用匿名类型风格（与现状 `new { name, size, createdAt }` 一致），`info` 内嵌匿名对象。
- **错误响应/透传先例**：api 列表代理 `Content(json, "application/json")` 纯透传不变；下载接口全链路不动。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash）与 Conventional Commits：

- 本需求开发全程只使用需求总分支 `feat/recording-list-info`：契约、实现、测试、文档全部直接在该分支提交，不切开发子分支（沿用既定做法）。
- 需求总分支已推送 origin，需求总 PR 已存在（#111），提交照常推送更新分支。
- 提交信息使用 Conventional Commits：契约 `feat:`、测试先行 `test:`、实现 `feat:`、文档 `docs:`；测试与实现分开提交。
- 改动面小（一份仓储 + 一个控制器 + 两份契约 + 两个前端文件 + 三个测试文件），后端与前端可分 subagent 并行，契约归入主串行线先行（见第 4 节）。

## 3. 实施阶段

按「契约先行 → TDD 红-绿 → 收口验证」推进。Phase 0 串行先行；Phase 1 后端测试（1.1）与前端测试（1.2）可并行；Phase 2 后端（2.1/2.2）与前端（2.3）分属不同子项目可并行；Phase 3 串行收口。

### Phase 0: 契约先行

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | `contracts/recording.openapi.yaml`：① 新增 `RecordingSessionInfo` schema（`recordingId: string`、`title: string`、`mid: integer/int64`、`startedAt: string/date-time`、`endedAt: string/date-time nullable`，五者均 required，字段描述同提案 §3.1）；② `RecordingFile`（:199）追加 `info: $ref RecordingSessionInfo` 并列入 required；③ `GET /api/{roomId}/recordings` 的 `200` 描述更新为「录播文件列表（按场次归档聚合，含文件元信息与所属场次信息；不含录制中文件；无录播返回空数组）」 | `contracts/recording.openapi.yaml` | `npx openapi-typescript` 可成功生成类型 |
| 0.2 | `contracts/bilibili-crawler.openapi.yaml`：① `RecordingFileList`（:1150）关联 schema 同步 0.1①②（`RecordingSessionInfo` 与 `RecordingFile.info` 追加，位置对齐该文件既有 components 布局）；② `GET /api/b/live/{roomId}/recordings` 的 `200` 描述同步更新；`502`/下载端点语义不变 | `contracts/bilibili-crawler.openapi.yaml` | codegen 通过 |

### Phase 1: 测试先行（红）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | `RecordingApiTests` 改造与增补：① **既有用例适配**——`ListRecordings_*`（:80/:91/:115/:131）数据源改为场次归档：预置 `RecordingRecord`（种子 `recordings` 集合）+ 对应磁盘文件后断言；控制器构造追加 `RecordingRepository`（fixture Mongo 库现成）；「无目录返回空」用例改为「无场次记录返回空」；② **聚合与 info**——预置两场次（各含 RAW/AV1 两文件）→ 每元素 `name/size/createdAt/info` 正确，`info.recordingId/title/mid/startedAt/endedAt` 与所属场次一致、同场次两文件 `info` 相同；③ **排序**——场次 StartedAt 倒序、场内 name 升序；④ **跳过**——归档文件磁盘缺失不出现、磁盘孤儿文件（无记录）不出现；⑤ **排除**——录制中（心跳新鲜）文件不在列表、心跳过期出现、`EndedAt=null` 无心跳孤儿场次文件出现且 `info.endedAt` 为 null；⑥ **回归**——下载接口全部场景（200/206/416/400/404）不动 | `tests/Z.Vtuber.Tests/RecordingApiTests.cs` | 红（新增/改造用例失败，下载回归不破坏） |
| 1.2 | web 测试适配（可与 1.1 并行）：① `client.test.ts` `listRecordings` 用例（:576-590）mock 元素补 `info` 对象并断言透传；② `RecordingStatusPage.test.tsx` mock `listRecordings` 返回含 `info` 的对象数组，断言场次标题渲染（文件名、格式化大小、下载 href 断言不变） | `apps/web/src/api/client.test.ts`、`apps/web/src/pages/recording/RecordingStatusPage.test.tsx` | `npm run test` 红 |

### Phase 2: 实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | common 仓储方法：`RecordingRepository` 新增 `ListByRoomAsync(long roomId, CancellationToken ct = default)`——`Find(r => r.RoomId == roomId).SortByDescending(r => r.StartedAt).ToListAsync(ct)`，XML 注释注明「按 StartedAt 倒序，供录播文件列表聚合」 | `servers/common/Persistence/RecordingRepository.cs` | 编译通过（随 2.2 用例转绿） |
| 2.2 | recording 列表聚合改造：① 构造函数追加 `RecordingRepository recordings`；② `ListRecordings` 重写为 §2.3 口径——`ListByRoomAsync` → `GetRecordingFilesAsync`（复用）→ 双重循环（场次倒序、场内 `Files` 按 `StringComparer.Ordinal` 升序）→ `seen` 去重、录制中跳过、`FileInfo.Exists` 缺失跳过、物理属性读取沿用 `CreationTimeUtc` 回退 `LastWriteTimeUtc` 与逐文件 try/catch → 组装 `{ name, size, createdAt, info = new { recordingId, title, mid, startedAt, endedAt } }`；③ 移除「目录不存在提前返回」分支（无记录即空数组，目录存在与否不再影响响应形状）；`DownloadRecording`/`StreamAudio`/状态端点不动 | `servers/recording/Controllers/RecordingController.cs` | 1.1 转绿；下载回归无破坏 |
| 2.3 | web 适配（仅依赖 Phase 0 契约，与 2.1/2.2 并行）：① `npm run codegen` 重新生成 `schema.d.ts`/`schema-recording.d.ts`（勿手改）；② `client.ts` `listRecordings` 类型随 codegen 更新（`return data.files ?? []` 不动）；③ `RecordingStatusPage.tsx`：`renderFileList`（:111 起）文件行追加所属场次标题展示（`file.info.title`，与现有内联风格一致；开播/结束时间按页面空间决定，不强制） | `apps/web/src/api/client.ts`、`apps/web/src/pages/recording/RecordingStatusPage.tsx`、codegen 产物 | 1.2 转绿；`npm run lint`/`npm run build` 通过 |

### Phase 3: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 3.1 | `dotnet test tests/Z.Vtuber.Tests` 全绿（含下载/代理回归）；recording 列表聚合逻辑覆盖率 ≥ 80%；`npm run test`/`lint`/`build` 全绿；grep 确认两份契约的 `RecordingSessionInfo`/`info` 一致、web 侧无残留「无 info 元素」假设 | 全部通过 |
| 3.2 | 编排手工验证（z-aspire-orchestrator）：本地 Aspire 拉起 → 录制一场直播结束后列表出现该场次文件且含 `info`（标题/起止时间正确）；录制中房间列表不含当前场次文件；Mongo 手动删除某文件磁盘副本后该文件从列表消失；录制状态页展示场次标题 | 手工验证记录 |
| 3.3 | AGENTS.md 同步：`servers/recording/AGENTS.md`（API 面列表行改「按场次归档聚合，含 info；不含录制中文件」、Controllers 模块描述同步）；`servers/common/AGENTS.md`（`RecordingRepository` 描述补 `ListByRoomAsync`）；`apps/web/AGENTS.md`（录制状态页描述补「文件列表展示所属场次标题」）；api 代理无改动，`servers/api/AGENTS.md` 不动 | 文档与实现一致 |

## 4. 并行策略

- Phase 0（契约 0.1/0.2）串行先行——recording 控制器与前端 codegen 均依赖契约。
- Phase 1：后端测试（1.1）与前端测试（1.2）互不依赖，可拆 subagent 并行。
- Phase 2：2.1/2.2（common/recording，同一闭环）与 2.3（web）分属不同子项目，按 z-coding 规范拆 subagent 并行，各 subagent 负责「实现 → 提交 → 测试修复 → 提交」闭环；2.1 与 2.2 归同一 subagent（仓储方法与控制器强耦合，且与 1.1 测试文件存在写交集——同一文件「测试 + 实现」归同一 subagent 闭环）。
- Phase 3 串行收口。全部提交在需求总分支 `feat/recording-list-info`。

## 5. 验收对照

| PRD 功能点 | 实现任务 | 验证方式 |
|-----------|---------|---------|
| 功能点 1：列表数据源切换为场次归档（聚合/跳过/排序） | 0.1、0.2、2.1、2.2 | 1.1①②③④ + 手工验证（3.2） |
| 功能点 2：文件扩展所属场次信息（info） | 0.1①②、0.2①、2.2②、2.3③ | 1.1②、1.2② + 手工验证（3.2） |
| 功能点 3：录制中文件排除语义不变 | 2.2②（复用 `GetRecordingFilesAsync`） | 1.1⑤⑥ |
| 功能点 4：web 录制状态页适配 | 0.2、2.3 | 1.2 + 手工验证（3.2） |
| 非功能：核心逻辑覆盖率 ≥ 80%、全量测试绿 | 3.1 | `dotnet test` / `npm run test` |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| 归档完整性依赖 | 列表完整性依赖「每场录制产出文件均已归档」 | 开播即归档 + 存量清洗迁移已覆盖（提案 §6.2），无需新增保障 |
| 磁盘孤儿文件从列表消失 | 未归档文件不再可见（破坏性变更） | PRD 3.5 已声明接受；1.1④ 锁定该语义 |
| 同名文件出现在多条记录 | 极端情况归属歧义 | `seen` 去重按 StartedAt 倒序先见先得（2.2②）；文件名由场次开播时间渲染天然唯一，`RoomId+StartedAt` 索引兜底 |
| 文件查询期间被删除/属性读取失败 | `FileInfo` 访问抛 IO 异常 | `Exists` 判空 + 逐文件 try/catch 跳过（沿用现状容错），1.1④ 覆盖 |
| 列表接口破坏性变更 | `files` 元素新增 `info` 且排序规则调整，消费方仅本仓库 web | web 适配随本需求同分支交付（2.3），统一部署天然同版本（提案 §9） |
| 既有列表测试语义翻转 | 磁盘文件不再自动列出 | 1.1① 统一改造种子数据（先写 `recordings` 记录再置磁盘文件） |
| 依赖 | 无新增 NuGet/npm 依赖；`RecordingRepository` 已在 recording 注册（1.0 核实）；复用 `RoomId_1_StartedAt_-1` 索引 | 已核实 |

## 7. 相关文档

- PRD：`docs/product/draft/20260917-recording-list-info-prd.md`（v1.0）
- 技术提案：`docs/engineering/proposals/20260917-proposal-recording-list-info.md`
- 契约：`contracts/recording.openapi.yaml`、`contracts/bilibili-crawler.openapi.yaml`
- 列表元信息 Plan：`docs/engineering/plans/20260914-plan-recording-file-list-metadata.md`
- 录播转写 Plan：`docs/engineering/plans/20260916-plan-recording-transcript.md`
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`docs/engineering/frontend-rules.md`、`servers/recording/AGENTS.md`、`servers/common/AGENTS.md`、`servers/api/AGENTS.md`、`apps/web/AGENTS.md`
