---
description: AI训练模块第一期「音频数据集」实现 Plan——基于 PRD v1.0 与技术提案的契约先行与任务分解：四条实施线（training 服务与 common 数据层、8 端点 API 与 Transcript 导入、api 代理镜像、web 训练分组与数据集页面/wavesurfer 波形试听），TDD 测试先行；AudioKey 采用 segment 原始 start 口径（提案决策 4 的一处细化，保证任务偏移变更后重复导入仍去重），transcript/recording 既有能力零改动
type: Permanent
---

# AI 训练 - 音频数据集（第一期） - 实现 Plan

**功能名称**: 训练 - 音频数据集
**关联 PRD**: `docs/product/draft/20260927-training-audio-dataset-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20260927-proposal-training-audio-dataset.md`（v1.0）
**Plan 版本**: v1.0
**创建日期**: 2026-09-27
**作者**: Workflow Agent（代起草）
**feat-branch**: `feat/training-dataset`（需求总分支，已推送 origin；需求总 PR：<https://github.com/Deliay/z-vtuber/pull/136>）

## 1. 概述

本 Plan 基于 PRD v1.0 与技术提案 v1.0 编写，指导「AI 训练 - 音频数据集（第一期）」的编码实施。改动分四条线：① 数据层与服务骨架——`servers/common` 新增 `TrainingDatasetRecord`/`TrainingDatasetItemRecord` 实体、两个 Repository 与 `EnsureTrainingIndexesAsync`（`(Type,Name)` 与 `(DatasetId, AudioKey)` 唯一索引），新建独立 `servers/training`（Z.Vtuber.Training，8 端点 + TranscriptApiClient 导入）；② API——数据集 CRUD（重名 409、删除级联清条目）、条目列表/更新（「缺失 vs 显式 null」语义）/批量删除、从 Transcript 导入（仅 DONE 任务、尊重 offset、按 AudioKey 去重并反馈 imported/skipped）；③ api 代理——`/api/training/**` 8 端点镜像（`ForwardAsync` 透传模式，502 口径统一）；④ web——菜单分组扩展出「训练 → 数据集」，数据集页面（Tab、下拉管理、导入对话框、七字段列表多选/分组、wavesurfer.js 波形试听）。识别链路（consumer/ASR）、transcript 既有端点与 recording 音频片段端点零改动。实施全程遵循 `docs/engineering/common-rules.md`（契约先行、TDD、核心逻辑覆盖率 ≥ 80%）、`backend-rules.md`、`frontend-rules.md`、z-coding 开发流程与各服务 `AGENTS.md`。

### 1.0 上游核实结论（代码现状复查）与偏差

对需求总分支 `feat/training-dataset` 代码逐项核实，提案技术路径**可行**，引用与现状描述属实；下列核实要点在对应任务中直接使用：

- **识别数据口径**：`TranscriptTaskRecord`（`servers/common/Persistence/Entities/TranscriptTaskRecord.cs`）含 `OffsetMs`（任务播放偏移，毫秒可正可负默认 0）与 `RoomId`/`File`（拼音频地址的全部要素）；`TranscriptSegmentRecord`（`TranscriptSegmentRecord.cs:6-20`）字段 `TaskId/StartTime/EndTime/Text`，唯一索引 `TaskId_1_StartTime_1`（`servers/common/Persistence/MongoDbInitializer.cs:972-979`）——segment 无对外 id（`TranscriptSegmentDto` 仅 `start/end/text`，`servers/recording-transcript-api/Contracts/Dtos.cs`），**(TaskId, 原始 StartTime) 即其唯一标识依据**；
- **offset 口径**：`TranscriptMapping.ToSegmentDto`（`Contracts/TranscriptMapping.cs:42-43`）返回 start/end = 落库时间 + `OffsetMs`（负值不钳制）；`ToTaskDto` 输出 `offsetMs`——**导入方可用「返回 start − task.offsetMs」反推原始 start**（本 Plan AudioKey 依赖此点，见偏差）；`GET /api/transcripts/task?status=DONE`（`contracts/recording-transcript.openapi.yaml:87-103`，`TaskStatusQuery`）+ 任务详情 `TranscriptTaskDetail{task, segments}`（:570-580）即导入数据源；
- **试听能力**：音频片段端点 `GET /api/b/live/{roomId}/recordings/{fileName}/audio?startMs&endMs`（api 代理 → recording），区间 ≤ `AudioClipMaxDurationMs`（`servers/recording/Configuration/RecordingOptions.cs:57`，默认 300000 即 5 分钟硬约束）；前端构造器 `apps/web/src/api/client.ts:153-161` `recordingAudioClipUrl(roomId, fileName, startMs, endMs)` 可复用；
- **web 现状**：`apps/web/src/App.tsx:9-23` 扁平 `menuItems`（无分组概念，需最小扩展）；`utils/format.ts` `formatPosition`（ms→`m:ss`）；`client.ts` 统一 `request<T>` + `ApiError{status, serverMessage}`；`package.json` `codegen` 串联 6 契约（追加第 7 个 training 契约）；全仓库无波形库，wavesurfer.js 为唯一新增 npm 依赖；
- **持久层与服务模式**：`MongoCollectionNames` 常量 + `XxxRecord` + `XxxRepository` + `EnsureXxxIndexesAsync` 幂等建索引（范本 `EnsureTranscriptIndexesAsync`）；api 代理范本 `RecordingTranscriptProxyController`（`ForwardAsync` 对 POST/PUT 透传 body、502 `{"error": "... service unavailable"}`；named client `TranscriptService` 注册于 `servers/api/Program.cs:56`）；AppHost 编排范本 `infra/apphost/AppHost.cs`（`AddProject` + `WithReference(mongo)` + `WithEnvironment`）；slnx 项目注册与 apphost csproj `ProjectReference` 需同步；
- **测试设施**：`tests/Z.Vtuber.Tests` 控制器级集成测试（`MongoDbFixture`/`RedisFixture` + 直接实例化控制器，范本 `TranscriptApiTests.cs`）；代理测试 `RecordingTranscriptProxyControllerTests`（`StubHandler` 记录方法/路径/body）；web 为 Vitest + RTL。

**偏差与提案遗漏（2 处，均在本 Plan 收敛）**：

1. **AudioKey 时间口径（提案 §6.1 决策 4 的一处细化）**：提案选「偏移后 start」，但偏移是任务级可变设置——若导入后任务偏移再变更并重复导入，AudioKey 随之平移，**唯一索引去重失效产生整份重复数据**，违反 PRD 成功标准「重复导入同一 Transcript 不产生重复数据」；且提案自身 §1.1① 与 PRD「音频标识 = segment 的 id」均指向 `(TaskId, 原始 StartTime)` 标识依据。本 Plan 采用 **`transcript:{taskId}:{原始startMs}`**（原始 start = 详情返回 start − `task.offsetMs`；key 格式、`(DatasetId, AudioKey)` 唯一索引与决策 2 全部不变），`StartMs/EndMs/Text` 仍为偏移后展示口径（PRD「导入时间尊重 offset」不变）。影响面仅音频标识生成一处（2.3）与对应断言（1.3、1.6）；如评审坚持提案口径，回退点即该一处 + 2 组断言。
2. **提案遗漏 Dockerfile 与镜像流水线**：提案 §9 要求生产部署 training 服务，但项目结构（§7）未含 `servers/training/Dockerfile` 与 `.github/workflows/build-training-image.yml`（既有每服务均有）。本 Plan 在 2.2 补齐（照抄 `recording-transcript-api` 同名文件模式），否则部署方案不可执行。

### 1.1 实施范围

- **做**：
  - `contracts/`：新建 `training.openapi.yaml`（8 端点 + schema，含上文 AudioKey 口径与更新请求「缺失 vs 显式 null」语义）；`bilibili-crawler.openapi.yaml` + `/api/training/**` 代理镜像；`apps/web` `npm run codegen` 生成 `schema-training.d.ts`（勿手改）。
  - `servers/common`：`MongoCollectionNames` + 2 常量；`TrainingDatasetRecord`/`TrainingDatasetItemRecord`（含 `Extensions` 扩展字段）；`TrainingDatasetRepository`/`TrainingDatasetItemRepository`；`EnsureTrainingIndexesAsync`（4 索引）。
  - `servers/training`（新建）：csproj/Program.cs/两个控制器/`TranscriptApiClient`/`TrainingOptions`/DTO 映射/launchSettings 8086/Dockerfile/CI 镜像流水线。
  - `servers/api`：`TrainingProxyController` + `TrainingService:BaseUrl` named client。
  - `infra/apphost`：注册 training（mongo + `TranscriptService__BaseUrl`）；`servers/Z.Vtuber.slnx` 与 apphost csproj 注册。
  - `apps/web`：`App.tsx` 菜单分组 + `PageKey`；`pages/training/` 五组件；`client.ts` training 封装 + 试听 URL 构造；`utils/format.ts` + 时间解析；`package.json` + wavesurfer.js。
  - `tests/Z.Vtuber.Tests` 与 `apps/web` Vitest 测试（清单见 §3 Phase 1）；文档同步（3.3）。
- **不做**：识别过程（consumer/ASR）与 transcript 既有端点零改动；recording 音频片段端点与 5 分钟上限零改动；情感识别/二次 ASR 加工（`Extensions` 仅预留）；更多 Tab 与音频类型实现（仅结构预留）；条目分页/虚拟滚动（千级全量返回，决策 6）；数据集导出/训练任务对接；`transcript_*` 集合与索引零改动；无数据迁移。

### 1.2 现状基线（范本）

- 数据层范本：`MongoDbInitializer.EnsureTranscriptIndexesAsync`（幂等 `CreateOneAsync`）；`TranscriptTaskRepository.UpdatePositionAsync`（更新模式）。
- 服务骨架范本：`servers/recording-transcript-api`（Program/Controllers/Contracts/Clients/Dockerfile 全套）。
- 代理范本：`RecordingTranscriptProxyController` + `RecordingTranscriptProxyControllerTests`（`StubHandler`）。
- web 范本：`TranscriptsPage.tsx`（Tab `role="tablist"`）、`client.ts`（`request<T>`/URL 构造器）、`TaskListTab.test.tsx`、`SegmentAudioPlayer.test.tsx`（试听降级口径）。
- 测试范本：`TranscriptApiTests`（控制器级 + Mongo fixture）、`TranscriptTaskRepositoryTests`、`TranscriptMappingTests`。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash、分支不复用）与 Conventional Commits：

- 本需求开发全程只使用需求总分支 `feat/training-dataset`：契约、实现、测试、文档全部直接在该分支提交，不切开发子分支（沿用前几期需求既定做法）；需求总 PR #136 已存在，提交照常推送更新分支。
- 提交信息：契约先行 `feat(contracts):`、测试先行 `test:`、实现 `feat:`、文档 `docs:`；测试与实现分开提交；scope 使用 `training`/`common`/`api`/`web` 既有口径。
- 编码实现按 z-coding 流程以 subagent 按子项目并行（§4 线划分即子项目划分）。

## 3. 实施阶段

按「契约先行（0）→ TDD 测试先行（红）→ 实现（绿）→ 收口验证」推进。Phase 0 串行先行；Phase 1/2 按四条线并行（线内串行）；Phase 3 串行收口。

### Phase 0: 契约先行（串行，全部实现的联调基线）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | 新建 `training.openapi.yaml`：8 端点（GET/POST `/api/training/datasets`、PUT/DELETE `/api/training/datasets/{datasetId}`、GET `/api/training/datasets/{datasetId}/items`（query `audioType?`/`source?`）、POST `.../items/import`（body `{taskId}`→200 `{imported, skipped}`；400 `task not done`、404 `dataset/task not found`、502 `transcript service unavailable`）、PUT `.../items/{itemId}`、POST `.../items/batch-delete`（body `{itemIds}`→200 `{deleted}`））+ `TrainingDataset`/`TrainingDatasetItem`/请求响应 schema；要点：① `type` 本期固定 `audio`（缺失或他值 400 `unsupported dataset type`）；② `audioKey` 描述为 `transcript:{taskId}:{原始startMs}`（原始 start = 任务详情 start − 任务 offsetMs，不含播放偏移，保证偏移变更后重复导入仍去重）；③ `startMs/endMs` 为偏移后展示口径，`endMs` nullable（null=到音频末尾）；④ 更新请求语义：字段缺失=不变、`endMs` 显式 null=置空、`text/audioUri/startMs` 显式 null → 400；`endMs` 非空须 `> startMs`（否则 400 `invalid time range`）、`startMs` ≥ 0；⑤ `extensions` object 可空（预留情感/二次 ASR）；⑥ 名字 trim 非空 ≤ 128，重名 409；数据集删除级联清条目 | `contracts/training.openapi.yaml` | 契约自洽，与提案 §3.1 + 本 Plan §1.0 偏差一致 |
| 0.2 | `bilibili-crawler.openapi.yaml` + `/api/training/**` 8 端点镜像（body/状态码透传；上游不可达 502 `{"error": "training service unavailable"}`，与 transcript 镜像同口径） | `contracts/bilibili-crawler.openapi.yaml` | 与 0.1 语义一致 |
| 0.3 | web codegen：`package.json` `codegen` 追加 `training` 契约 → 生成 `src/api/schema-training.d.ts`（勿手改）；提交契约 + codegen 作为评审基线 | `apps/web/package.json`、`apps/web/src/api/schema-training.d.ts` | 生成物与契约一致；`npm run lint` 通过 |

### Phase 1: 测试先行（红）

**线 A：数据层 + 服务骨架（common + training）**

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | `TrainingDatasetRepositoryTests`：新建落库（Name trim、Type）；同 `(Type,Name)` 重复插入抛 DuplicateKey；`ListAsync(type)` 按 CreatedAt 升序；`RenameAsync` 命中/未命中/改重名抛 DuplicateKey；`DeleteAsync` 命中/未命中 | `tests/Z.Vtuber.Tests/TrainingDatasetRepositoryTests.cs` | 红 |
| 1.2 | `TrainingDatasetItemRepositoryTests`：`InsertManyIgnoreDuplicatesAsync` 全新导入 inserted=N；重导同 key 全跳过 imported=0；混合批次按 `MongoBulkWriteException` 计数（imported+skipped=提交数）；`ListAsync` 按 `audioType`/`source` 过滤与排序；`UpdateAsync` 部分字段更新与 `endMs` 置 null；`DeleteManyAsync` 部分 id 存在时返回删除数；`DeleteByDatasetAsync` 级联清空 | `tests/Z.Vtuber.Tests/TrainingDatasetItemRepositoryTests.cs` | 红 |
| 1.3 | `TrainingMappingTests`（含 AudioKey 纯函数）：`AudioKey = transcript:{taskId}:{原始startMs}`——offset=0 任务 key=start；offset=+2500 任务返回 start=原始+2500 时 key 仍用原始 start，`StartMs/EndMs` 为偏移后值；负偏移同理（原始 start 反推 = start − offsetMs，恒 ≥ 0）；`AudioUri` 拼接 `b/live/{roomId}/recordings/{file}`，`file` 空 → 空串；时间校验纯函数：负 `startMs`、`endMs <= startMs` 判非法，`endMs` null 合法 | `tests/Z.Vtuber.Tests/TrainingMappingTests.cs` | 红 |

**线 B：training 8 端点 API（controllers）**

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.4 | `TrainingDatasetsApiTests`（Mongo fixture + 控制器直测）：新建 201（仅名字）；重名 409 `dataset name exists`；名字空/超 128 400；`type` 非 `audio` 400；列表 `type` 过滤 + CreatedAt 升序；重命名 200/409/404；删除 204 且**条目级联清空**、404 | `tests/Z.Vtuber.Tests/TrainingDatasetsApiTests.cs` | 红 |
| 1.5 | `TrainingDatasetItemsApiTests`：列表七字段 + `audioType`/`source` 过滤 + 排序；更新 audioUri/text/startMs/endMs 单独与组合（缺失字段不变）、`endMs` 显式 null 置空、`endMs <= startMs` 400 `invalid time range`、负 `startMs` 400、`text/audioUri/startMs` 显式 null 400、404（dataset/item）；批量删除部分 id 不存在仍删其余并返回 `deleted` 数、dataset 404 | `tests/Z.Vtuber.Tests/TrainingDatasetItemsApiTests.cs` | 红 |
| 1.6 | `TrainingImportApiTests`（stub `ITranscriptApiClient`）：DONE 任务导入条目字段逐项正确（类型/来源/地址/标识/偏移后起止/文本）；**offset≠0 任务：起止时间为偏移后、AudioKey 为原始 start**；重复导入 `imported=0, skipped=N`；**任务偏移变更后再次导入仍 `imported=0`（偏差 1 锁定）**；PENDING/RUNNING/FAILED → 400 `task not done`；taskId 非法/不存在 → 404；dataset 404；transcript-api 不可达/异常 → 502 `transcript service unavailable`；`file` 为空照常导入（AudioUri 空串） | `tests/Z.Vtuber.Tests/TrainingImportApiTests.cs` | 红 |

**线 C：api 代理镜像**

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.7 | `TrainingProxyControllerTests` 扩展（`StubHandler` 模式）：8 镜像路由的方法/路径（含 query 与 body/Content-Type 透传）断言；200/201/204/400/404 透传；上游不可达 502 `training service unavailable` | `tests/Z.Vtuber.Tests/TrainingProxyControllerTests.cs` | 红 |

**线 D：web（Vitest + RTL）**

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.8 | `client.test.ts` 扩展：training 9 个封装的路径/method/body 断言（含 `importTrainingDatasetItems`、`updateTrainingDatasetItem` 的 endMs null 透传、`batchDeleteTrainingDatasetItems`）；400 抛 `ApiError` 含服务端消息；`trainingAudioClipUrl(audioUri, startMs, endMs)`：合法 uri → `/api/b/live/{roomId}/recordings/{file}/audio?startMs&endMs`、`endMs` 空 → `startMs+300000` 兜底、非法 uri → null | `apps/web/src/api/client.test.ts` | 红 |
| 1.9 | `App.test.tsx` 扩展：导航出现「训练」分组且含「数据集」入口；既有 5 菜单项渲染不变；点击进入数据集页 | `apps/web/src/App.test.tsx` | 红 |
| 1.10 | `DatasetsPage.test.tsx`：Tab 结构仅「音频数据集」；下拉列出/切换数据集；新建（输入名字即可）/重命名/删除（`confirm` 二次确认）；重名提示且不生效；**未选中数据集不显示「从 Transcript 导入」按钮**；空态引导导入 | `apps/web/src/pages/training/DatasetsPage.test.tsx` | 红 |
| 1.11 | `TranscriptImportDialog.test.tsx`：仅可选 `status=DONE` 任务；确认后调 import 并反馈 `{imported, skipped}`；取消不请求；400/502 错误提示；**模态弹窗结构（全屏 fixed 半透明遮罩 + flex 居中面板、高层级；点击遮罩/取消关闭、点面板内不关闭）** | `apps/web/src/pages/training/TranscriptImportDialog.test.tsx` | 红 |
| 1.12 | `DatasetItemList.test.tsx`：七字段展示（音频标识不可编辑）；audioUri/text/起止时间编辑保存（`m:ss`/`m:ss.S` 输入毫秒互转）；`endMs` 可置空；非法时间（负值/结束早于开始）提示且不保存；多选 + 批量删除（confirm）后刷新；分组切换（不分组/按来源/按类型）与分组头全选联动批量删除；**列宽策略（`table-layout: fixed` + `colgroup`）：来源/地址/标识三列限宽省略号截断且 `title` 悬浮完整值、「文本」列不设宽度占满剩余并正常换行、短列固定窄宽；编辑态「文本」为多行 `textarea` 且 `width:100%` 占满单元格（`aria-label="文本"` 不变）；波形试听在**被点击条目行正下方的展开行内**（`colSpan=9` 覆盖全列、位于表格内）渲染、同一时刻仅展开一行、分组模式下同样插入对应条目行之下** | `apps/web/src/pages/training/DatasetItemList.test.tsx` | 红 |
| 1.13 | `DatasetItemWaveform.test.tsx`：点击条目渲染波形容器并以区间 url 加载（含 `endMs` 空兜底）；**加载就绪即自动播放**（无需再点播放）、「播放/停止」用于重播/中止、停止/切换条目/卸载不误播串播、`play()` 失败（抛错/reject）非阻断提示；加载失败提示「音频不可用」且不阻断列表操作；`audioUri` 为空/不可解析同样降级；**另以 `waveformLoader.test.ts` 直测真实 loader（mock wavesurfer）断言 `play` 入参为片段相对时间轴 `0 → (endMs-startMs)/1000`（含 `endMs` 空走 300000 兜底），锁定「不得按录播绝对秒 seek 到已裁剪片段之外」** | `apps/web/src/pages/training/DatasetItemWaveform.test.tsx`、`apps/web/src/pages/training/waveformLoader.test.ts` | 红 |

### Phase 2: 实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | **数据层（common）**：① `MongoCollectionNames` + `TrainingDatasets`(`training_datasets`)/`TrainingDatasetItems`(`training_dataset_items`)；② `TrainingDatasetRecord`（Name/Type/CreatedAt/UpdatedAt）与 `TrainingDatasetItemRecord`（DatasetId/AudioType/Source/AudioUri/AudioKey/StartMs(long)/EndMs(long?)/Text/`Extensions`(BsonDocument?，预留情感/二次 ASR)/时间戳）；③ 两个 Repository（签名按提案 §3.2，`InsertManyIgnoreDuplicatesAsync` 用 `InsertManyOptions{IsOrdered=false}` + `MongoBulkWriteException.Result.InsertedCount` 计 imported、`skipped=提交数-imported`）；④ `MongoDbInitializer.EnsureTrainingIndexesAsync` 幂等建 4 索引：`(Type,Name)` 唯一、`(DatasetId,AudioKey)` 唯一、`(DatasetId,AudioType)`、`(DatasetId,Source)` | `servers/common/Persistence/`（Entities/Repositories/MongoDbInitializer.cs） | 1.1、1.2 转绿 |
| 2.2 | **training 服务骨架（新建）**：`Z.Vtuber.Training.csproj`（引用 common）+ `Program.cs`（Mongo/仓储/`TranscriptApiClient`（named client，节 `TranscriptService:BaseUrl` 默认 `http://recording-transcript-api`）/`TrainingOptions`/启动跑 `EnsureTrainingIndexesAsync`/`/health`/`MapControllers`）+ `Clients/TranscriptApiClient.cs`（`GetTaskDetailAsync` 返回任务（含 `offsetMs`/`roomId`/`file`）+ segments，异常上抛供控制器映 502）+ `Contracts/Dtos.cs`/`TrainingMapping.cs` + launchSettings 8086 + **Dockerfile + `.github/workflows/build-training-image.yml`（偏差 2 补齐，照抄 recording-transcript-api 模式，paths `servers/training/**`+`servers/common/**`）**；`servers/Z.Vtuber.slnx` 注册；`tests/Z.Vtuber.Tests` 加项目引用 | `servers/training/`、`servers/Z.Vtuber.slnx`、`.github/workflows/` | 骨架可启动、`dotnet build` 过 |
| 2.3 | **映射与 AudioKey（偏差 1 落点）**：`TrainingMapping`——`GenerateAudioKey(taskId, rawStart)` → `transcript:{taskId}:{rawStart}`（**rawStart = segment.start − task.offsetMs**）；条目映射 `StartMs/EndMs/Text` 取返回值（偏移后口径）、`AudioUri = file 空 ? "" : $"b/live/{roomId}/recordings/{file}"`；更新请求 DTO 用 `JsonElement?`（或等价封装）区分「缺失/显式 null」；时间校验纯函数（`startMs >= 0`、`endMs` 非空须 `> startMs`） | `servers/training/Contracts/` | 1.3 转绿 |
| 2.4 | **数据集 API（接口 1-4）**：`TrainingDatasetsController`——列表（`type` 必填校验）、新建（trim/≤128、DuplicateKey → 409）、重命名（400/404/409）、删除（先 `DeleteByDatasetAsync` 再删数据集，204/404）；错误体统一 `{"error": "..."}` | `servers/training/Controllers/TrainingDatasetsController.cs` | 1.4 转绿 |
| 2.5 | **条目 API（接口 5/7/8）**：`TrainingDatasetItemsController`——列表（`audioType`/`source` 过滤，CreatedAt/AudioKey 升序，本期全量）；更新（缺失不变、`endMs` 显式 null 置空、显式 null 其余字段 400、时间校验 400 `invalid time range`、404）；批量删除（部分 id 允许不存在，返回 `{deleted}`） | `servers/training/Controllers/TrainingDatasetItemsController.cs` | 1.5 转绿 |
| 2.6 | **导入（接口 6）**：dataset 404 → `ObjectId.TryParse`/任务不存在 404 → `Status != DONE` 400 `task not done` → 按 2.3 映射 → `InsertManyIgnoreDuplicatesAsync` → 200 `{imported, skipped}`；transcript-api 传输异常 → 502；Info 日志 `training dataset import: datasetId={} taskId={} imported={} skipped={}`（不输出无关字段） | `servers/training/Controllers/TrainingDatasetItemsController.cs`（或独立 action）、`Clients/TranscriptApiClient.cs` | 1.6 转绿 |
| 2.7 | **api 代理镜像**：`TrainingProxyController`（`[Route("api")]`，8 个 action → `ForwardAsync` 同款私有方法，query 原样透传、POST/PUT body 透传、502 `training service unavailable`）；`Program.cs` 注册 named client `TrainingService`（节 `TrainingService:BaseUrl` 默认 `http://training`） | `servers/api/Controllers/TrainingProxyController.cs`、`servers/api/Program.cs` | 1.7 转绿 |
| 2.8 | **AppHost 编排**：`AppHost.cs` `AddProject<Projects.Z_Vtuber_Training>("training")` + `.WithReference(mongo).WaitFor(mongo)` + `.WithEnvironment("TranscriptService__BaseUrl", transcriptApi.GetEndpoint("http"))` + `.WithHttpHealthCheck("/health")`；`infra/apphost/Z.Vtuber.AppHost.csproj` 加 ProjectReference | `infra/apphost/` | Aspire 可拉起 training |
| 2.9 | **web 实现**：① `App.tsx` `menuItems` 扩展为分组结构（`{group?, items}`，既有 5 项归默认区渲染不变，新增分组「训练」→「数据集」，`PageKey` + `training-datasets`）；② `package.json` + `wavesurfer.js` 依赖；③ `utils/format.ts` + `parsePosition`（`m:ss`/`m:ss.S` → ms，非法返回 null）；④ `pages/training/`：`DatasetsPage`（Tab 容器 `role="tablist"`）、`DatasetSelector`（下拉 + 新建/重命名/删除 confirm）、`TranscriptImportDialog`（`listTranscriptTasks({status:'DONE'})` 选择 + 导入 + `{imported, skipped}` 反馈）、`DatasetItemList`（七字段表格、勾选多选、批量删除 confirm、不分组/按来源/按类型切换、分组头全选，客户端聚合一次拉全）、`DatasetItemWaveform`（挂载 wavesurfer，url 用 `trainingAudioClipUrl`，`endMs` 空取 `startMs+300000` 并标注「试听预览上限 5 分钟」，加载失败/uri 不可解析提示「音频不可用」非阻断）；⑤ `client.ts` training 封装 + `trainingAudioClipUrl`（解析 `b/live/{roomId}/recordings/{fileName}` 复用 `recordingAudioClipUrl`） | `apps/web/src/` | 1.8–1.13 转绿 |
| 2.10 | codegen 一致性复核（`npm run codegen` diff 为空）+ `npm run lint`/`npm run build`/`npm run test`、`dotnet build` 全通过 | 全部 | 构建零告警 |

### Phase 3: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 3.1 | 全量测试与覆盖率：`dotnet test tests/Z.Vtuber.Tests` 全绿（含既有回归）；`apps/web` `npm run test` 全绿；核心逻辑（导入映射/AudioKey 生成/去重计数/时间校验/试听 url 拼接）覆盖率 ≥ 80%（common-rules） | 全部通过 |
| 3.2 | 兼容性回归：`git diff` 确认 consumer/ASR、`servers/recording-transcript-api`、`servers/recording` 与 `transcript_*` 集合零改动；既有 transcript 详情/SSE/字幕/试听与 api 既有代理回归不回退 | diff/grep 无违规 |
| 3.3 | 文档同步：新建 `servers/training/AGENTS.md`（技术栈/模块结构/API 面/配置/部署要点）；更新 `servers/common/AGENTS.md`（2 实体 2 仓储 + 4 索引 + Extensions 预留）、`servers/api/AGENTS.md`（TrainingProxyController 镜像路由）、`apps/web/AGENTS.md`（目录 + 训练分组/数据集页/codegen 7 契约）、`docs/engineering/backend-rules.md` 项目结构补 `training/`；本 Plan 与提案/PRD 链接互验 | 文档与实现一致 |
| 3.4 | 手工验证（z-aspire-orchestrator，可选但推荐）：Aspire 拉起 → 新建数据集 → 从一个 **offset≠0** 的 DONE Transcript 导入（起止时间为偏移后、条数反馈正确）→ 点击条目波形试听（区间=起止）→ 编辑文本/起止/uri 与 `endMs` 置空 → **重复导入 0 新增** → 改任务偏移后再导入仍 0 新增 → 按来源分组全选删除 → 删除数据集级联清空 → 既有识别结果页/试听回归正常 | 手工验证记录 |

## 4. 并行策略

- **Phase 0 串行先行**（0.1→0.2→0.3）：契约是全部线的联调与 codegen 基线。
- **Phase 1/2 四条线并行，线内串行**（亦为 subagent 子项目划分）：
  - **线 A 数据层 + 骨架**：1.1→1.2→1.3→2.1→2.2→2.3（common → 骨架/部署 → 映射；含 2.8 AppHost/slnx 收尾）；
  - **线 B API**：1.4→1.5→1.6→2.4→2.5→2.6（依赖线 A 的实体/仓储/映射与 0.1 契约；`TrainingDatasetItemsController.cs` 承载接口 5-8，线内串行）；
  - **线 C 代理**：1.7→2.7（与 A/B 零文件交集，可最先开工）；
  - **线 D web**：1.8→1.9→1.10→1.11→1.12→1.13→2.9（依赖 0.3 codegen；组件以 mock fetch 测试驱动，不阻塞后端联调，联调在 3.4 收口）。
- **文件冲突规避**：`servers/common/Persistence` 归线 A 单线；training 控制器归线 B；`client.ts`/`App.tsx`/`utils/format.ts` 改动全部收敛线 D；AppHost/slnx/Dockerfile/CI 归线 A 收尾。
- **依赖关系**：2.3–2.6 依赖 0.1；2.7 依赖 0.2；2.9 依赖 0.3；3.x 全线完成后串行收口。
- 全部提交在需求总分支 `feat/training-dataset`。

## 5. 验收对照

| PRD 功能点 / 成功标准 | 实现任务 | 验证方式 |
|-----------|---------|---------|
| 功能点 1：训练分组与数据集入口（多 Tab） | 0.3、2.9① | 1.9、1.10 |
| 功能点 2：数据集下拉管理（新建/重命名/删除、重名提示、级联删除） | 0.1、2.1④、2.4 | 1.1、1.4、1.10 |
| 功能点 3：从 Transcript 导入（仅 DONE、尊重 offset、按标识去重、反馈条数） | 0.1、2.3、2.6 | 1.3、1.6（含偏差 1 锁定用例）、1.11 |
| 功能点 4：七字段列表与四字段编辑（endMs 置空、标识唯一不可编辑、非法时间拦截） | 0.1、2.3、2.5 | 1.5、1.12 |
| 功能点 5：波形展示与按区间播放（endMs 空=预览上限、失效提示不阻断） | 2.9④⑤ | 1.8、1.13 + 3.4 |
| 功能点 6：多选与批量删除（确认） | 0.1（batch-delete）、2.5 | 1.5、1.12 |
| 功能点 7：按来源/类型分组与分组全选 | 0.1（过滤参数）、2.5、2.9④ | 1.5、1.12 |
| 成功标准：闭环可用、试听与识别结果口径一致 | 2.6、2.9 | 3.4 手工验证 |
| 成功标准：标识唯一、重复导入不产生重复数据 | 2.1④、2.3、2.6 | 1.2、1.6、3.4（含偏移变更后重导） |
| 非功能：扩展预留（Extensions/多 Tab/多类型） | 0.1⑤、2.1② | 契约与实体评审 |
| 非功能：安全（沿用管理后台口径，不新增公开面） | 2.7（既有代理模式） | 1.7 + 3.2 |
| 非功能：兼容（识别/试听/字幕零改动） | 范围约束 | 3.2 diff 确认 |
| 契约与质量门槛（codegen 一致/覆盖率 ≥ 80%/TDD） | 0.3、2.10、3.1 | codegen diff、覆盖率报告、红-绿提交记录 |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| AudioKey 口径（偏差 1） | 提案决策 4 用偏移后 start，任务偏移变更后重复导入会绕过 `(DatasetId, AudioKey)` 去重产生整份重复数据（违反 PRD 成功标准 1.3） | 本 Plan 用原始 start（= 返回 start − offsetMs）生成 AudioKey；1.3/1.6 锁定断言；如评审坚持提案口径，回退点为 `GenerateAudioKey` 一处 + 对应断言 |
| 更新请求「缺失 vs 显式 null」 | `long?` 无法区分「不改」与「置空 endMs」，序列化默认行为会误伤 | 请求 DTO 用 `JsonElement?`/等价封装显式判别（2.3）；1.5 锁定 |
| 导入去重计数语义 | unordered bulk 部分撞唯一索引抛 `MongoBulkWriteException`，易误算 | 以 `Result.InsertedCount` 为 imported、`skipped=提交数-imported`（2.1③）；1.2 锁定混合批次 |
| 试听 5 分钟硬约束 vs 「endMs 空=到末尾」 | 片段端点 `AudioClipMaxDurationMs` 上限，完整语义待导出/加工场景 | 决策 5：预览截取 `startMs+300000` 并在界面标注；recording 端点零改动 |
| 千级全量返回 | 数据集量级千级（整场识别片段数百到数千），未来可能超量级 | 决策 6：本期全量 + 前端分组/多选；契约 query 预留分页扩展位 |
| 级联删除非事务 | 条目先删、数据集后删，中途失败可能残留条目 | 删除幂等可重试；无外键侵入既有集合；1.4 断言成功路径级联清空 |
| wavesurfer.js 新增依赖 + 音频加载失败 | 唯一新增 npm 依赖；uri 失效/为空/不可解析 | 加载失败统一「音频不可用」非阻断（PRD 边界）；1.13 锁定降级 |
| `file` 为空的导入条目 | 无可用音频地址 | 照常导入（AudioUri 空串）、试听提示不可用、可手工修订地址（PRD 边界）；1.6 锁定 |
| 代理 502 口径 | 需与既有 transcript 镜像一致 | 统一 `{"error": "training service unavailable"}`（0.2/2.7）；1.7 锁定 |
| 提案遗漏部署物（偏差 2） | 缺 Dockerfile/镜像流水线则 §9 部署不可执行 | 2.2 补齐（照抄 recording-transcript-api 模式） |
| 依赖 | transcript-api 任务详情（segments 含 offsetMs 口径 + `task.offsetMs` 可反推原始 start，已核实）；recording 音频片段端点；Mongo `(DatasetId, AudioKey)` 唯一索引；无新增 NuGet 依赖 | 均已核实存在（§1.0）；.NET 10、TDD、覆盖率 ≥ 80% 为项目既有规范 |

## 7. 相关文档

- PRD：`docs/product/draft/20260927-training-audio-dataset-prd.md`（v1.0）
- 技术提案：`docs/engineering/proposals/20260927-proposal-training-audio-dataset.md`（v1.0）
- 前序 Plan：[[20260916-plan-recording-transcript]]、[[20260927-plan-transcript-playback-offset]]
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`docs/engineering/frontend-rules.md`
- 模块规范：`servers/common/AGENTS.md`、`servers/api/AGENTS.md`、`servers/recording-transcript-api/AGENTS.md`、`servers/recording/AGENTS.md`、`apps/web/AGENTS.md`
- 既有契约：`contracts/recording-transcript.openapi.yaml`、`contracts/bilibili-crawler.openapi.yaml`、`contracts/recording.openapi.yaml`
