---
description: 训练「音频数据集」导出 ZIP 实现 Plan：training 服务 TrainingDatasetsController 新增 GET /api/training/datasets/{datasetId}/export 流式 zip 端点（ZipArchive 直写 Response.Body，逐条复用 IRecordingAudioClient.GetClipAsync 取音频，000001 起 D6 连续编号 mp3/txt 成对写入，失败/降级 wav 跳过不占编号），api 网关 TrainingProxyController 加镜像 action（ForwardAsync 零额外处理），web 数据集页加「下载数据集」按钮（a[href download] + 条目数回调禁用口径），契约先行两份 + codegen；TDD 补导出内容/命名/跳过/404/透传用例，覆盖率 ≥ 80%
type: Permanent
---

# 训练 - 数据集导出 ZIP - 实现 Plan

**功能名称**: 训练 - 数据集导出 ZIP（数据集流式打包 zip 下载）
**关联 PRD**: `docs/product/draft/20261007-training-dataset-export-zip-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20261007-proposal-training-dataset-export-zip.md`（v1.0）
**Plan 版本**: v1.0
**创建日期**: 2026-10-07
**作者**: Workflow Agent（代起草）
**需求总分支**: `prd/training-dataset-export-zip`（需求总 PR #177）
**feat-branch**: `feat/training-dataset-export-zip`（实现分支，基于需求总分支切出）

## 1. 概述

依据 PRD 与技术提案落地四件事：① `servers/training` 的 `TrainingDatasetsController` 新增 `GET /api/training/datasets/{datasetId}/export`：数据集 404 校验后先写响应头（200 / `application/zip` / `Content-Disposition: attachment; filename*=UTF-8''{数据集名}.zip` / `Cache-Control: no-store`），再以 BCL `ZipArchive`（`ZipArchiveMode.Create`，`leaveOpen: true`）直写 `Response.Body`，按条目列表（`CreatedAt`、`AudioKey` 升序）逐条 `IRecordingAudioClient.GetClipAsync` 取音频，成功条目从 `000001` 起 `{D6}.mp3` + `{D6}.txt`（UTF-8 无 BOM）成对写入并 `FlushAsync`，音频不可用/降级非 mp3 的条目跳过不占编号，全程尊重 `RequestAborted`；② `servers/api` 的 `TrainingProxyController` 增加镜像 action，复用 `ForwardAsync`（已流式透传 `Content-Disposition`）零额外处理；③ 契约先行（`contracts/training.openapi.yaml` + `contracts/bilibili-crawler.openapi.yaml` 镜像）+ `npm run codegen`；④ `apps/web` 数据集页「音频数据集」Tab 顶部操作区新增「下载数据集」按钮（`<Typography.Link href download>` 模式；未选定数据集或数据集为空禁用，条目数经 `DatasetItemList` 回调上提）。实施遵循 `docs/engineering/common-rules.md`（契约先行、TDD、覆盖率 ≥ 80%、单用例 ≤ 5s）、`backend-rules.md`、`frontend-rules.md`、z-coding 流程与相关模块 `AGENTS.md`（`servers/training`、`servers/api`、`servers/common`、`apps/web`）。

### 1.0 现状核实结论（读码确认，均在需求总分支 @ `a96c279` 复核）

- **导出端点落点**：`servers/training/Controllers/TrainingDatasetsController.cs`——`[Route("api/training/datasets")]`（L20），现有 List/Create/Rename/Delete（接口 1-4），构造函数 `(TrainingDatasetRepository datasets, TrainingDatasetItemRepository items, ILogger? logger = null, VectorSyncService? vectors = null, SegmentSyncService? segments = null)`；**尚无 `IRecordingAudioClient` 注入**，export action 需新增构造参数（提案 §5.2 实现点 1 裁定放本控制器，`[HttpGet("{datasetId}/export")]` 即完整路径 `/api/training/datasets/{datasetId}/export`）。
- **条目顺序口径**：`TrainingDatasetItemsController.List`（`servers/training/Controllers/TrainingDatasetItemsController.cs:35-47`）→ `items.ListAsync(id, audioType, source, ct)`（`CreatedAt`、`AudioKey` 升序），导出直接复用 `TrainingDatasetItemRepository.ListAsync(datasetId, null, null)` 全量口径，与页面列表展示顺序一致；条目 DTO 含 `audioUri/audioKey/startMs/endMs/text`（`servers/training/Contracts/Dtos.cs:23-36`）。
- **取音频封装即试听同源**：`IRecordingAudioClient.GetClipAsync(audioUri, startMs, endMs?, ct)` → `AudioClip(Bytes, ContentType)`（`servers/training/Clients/IRecordingAudioClient.cs`）；`endMs` 空按 `Ai:AudioClipDefaultDurationMs`（默认 300000）补齐（`RecordingAudioClient.cs:57`）；uri 空/不可解析抛 `ArgumentException`、取数失败抛 `AudioUnavailableException`、超 `Ai:MaxAudioBytes`（默认 8 MiB）抛 `AudioTooLargeException`；上游无 Content-Type 时兜底 `audio/mpeg`（`RecordingAudioClient.cs:105`）。
- **recording 片段端点两个既有约束（零改动）**：`servers/recording/Controllers/RecordingController.cs:130-179`——区间 ≤ `Recording:AudioClipMaxDurationMs`（300000，L149 硬校验）；缺 mp3 编码时降级 `audio/wav`（L164 `extractor.ContentType`）——`ContentType != "audio/mpeg"` 的条目按「音频不可用」跳过（提案 §6.1 决策 5）。
- **api 代理已具备流式 + Content-Disposition 透传**：`servers/api/Controllers/TrainingProxyController.cs`（`[Route("api")]`）`ForwardAsync`（L148-209）：query 原样透传、`HttpCompletionOption.ResponseHeadersRead`（L167）、状态码/`ContentType`/`Cache-Control`/`Content-Disposition`（L181-193）透传、`CopyToAsync(Response.Body)` 流式回写（L206）、不可达/超时 502 `{"error":"training service unavailable"}`；30 个镜像 action 统一 `Uri.EscapeDataString` 拼路径；api 侧 `TrainingService:RequestTimeoutSeconds` 默认 180s，`ResponseHeadersRead` 下仅约束首字节（响应头先行 → 首字节即时产出，总时长不受限）。二进制透传测试先例：`tests/Z.Vtuber.Tests/TrainingProxyControllerTests.cs` `GenerateTrainingImage_UpstreamImageResponse_Passthrough`（image/png 字节 + Content-Type 断言）。
- **web 现状（含提案表述修正）**：`apps/web/src/pages/training/DatasetsPage.tsx:25-56` audioTab 顶部 `<Space>` 内为「从 Transcript 导入」按钮，且整块 `{selectedId && (...)}` 条件渲染；**条目列表状态在 `DatasetItemList` 内部**（`DatasetItemList.tsx:133` `useState<TrainingDatasetItem[]>`、L148-157 `refresh` 调 `listTrainingDatasetItems`），`DatasetsPage` 并不持有 `items`——提案 §5.2 实现点 4「条目列表已在页面状态内，直接取 `items.length`」不成立，须以回调上提条目数（见 §1.1 口径 6）；`DatasetItemListProps`（L32-39）已有 `onChanged?: () => void` 回调先例可循。
- **下载链接先例**：`apps/web/src/pages/transcripts/TaskListTab.tsx:478-504`——`<Typography.Link href={...} download>`，禁用态 `aria-disabled` + 置灰样式 + `preventDefault`（Tooltip `trigger={['hover','focus']}` 对键盘可达）；URL 生成器惯例 `trainingAudioClipUrl`（`apps/web/src/api/client.ts:882`，`encodeURIComponent` 拼接）、`transcriptSubtitleUrl`。
- **契约现状**：`contracts/training.openapi.yaml` 已有 `/api/training/datasets`（L20）等 training 全量端点，二进制响应先例 `POST /api/training/images/generate` 200 `image/png: schema {type: string, format: binary}`（L829-858）；api 镜像契约 `contracts/bilibili-crawler.openapi.yaml:827+` 同路径逐端点镜像（含 502 `training service unavailable` 口径）。
- **测试设施可直接扩展**：`tests/Z.Vtuber.Tests/TrainingDatasetItemsApiTests.cs:20-38`（`IClassFixture<MongoDbFixture>` + 直构控制器 + `DefaultHttpContext` + `Response.Body = MemoryStream`）；`IRecordingAudioClient` 桩先例 `StubAudioClient`（`TrainingSynthesizeFakes.cs:223`）、`EmotionTaskStubAudioClient`/`RecognizeStubAudioClient`；`TrainingDatasetsApiTests.cs:30` 直构 `TrainingDatasetsController(Datasets, Items, NullLogger...)`——构造签名变更后需补桩。
- **零新增依赖成立**（提案 ⑤ 复核）：全仓（`servers/`+`tests/`）无 zip 代码/依赖引用，`System.IO.Compression.ZipArchive` 为 BCL 内置。
- **codegen 与跑测口径**：`apps/web` `npm run codegen` 从 7 份契约生成 `src/api/schema*.d.ts`（勿手改，复核 0 diff）；跑测走 `dotnet test --logger trx` + `tests/Z.Vtuber.TestReport --check`（单用例 ≤ 5s，超时例外登记 `TimeoutExceptions.json`）。

### 1.1 口径澄清（规划期裁定，随实现评审）

1. **端点落点与构造签名**：export action 放 `TrainingDatasetsController`（`[HttpGet("{datasetId}/export")]`），构造函数新增**必填** `IRecordingAudioClient audio`（置于 `items` 之后、可选参数之前）；`TrainingDatasetsApiTests` 构造点补 no-op 桩（编译与既有用例零回退），不走「可选参数 + 运行时判空」。
2. **顺序与编号口径**：条目按 `ListAsync(datasetId, null, null)`（`CreatedAt`、`AudioKey` 升序 = 页面列表顺序）；**成功条目**从 `000001` 起 `{n:D6}.mp3` + `{n:D6}.txt` 连续编号、`n` 仅在写入成功后递增；失败/降级条目跳过不占编号（提案 §6.1 决策 4/5）。
3. **「音频不可用」判定**：`GetClipAsync` 抛任意异常（`ArgumentException`/`AudioUnavailableException`/`AudioTooLargeException` 等）或返回 `ContentType != "audio/mpeg"`（含 recording wav 降级）→ `LogWarning("training dataset export skip: datasetId={DatasetId} audioKey={AudioKey} reason={Reason}")` 并 `continue`，不中断整体下载。
4. **响应头先行 + 流式写入**：`ObjectId.TryParse` 失败或数据集不存在 → 404 `{"error": "dataset not found"}`；随后先写响应头（200、`application/zip`、`Content-Disposition: attachment; filename*=UTF-8''{Uri.EscapeDataString(name)}.zip` 兼容非 ASCII 数据集名、`Cache-Control: no-store`），再 `using var zip = new ZipArchive(Response.Body, ZipArchiveMode.Create, leaveOpen: true)`；txt 用 `new UTF8Encoding(false)`（无 BOM）；单条写入后 `await Response.Body.FlushAsync(ct)` 保证即时下行；全程尊重 `HttpContext.RequestAborted`（中断即停、服务端无残留）；收尾 Info 日志 `training dataset export: datasetId={} exported={} skipped={}`。
5. **空数据集行为**：web 侧按钮禁用（PRD 功能点 1）；直接调 API 仍返回 200 空 zip（仅中央目录），服务端不额外 4xx（契约简单、语义为「导出结果为空」）。
6. **web 禁用口径落地（纠正提案表述）**：条目数不在 `DatasetsPage` 状态内——`DatasetItemListProps` 新增 `onItemsLoaded?: (count: number) => void`（`refresh` 成功后回调，与既有 `onChanged` 同风格），`DatasetsPage` 持 `itemCount` state；「下载数据集」按钮**始终渲染**（PRD 验收「未选定数据集时按钮不可用」= 可见但禁用），禁用条件 `!selectedId || itemCount === 0`；「从 Transcript 导入」维持「未选中数据集不显示」现状不动。
7. **按钮形态与「进行中」口径**：沿提案 §5.2 实现点 4 评审裁定——`<Typography.Link href={trainingDatasetExportUrl(id)} download>`（浏览器接管下载），页面不维护下载状态机、不做「进行中」按钮态（PRD 功能点 3 的「进行中不可重复触发」由浏览器下载管理天然满足）；禁用态 `aria-disabled` + 置灰 + `preventDefault`（同 `TaskListTab` 先例）。此为提案相对 PRD 验收措辞的既定偏差，已经人工评审通过，本 Plan 沿用。
8. **代理零额外处理**：镜像 action `[HttpGet("training/datasets/{datasetId}/export")]` → `ForwardAsync(HttpMethod.Get, $"api/training/datasets/{Uri.EscapeDataString(datasetId)}/export", ct)`；GET 不带 body，`Content-Disposition`/流式/502 口径均现成。
9. **契约先行 + codegen**：先改两份契约再改实现（common-rules）；`npm run codegen` 生成物勿手改、复核 0 diff。
10. **零新增依赖与配置**：zip 用 BCL；前端无新 npm 包；无 DB 变更、无新配置、无流水线调整。

### 1.2 实施范围

- **做**：`contracts/`（training + bilibili-crawler 两份契约 + web codegen 生成物）；`servers/training`（`TrainingDatasetsController` export action + `IRecordingAudioClient` 构造注入）；`servers/api`（`TrainingProxyController` 镜像 action）；`apps/web`（`trainingDatasetExportUrl` + 「下载数据集」按钮 + `DatasetItemList` 条目数回调）；`tests/Z.Vtuber.Tests`（`TrainingDatasetExportApiTests.cs` 新增、`TrainingProxyControllerTests.cs` 增补、`TrainingDatasetsApiTests.cs` 构造补桩）与 `apps/web` Vitest（`client.test.ts`/`DatasetsPage.test.tsx` 增补）；文档同步（`servers/training/AGENTS.md`、`servers/api/AGENTS.md`、`apps/web/AGENTS.md`、PRD 验收勾选、提案/Plan 互链）。
- **不做**：recording 片段端点改动（5 分钟上限与 wav 降级口径不变）；条目数据结构变更（无迁移）；情感标注等 `extensions` 加工信息导出；断点续传/导出历史/异步导出任务；web 下载状态管理；txt 富格式（时间轴等）；新增 NuGet/npm 依赖、配置项、流水线。

## 2. 分支与提交流程

- 需求总分支 `prd/training-dataset-export-zip`（基于 origin/main 切出，PRD 与技术提案已随 `a96c279` 提交，需求总 PR #177）：本 Plan 提交到该分支随 PR 评审。
- 实现分支 `feat/training-dataset-export-zip`（基于需求总分支切出，提案 §0 口径）：实现按 z-coding「编写实现」流程推进（子项目为单位委托 subagent、TDD 红-绿、测试与实现分开提交），实现 PR 回需求总分支评审。
- 提交顺序（Conventional Commits）：
  1. `docs(training):` 本实现 Plan（需求总分支）；
  2. `feat(contracts):` 契约变更（training + api 镜像）+ `npm run codegen`；
  3. `feat(training):` 导出端点（`TrainingDatasetsController` export action + 构造注入）+ `test(training):` 配套导出用例；
  4. `feat(api):` 镜像 action + `test(api):` 透传用例（zip 字节/Content-Disposition/404/502）；
  5. `feat(web):` 按钮 + URL 生成器 + 条目数回调 + 组件测试；
  6. `docs(training):` `AGENTS.md`（training/api/web）/ PRD 验收勾选 / 文档互链同步。

## 3. 实施阶段

### Phase 0: 技术文档（先行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | PRD（上游节点产出） | `docs/product/draft/20261007-training-dataset-export-zip-prd.md` | 已完成（`a96c279`） |
| 0.2 | 技术提案（上游节点产出） | `docs/engineering/proposals/20261007-proposal-training-dataset-export-zip.md` | 已完成（`a96c279`） |
| 0.3 | 实现 Plan（本文档） | `docs/engineering/plans/20261007-plan-training-dataset-export-zip.md` | 任务/文件/验收明确，与提案口径一致（§1.1 偏差已标注） |

### Phase 1: 契约先行

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | 新增 `GET /api/training/datasets/{datasetId}/export`（operationId `exportTrainingDataset`）：path 参数 `datasetId`；200 `content: application/zip: schema {type: string, format: binary}`，描述含 `Content-Disposition: attachment; filename*=UTF-8''{数据集名}.zip`、`Cache-Control: no-store`、流式无 Content-Length、zip 内容约定（`CreatedAt`/`AudioKey` 升序、`{D6}.mp3`+`{D6}.txt` 成对、失败跳过不占编号）；404 `$ref ErrorResponse` `{"error": "dataset not found"}` | `contracts/training.openapi.yaml` | 契约自洽，错误口径与提案 §3.1 逐字一致 |
| 1.2 | 镜像端点同步（同既有 training 镜像写法；补充 502 `{"error": "training service unavailable"}` 口径与透传语义说明） | `contracts/bilibili-crawler.openapi.yaml` | 与 1.1 一致 |
| 1.3 | `npm run codegen` 再生成 `schema.d.ts`/`schema-training.d.ts`（勿手改） | `apps/web/src/api/` | 生成物与契约一致 |

### Phase 2: training 导出端点（TDD：测试先行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | 导出用例先行（红）：`TrainingDatasetExportApiTests`（`MongoDbFixture` + 直构 `TrainingDatasetsController` + `StubRecordingAudioClient`（按 audioUri/startMs/endMs 记录入参，可按条目返回 `AudioClip(bytes,"audio/mpeg")`/`AudioClip(bytes,"audio/wav")`/抛异常）+ `Response.Body = MemoryStream` 后以 `ZipArchive` 读回断言） | `tests/Z.Vtuber.Tests/TrainingDatasetExportApiTests.cs` | 用例先行落地并失败 |
| 2.2 | `TrainingDatasetsController` 构造函数新增必填 `IRecordingAudioClient audio`（`items` 后、可选参数前）；`TrainingDatasetsApiTests` 构造点补桩 | `servers/training/Controllers/TrainingDatasetsController.cs`、`tests/Z.Vtuber.Tests/TrainingDatasetsApiTests.cs` | 编译通过、既有用例不回退 |
| 2.3 | export action：`ObjectId.TryParse`/数据集不存在 → 404 → 写响应头（200/`application/zip`/`Content-Disposition: attachment; filename*=UTF-8''{Uri.EscapeDataString(name)}.zip`/`Cache-Control: no-store`）→ `ZipArchive(Response.Body, Create, leaveOpen: true)` → 逐条 `GetClipAsync`（try/catch 含 `ArgumentException`；失败或 `ContentType != "audio/mpeg"` → Warn + 跳过不占编号）→ 成功写 `{n:D6}.mp3`（字节）+ `{n:D6}.txt`（`new UTF8Encoding(false)`）→ `n++` + `FlushAsync(ct)` → 收尾 Info 日志；全程尊重 `RequestAborted` | `servers/training/Controllers/TrainingDatasetsController.cs` | 2.1 用例转绿 |
| 2.4 | 关键用例矩阵（提案 §8.2）：① 多条成功 → 连续编号/顺序与列表一致/mp3 字节与 stub 一致/txt UTF-8 无 BOM 内容一致；② 失败跳过（抛异常 / 返回 `audio/wav`）→ 无文件、后续编号连续、无跳号；③ 全失败 → 空包（仅中央目录）仍 200；④ datasetId 非法/不存在 → 404；⑤ 中文/特殊字符数据集名 → `filename*=UTF-8''` 正确编码；⑥ `endMs=null` 条目照常导出（stub 断言收到 null） | `tests/Z.Vtuber.Tests/TrainingDatasetExportApiTests.cs` | 全绿，覆盖 §1.1 口径 2-5 |

### Phase 3: api 代理镜像

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 3.1 | 新增 `[HttpGet("training/datasets/{datasetId}/export")]` action → `ForwardAsync(HttpMethod.Get, $"api/training/datasets/{Uri.EscapeDataString(datasetId)}/export", ct)`（`ForwardAsync` 已透传 Content-Disposition 与流式 body，零额外处理） | `servers/api/Controllers/TrainingProxyController.cs` | 镜像面 +1，其余 30 个 action 零 diff |
| 3.2 | 透传用例：200 zip 字节 + `Content-Type: application/zip` + `Content-Disposition` 透传；404 错误体透传；上游不可达 502（参考 `GenerateTrainingImage_UpstreamImageResponse_Passthrough` 先例） | `tests/Z.Vtuber.Tests/TrainingProxyControllerTests.cs` | 新用例转绿 |

### Phase 4: web 按钮（TDD：组件测试先行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 4.1 | `trainingDatasetExportUrl(datasetId)`（纯拼接 `/api/training/datasets/${encodeURIComponent(datasetId)}/export`，参照 `trainingAudioClipUrl`）+ 单测 | `apps/web/src/api/client.ts`、`client.test.ts` | href 拼接断言转绿 |
| 4.2 | `DatasetItemListProps` + `onItemsLoaded?: (count: number) => void`：`refresh` 成功后回调 `list.length`（失败不回调），与 `onChanged` 同风格 | `apps/web/src/pages/training/DatasetItemList.tsx` | 既有列表测试不回退 |
| 4.3 | 「下载数据集」按钮：audioTab 顶部操作区 `<Space>`（与「从 Transcript 导入」并列；按钮**始终渲染**，导入按钮维持 `selectedId` 条件）——`<Typography.Link href={trainingDatasetExportUrl(selectedId)} download aria-disabled>`，禁用条件 `!selectedId || itemCount === 0`（`aria-disabled` + 置灰 + `preventDefault`）；`DatasetsPage` 持 `itemCount` state（`onItemsLoaded` 回调写入） | `apps/web/src/pages/training/DatasetsPage.tsx` | 与口径 6/7 一致，无新增 npm 依赖 |
| 4.4 | 组件测试：按钮恒渲染；未选定数据集禁用且点击不发起；空数据集（`listTrainingDatasetItems` 返回 `[]`）禁用；正常态 href 为 `/api/training/datasets/{id}/export` 且带 `download`；「从 Transcript 导入」未选中不显示（既有行为不回退） | `apps/web/src/pages/training/DatasetsPage.test.tsx` | 全绿 |

### Phase 5: 测试收口与文档同步

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 5.1 | 全量回归：`dotnet build` + podman 容器 UT（Mongo）；既有 `Training*`/`TrainingProxy*` 测试不回退 | 全部 | 全绿 |
| 5.2 | 跑测口径：trx + `tests/Z.Vtuber.TestReport --check`（单用例 ≤ 5s，新增慢用例须登记 `TimeoutExceptions.json`） | 全部 | 通过 |
| 5.3 | 覆盖率：核心逻辑（编号生成、失败跳过、文件名编码、zip 条目写入、禁用判定）≥ 80% | 全部 | 达标 |
| 5.4 | `npm run codegen` 复核 0 diff（生成物勿手改） | `apps/web/src/api/` | 一致 |
| 5.5 | 文档同步：`servers/training/AGENTS.md`（API 面表 + export 端点、`TrainingDatasetsController` 描述补导出）、`servers/api/AGENTS.md`（训练镜像清单 + export）、`apps/web/AGENTS.md`（按钮与 `trainingDatasetExportUrl` 封装）、PRD 验收项勾选、提案/Plan 互链 | 文档 | 与实现一致 |

## 4. 并行策略

- Phase 1 契约先行（1.1 → 1.2 → 1.3）；1.1 定稿后 Phase 2/3/4 可并行推进（文件面互不重叠：training / api / web）。
- Phase 2 内部顺序 2.1 → 2.2 → 2.3（2.4 随 2.3 收口）；2.2 构造签名先行，避免 2.1 长期红。
- Phase 3 仅依赖 1.2 契约定稿（镜像 action + 测试）；Phase 4 依赖 1.3 codegen（类型）与 4.1/4.2（4.3 依赖 4.1+4.2）。
- Phase 5 收口最后；5.5 文档同步在实现定稿后。

## 5. 验收对照

| PRD/提案目标 | 实现任务 | 验证 |
|--------------|---------|------|
| 功能点 1：「下载数据集」按钮出现；未选定/空数据集禁用；点击下载「数据集名.zip」 | 4.1、4.3、2.3 | `DatasetsPage.test.tsx`（渲染/禁用态）、导出用例 Content-Disposition 断言；PRD 功能点 1 |
| 功能点 2：zip 内仅「6 位数字.mp3/.txt」成对、000001 连续、音频区间与试听一致、txt UTF-8 | 2.3、2.4①⑥ | `TrainingDatasetExportApiTests` zip 读回断言（顺序/编号/字节/编码）；`GetClipAsync` 与试听同源 |
| 功能点 3：点击即开始流式下载、千级条目可完成、期间不阻塞页面 | 2.3（响应头先行 + FlushAsync）、3.1（ResponseHeadersRead 透传）、4.3（浏览器接管） | 代理透传用例；流式口径见提案 §6.1 决策 6（180s 仅限首字节） |
| 功能点 4：音频不可用条目跳过、编号连续、不中断 | 2.3、2.4②③ | 失败跳过/全失败空包用例 |
| 边界：数据集名特殊字符、`endMs=null`、404、网络中断 | 2.4④⑤⑥ | 对应用例；`RequestAborted` 取消无残留 |
| 契约先行 + api 镜像一致 | 1.1–1.3、3.1、3.2 | 5.4 codegen 0 diff；契约评审 |
| 回归不回退、覆盖率 ≥ 80%、单用例 ≤ 5s | 5.1–5.3 | 5.1–5.3 |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|----|------|------|
| 代理 180s 超时与长导出 | `HttpClient.Timeout` 在 `ResponseHeadersRead` 下仅约束首字节 | 响应头先行保证首字节即时；总时长不受限（提案 §6.1 决策 6）；若未来出现单条 clip 极慢导致长空闲，再评估空闲超时 |
| 单条音频内存占用 | `GetClipAsync` 整段入内存，上限 `Ai:MaxAudioBytes`（8 MiB） | 逐条处理后即释放，内存 O(单条)；超限条目按失败跳过 |
| wav 降级条目被跳过 | 缺 mp3 编码的 recording 条目不进 zip，导出集合小于数据集 | PRD 功能点 4 口径（不产生残缺文件）；Warn 日志可观测跳过原因/数量 |
| 文件名编码 | 数据集名含中文/特殊字符 | `filename*=UTF-8''{Uri.EscapeDataString}` + 专项用例（2.4⑤） |
| 构造签名变更 | `TrainingDatasetsController` 新增必填参数 | 仅测试直构点受影响，补桩一行；DI 自动解析零改动 |
| 下载中断 | 网络断开/用户取消 | `RequestAborted` 即停；纯只读、服务端无残留状态；浏览器负责失败提示（PRD §3.5） |
| web 禁用态与「进行中」措辞偏差 | PRD 功能点 3 验收提到「按钮呈进行中状态」，实现按提案裁定不做状态机 | 已在 §1.1 口径 7 标注（提案经人工评审）；浏览器下载管理天然防重复触发 |
| 部署顺序 | 先 training 后 api 再 web | 均为纯新增面，旧版本无感知；回滚按整体版本回退，无持久化副作用 |

## 7. 相关文档

- PRD：`docs/product/draft/20261007-training-dataset-export-zip-prd.md`
- 技术提案：`docs/engineering/proposals/20261007-proposal-training-dataset-export-zip.md`
- 关联 PRD/提案：`20260927-training-audio-dataset-prd.md`、`20260927-proposal-training-audio-dataset.md`（音频数据集初版）
- 模块规范：`servers/training/AGENTS.md`、`servers/api/AGENTS.md`、`servers/common/AGENTS.md`、`apps/web/AGENTS.md`
- 规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`docs/engineering/frontend-rules.md`
- 契约：`contracts/training.openapi.yaml`、`contracts/bilibili-crawler.openapi.yaml`
