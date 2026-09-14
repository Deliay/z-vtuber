---
description: 录播文件列表接口提供更多信息实现 Plan——基于 PRD v1.0 与技术提案（feat/recording-file-list-metadata）的实施阶段划分与任务分解；契约先行（recording/bilibili-crawler 两份 OpenAPI 的 RecordingFile 对象化与下载 400），recording RecordingController 列表返回 name/size/createdAt 元信息并排除录制中文件、下载录制中文件返回 400，api 代理新增 400 透传分支（顺带修复非法文件名 400 被吞为 502），web 经 codegen 适配并展示文件大小
type: Permanent
---

# 录播文件列表接口提供更多信息 - 实现 Plan

**功能名称**: 录播文件列表接口提供更多信息
**关联 PRD**: `docs/product/draft/20260914-recording-file-list-metadata-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20260914-proposal-recording-file-list-metadata.md`
**Plan 版本**: v1.0
**创建日期**: 2026-09-14
**作者**: Workflow Agent（代起草）
**feat-branch**: `feat/recording-file-list-metadata`（需求总分支，已推送 origin；需求总 PR：<https://github.com/Deliay/z-vtuber/pull/99>）

## 1. 概述

本 Plan 基于 PRD v1.0 与技术提案编写，指导「录播文件列表接口提供更多信息」的编码实施。改动分四块：① 契约——`contracts/recording.openapi.yaml` 新增 `RecordingFile` schema、列表对象化、下载追加 400 语义，`contracts/bilibili-crawler.openapi.yaml` 同步（先于实现）；② recording——`RecordingController` 列表返回 `{ files: [{ name, size, createdAt }] }` 并按 `recording_status` 口径排除录制中文件，下载对录制中文件返回 400；③ api——`BilibiliLiveRecordingController.DownloadRecording` 新增 400 透传分支（顺带修复非法文件名 400 被改写为 502 的现状偏差）；④ web——codegen 重新生成类型、`listRecordings` 返回 `RecordingFile[]`、录制状态页历史录播列表展示文件大小。实施全程遵循 `docs/engineering/common-rules.md`（TDD、契约式开发、核心逻辑覆盖率 ≥ 80%）、`backend-rules.md`、`frontend-rules.md` 及各子项目 AGENTS.md。

### 1.0 上游核实结论（代码现状复查）

对需求总分支 `feat/recording-file-list-metadata`（基于 main，含已合入的 `feat/live-recording`）代码核实，提案技术路径**整体可行**，现状细节如下：

- **recording 控制器依赖已齐备**：`RecordingController`（`servers/recording/Controllers/RecordingController.cs:11-14`）构造函数已注入 `RecordingStatusRepository statusRepository` 与 `RecordingOptions options`——「录制中」判定无需新增 DI 注册；`RecordingStatusRepository.GetByRoomIdAsync`（`servers/common/Persistence/RecordingStatusRepository.cs:25-26`）单房间读取现成；`RecordingOptions.StaleStatusThresholdSeconds`（`servers/recording/Configuration/RecordingOptions.cs:31`，默认 180）现成。
- **列表方法需同步转异步**：`ListRecordings`（:24-40）现为同步 `IActionResult`，引入 `GetByRoomIdAsync` 后改为 `async Task<IActionResult>`；`DownloadRecording`（:42-61）同理。路由签名、路径穿越防护（`IsSafeFileName` :101-113 + 解析后前缀校验 :54）保持不变。
- **下载 400 判定插入点确认**：`DownloadRecording` 现状顺序为「`IsSafeFileName` 400 → 路径解析+存在性 404 → `PhysicalFile` 200」。录制中判定须置于安全校验之后、存在性判断之前（录制中文件物理存在，不拦截会直接 200）；非法文件名 400 优先级最高不变（提案 §5.2 实现点 2）。
- **api 代理确认**：`BilibiliLiveRecordingController.DownloadRecording`（:148-158）仅特判 404，其余非成功一律 502——在 :150-158 区间新增 `BadRequest` 分支读取上游响应体原样透传；列表代理（:90-124）纯 JSON 透传新结构，**无需改动**；音频代理不受影响。
- **web 页面有两处文件列表，仅一处受影响**：`RecordingStatusPage.tsx` 的 `renderFileList`（:91-112）渲染 `listRecordings` 返回的历史录播（`recordingsByRoom`，:82-83）——本次适配对象；状态行的「文件名」列（:196-211）渲染的是 `listRecordingStatus` 的 `files`（recording-status 聚合接口，语义为「当前场次产出文件名」）——按提案 §1.3/§5.3 维持 string 数组**不变**。注意：录制中点该列下载链接将命中新 400，属需求既定行为，页面无需额外处理。
- **契约锚点确认**：`contracts/recording.openapi.yaml` 列表端点 :48-60、`RecordingFileList` schema :135；`contracts/bilibili-crawler.openapi.yaml` `RecordingFileList` :775（提案已标注）。
- **测试设施现状**：`tests/Z.Vtuber.Tests` 已有 `RecordingApiTests`（录播接口测试）与 `BilibiliLiveRecordingControllerTests`（fake HttpClient 设施），本次在既有文件上增补用例，无需新增测试设施；web 侧 `client.test.ts`（:560 附近 listRecordings 用例）与 `RecordingStatusPage.test.tsx`（:9-11 mock）同步改 mock 结构。

### 1.1 实施范围

- **做**：
  - 契约：`contracts/recording.openapi.yaml` 新增 `RecordingFile` schema + `RecordingFileList.files` items 对象化 + 下载端点 400 语义扩展；`contracts/bilibili-crawler.openapi.yaml` 同步对象化 + 下载追加 400 响应 + 列表 200 描述更新。
  - recording：`ListRecordings` 返回 `name/size/createdAt` 元信息（`FileInfo`，`CreationTimeUtc` 回退 `LastWriteTimeUtc`）并排除录制中文件；`DownloadRecording` 录制中文件 400 `{ "error": "file is still recording and cannot be downloaded" }`；新增「录制中文件集合」判定（state=recording 且心跳未过期 → `Files`，否则空集合）。
  - api：`DownloadRecording` 新增 400 透传分支。
  - web：codegen 重新生成 `schema.d.ts`/`schema-recording.d.ts`；`client.ts` `listRecordings` 返回类型改为契约生成的 `RecordingFile[]`；`RecordingStatusPage` 历史录播列表渲染 `file.name` + 格式化大小（`formatBytes` 风格内联小工具）。
  - 测试：后端两个测试文件增补用例 + web 两个测试文件适配，核心逻辑覆盖率 ≥ 80%，无回归。
- **不做**：录制流程本身（ffmpeg 输出、锁、状态上报语义）；`recording-status` 聚合接口 `files` 字段对象化（维持 string 数组，另立需求）；录制中文件的页面徽标；`createdAt` 的页面强制展示（PRD 3.4 不强制，按页面空间决定）；自动清理/生命周期管理。

### 1.2 现状基线

- **「录制中」判定口径先例**：api 读侧 `BilibiliLiveRecordingController.ListRecordingStatus`（:66-71，`StaleRecordingThreshold` 3 分钟）与 recording 写侧 `LiveStatusPoller`（:156，`StaleStatusThresholdSeconds`）均为「recording 且 UpdatedAt 超阈值 → 视为非录制中」——本次 recording 侧判定与 `LiveStatusPoller` 同源（同 Options 阈值）。
- **错误响应形态先例**：`DownloadRecording` 既有 `BadRequest(new { error = "invalid fileName" })`（:47）——新 400 同构 `{ error }`。
- **api 透传先例**：列表代理的 `Content(json, "application/json")`（:122）——下载 400 透传复用同模式。
- **前端先例**：`listRecordings`（`apps/web/src/api/client.ts:126-131`）`return data.files ?? []` 形态不变，仅泛型类型变化。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash）与 Conventional Commits：

- 本需求开发全程只使用需求总分支 `feat/recording-file-list-metadata`：契约、实现、测试、文档全部直接在该分支提交，不切开发子分支（沿用既定做法）。
- 需求总分支已推送 origin，需求总 PR 已存在（#99），提交照常推送更新分支。
- 提交信息使用 Conventional Commits：契约 `feat:`、测试先行 `test:`、实现 `feat:`、文档 `docs:`；测试与实现分开提交。
- 改动面小（两个控制器 + 两份契约 + 两个前端文件 + 四个测试文件），后端与前端可分 subagent 并行，契约归入主串行线先行（见第 4 节）。

## 3. 实施阶段

按「契约先行 → TDD 红-绿 → 收口验证」推进。Phase 0 串行先行；Phase 1 后端测试（1.1/1.2）与前端测试（1.3）可并行；Phase 2 后端（2.1/2.2）与前端（2.3）分属不同子项目可并行；Phase 3 串行收口。

### Phase 0: 契约先行

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | `contracts/recording.openapi.yaml`：① 新增 `RecordingFile` schema（`name: string`、`size: integer/int64`、`createdAt: string/date-time`，三者 required，字段描述同提案 §3.1）；② `RecordingFileList.files`（:135 起）items 由 `type: string` 改为 `$ref: '#/components/schemas/RecordingFile'`；③ `GET /api/{roomId}/recordings/{fileName}`（:61 起）400 响应描述扩展为「fileName 非法或文件正在录制中无法下载」，响应体 `application/json` `{ "error": string }` | `contracts/recording.openapi.yaml` | `npx openapi-typescript` 可成功生成类型 |
| 0.2 | `contracts/bilibili-crawler.openapi.yaml`：① `RecordingFileList`（:775）同步 0.1② 对象化（含 `RecordingFile` schema 追加，位置对齐该文件既有 components 布局）；② `GET /api/b/live/{roomId}/recordings/{fileName}` 追加 `400` 响应（fileName 非法或文件正在录制中，`{ "error": string }`）；③ `GET /api/b/live/{roomId}/recordings` 的 `200` 描述更新为「录播文件列表（含大小与创建时间；不含录制中文件）」；`200`/`404`/`502` 语义不变 | `contracts/bilibili-crawler.openapi.yaml` | codegen 通过 |

### Phase 1: 测试先行（红）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | `RecordingApiTests` 增补：① 列表元信息——预置多文件 → 每元素 `name/size/createdAt` 正确（size 与磁盘字节数一致、createdAt 合法 ISO 8601）、按 name 升序；② 排除录制中——预置状态记录（`state=recording`、心跳新鲜、`Files` 含某文件）→ 该文件不在列表，其余历史文件在列；③ 状态过期——`UpdatedAt` 超 `StaleStatusThresholdSeconds` → 文件重新出现在列表；④ 下载录制中——命中 `Files` → 400 且 error 语义为「正在录制中无法下载」；⑤ 状态转 `not_live` 后同文件 → 200；⑥ 回归——非法文件名 400、不存在 404、无目录 `{files:[]}`、上一场次已完成文件与当前场次并存时仅排除当前场次 | `tests/Z.Vtuber.Tests/RecordingApiTests.cs` | 红（新增用例失败，既有用例不回归） |
| 1.2 | `BilibiliLiveRecordingControllerTests` 增补：① 下载上游 400 → 代理返回 400 且响应体原样透传（录制中与非法文件名两种 error 文案均透传）；② 回归——上游 404 → 404、上游 500/不可达 → 502、上游 200 → 流式透传 | `tests/Z.Vtuber.Tests/BilibiliLiveRecordingControllerTests.cs` | 红 |
| 1.3 | web 测试适配（可与 1.1/1.2 并行）：① `client.test.ts` `listRecordings` 用例 mock 改为 `{ files: [{ name, size, createdAt }] }` 并断言返回对象数组；② `RecordingStatusPage.test.tsx` mock `listRecordings` 返回对象数组，断言文件名渲染、格式化大小展示（如 `1.2 GB`）、下载链接 href 使用 `file.name` | `apps/web/src/api/client.test.ts`、`apps/web/src/pages/recording/RecordingStatusPage.test.tsx` | `npm run test` 红 |

### Phase 2: 实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | recording 列表/下载改造：① 新增「录制中文件集合」判定（私有方法或纯函数，`GetByRoomIdAsync` → `state=="recording"` 且 `UtcNow - UpdatedAt <= StaleStatusThresholdSeconds` → `record.Files` 的 `HashSet<string>`（`StringComparer.Ordinal`），否则空集合）；② `ListRecordings` 改 `async Task<IActionResult>`：`EnumerateFiles(*.flv)` → `FileInfo` 取 `Length` 与 `CreationTimeUtc`（退化值回退 `LastWriteTimeUtc`），逐文件 try/catch 跳过枚举瞬间被删除的文件，排除录制中集合后按 name 升序返回 `{ files: [{ name, size, createdAt }] }`（空目录仍 `{files:[]}`）；③ `DownloadRecording` 改 async，`IsSafeFileName` 校验之后、存在性判断之前插入录制中判定 → `BadRequest(new { error = "file is still recording and cannot be downloaded" })` | `servers/recording/Controllers/RecordingController.cs` | 1.1 转绿；既有测试无回归 |
| 2.2 | api 代理 400 透传：`BilibiliLiveRecordingController.DownloadRecording` 在 404 特判旁新增 `BadRequest` 分支——读取上游响应体以 400 原样返回（`Content(json, "application/json")`，与列表透传同模式）；其余非成功仍归并 502；列表/音频代理不动 | `servers/api/Controllers/BilibiliLiveRecordingController.cs` | 1.2 转绿 |
| 2.3 | web 适配（仅依赖 Phase 0 契约，与 2.1/2.2 并行）：① `npm run codegen` 重新生成 `schema.d.ts`/`schema-recording.d.ts`（勿手改）；② `client.ts` `listRecordings` 返回类型 `string[]` → 契约生成的 `RecordingFile[]`（`return data.files ?? []` 不变）；③ `RecordingStatusPage.tsx`：`recordingsByRoom` 状态类型同步；`renderFileList`（:91-112）渲染 `file.name` + 格式化大小（新增 `formatBytes` 内联小工具，与现有内联工具同风格）；下载链接 `recordingDownloadUrl(roomId, file.name)`；状态行「文件名」列（:196-211）**不改** | `apps/web/src/api/client.ts`、`apps/web/src/pages/recording/RecordingStatusPage.tsx`、codegen 产物 | 1.3 转绿；`npm run lint`/`npm run build` 通过 |

### Phase 3: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 3.1 | `dotnet test tests/Z.Vtuber.Tests` 全绿（含既有回归）；recording 列表/下载判定逻辑覆盖率 ≥ 80%；`npm run test`/`lint`/`build` 全绿；grep 确认两份契约的 `RecordingFile`/`files` items 一致、无残留 `files: string[]` 假设（web 侧除 recording-status 外） | 全部通过 |
| 3.2 | 编排手工验证（z-aspire-orchestrator）：本地 Aspire 拉起 → 录制状态页历史录播列表展示文件名与大小；预置房间目录含文件 + Mongo `recording_status` 写入 recording 新鲜心跳（`Files` 含该文件）→ 列表不含该文件、直接下载返回 400 且文案正确；状态置 `not_live` 后文件出现且可 200 下载 | 手工验证记录 |
| 3.3 | AGENTS.md 同步：`servers/recording/AGENTS.md`（API 面列表行改「含大小与创建时间，不含录制中文件」、下载行补「录制中 400」、Controllers 模块描述同步）；`servers/api/AGENTS.md`（`BilibiliLiveRecordingController` 描述补「下载录制中文件/非法文件名 400 透传」）；`apps/web/AGENTS.md`（录制状态页描述补「历史录播列表展示文件大小」） | 文档与实现一致 |

## 4. 并行策略

- Phase 0（契约 0.1/0.2）串行先行——recording 控制器、api 代理、前端 codegen 均依赖契约。
- Phase 1：后端测试（1.1/1.2）与前端测试（1.3）互不依赖，可拆 subagent 并行。
- Phase 2：2.1/2.2（后端 recording/api）与 2.3（web）分属不同子项目，按 z-coding 规范拆 subagent 并行，各 subagent 负责「实现 → 提交 → 测试修复 → 提交」闭环；2.1 与 2.2 文件无交集可同批，但与 1.1/1.2 的测试文件存在写交集——同一文件「测试 + 实现」归同一 subagent 闭环，避免并行冲突。
- Phase 3 串行收口。全部提交在需求总分支 `feat/recording-file-list-metadata`。

## 5. 验收对照

| PRD 功能点 | 实现任务 | 验证方式 |
|-----------|---------|---------|
| 功能点 1：列表返回文件元信息（name/size/createdAt） | 0.1②、0.2①、2.1②、2.3②③ | 1.1①、1.3 + 手工验证（3.2） |
| 功能点 2：录制中文件不出现在列表 | 2.1①② | 1.1②③⑥ + 手工验证（3.2） |
| 功能点 3：录制中文件下载返回 400 | 0.1③、0.2②、2.1③、2.2 | 1.1④⑤⑥、1.2 + 手工验证（3.2） |
| 功能点 4：web 录制状态页适配（大小展示） | 0.2①、2.3 | 1.3 + 手工验证（3.2） |
| 非功能：核心逻辑覆盖率 ≥ 80%、全量测试绿 | 3.1 | `dotnet test` / `npm run test` |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| 状态过期窗口内「提前解禁」 | 心跳残留 ≤180s 内录制中文件判定失效，文件提前出现在列表/可下载 | 与 recording-status 读侧退化口径一致，PRD 3.5 已明确接受；1.1③ 锁定该语义 |
| 文件枚举瞬间被删除 | `FileInfo.Length` 访问抛 `FileNotFoundException` | 逐文件 try/catch 跳过（2.1②），1.1 覆盖 |
| `CreationTimeUtc` 不可用 | 部分 Linux 文件系统不记录 birth time | 回退 `LastWriteTimeUtc`（2.1②），字段始终有值 |
| 列表接口破坏性变更 | `files` 元素 string → object，消费方仅本仓库 web | web 适配随本需求同分支交付（2.3），统一部署天然同版本（提案 §9） |
| 代理 400 透传回归面 | 新增分支触碰既有 404/502 归并逻辑 | 1.2② 锁定 404/502/200 回归 |
| 依赖 | 无新增 NuGet/npm 依赖；复用 `RecordingStatusRepository`/`RecordingOptions`（已在控制器注入，1.0 核实） | 已核实 |

## 7. 相关文档

- PRD：`docs/product/draft/20260914-recording-file-list-metadata-prd.md`（v1.0）
- 技术提案：`docs/engineering/proposals/20260914-proposal-recording-file-list-metadata.md`
- 契约：`contracts/recording.openapi.yaml`、`contracts/bilibili-crawler.openapi.yaml`
- 录播功能 Plan：`docs/engineering/plans/20260913-plan-live-recording.md`
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`docs/engineering/frontend-rules.md`、`servers/recording/AGENTS.md`、`servers/api/AGENTS.md`、`servers/common/AGENTS.md`、`apps/web/AGENTS.md`
