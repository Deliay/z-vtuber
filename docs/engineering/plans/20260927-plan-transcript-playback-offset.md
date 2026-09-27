---
description: 识别结果播放偏移实现 Plan——基于 PRD v1.0 与技术提案（feat/transcript-playback-offset）的契约先行与任务分解：四条实施线（transcript_tasks OffsetMs 字段与仓储、PUT offset 端点与三出口偏移应用、api 代理镜像路由、web 偏移设置入口与保存后重置刷新/试听钳制），TDD 测试先行，DTO 映射层统一「落库时间 + OffsetMs」，consumer 与落库数据零改动
type: Permanent
---

# 识别结果播放偏移（Offset） - 实现 Plan

**功能名称**: 识别结果播放偏移
**关联 PRD**: `docs/product/draft/20260927-transcript-playback-offset-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20260927-proposal-transcript-playback-offset.md`（v1.0）
**Plan 版本**: v1.0
**创建日期**: 2026-09-27
**作者**: Workflow Agent（代起草）
**feat-branch**: `feat/transcript-playback-offset`（需求总分支，已推送 origin；需求总 PR：<https://github.com/Deliay/z-vtuber/pull/132>）

## 1. 概述

本 Plan 基于 PRD v1.0 与技术提案 v1.0 编写，指导「识别结果播放偏移」的编码实施。改动分四条线：① 数据层——`servers/common` 的 `TranscriptTaskRecord` 新增 `OffsetMs` 字段（long，可正可负，默认 0）与 `TranscriptTaskRepository.UpdateOffsetAsync`；② transcript-api——新增 `PUT /api/transcripts/task/{taskId}/offset` 端点（±24h 校验），任务详情查询、SSE 实时推送、SRT/ASS 字幕导出三个出口在 DTO 映射层统一应用「落库时间 + OffsetMs」；③ api 代理——`RecordingTranscriptProxyController` 新增 PUT 镜像路由（`ForwardAsync` 已支持 PUT body 透传，零改动）；④ web——识别结果面板新增「播放偏移」设置入口（秒输入、0.1 精度、正负值），保存成功后清空已合并片段缓存并重拉详情、重建 SSE，试听起点/终点钳 0、整场出界片段置灰。识别链路（consumer/ASR）与 `transcript_segments` 落库数据零改动。实施全程遵循 `docs/engineering/common-rules.md`（契约先行、TDD、核心逻辑覆盖率 ≥ 80%）、`backend-rules.md`、`frontend-rules.md` 与各服务 `AGENTS.md`。

### 1.0 上游核实结论（代码现状复查）

对需求总分支 `feat/transcript-playback-offset` 代码逐项核实，提案技术路径**可行**，引用与现状描述全部属实；下列核实要点在对应任务中直接使用：

- **数据层**：`TranscriptTaskRecord`（`servers/common/Persistence/Entities/TranscriptTaskRecord.cs:31-62`）现有字段 `Id/RecordingId/RoomId/File/ModelId/Type/Mode/Source/Position/Status/Error/CreatedAt/UpdatedAt`，无 offset 字段；`TranscriptTaskRepository`（`servers/common/Persistence/TranscriptTaskRepository.cs`）无偏移更新方法，`UpdatePositionAsync`（:80-86，`Builders.Update.Set(...) + Set(UpdatedAt, DateTime.UtcNow) + UpdateOneAsync(Id == id)`）是 `UpdateOffsetAsync` 的直接范本；`transcript_segments` 唯一索引 `TaskId_1_StartTime_1`（`servers/common/Persistence/MongoDbInitializer.cs:972-979`）不受本需求影响。
- **transcript-api 三出口**：详情 `TranscriptTasksController.GetTask`（:141-160，片段映射 :157-159）；字幕 `TranscriptSubtitleController.DownloadSubtitle`（:26-76，:56 取任务、:62 取片段、:63 映射）；SSE `TranscriptSegmentStream.StreamAsync`（:25-117）——`seen` 去重键为原始 `StartTime`（:62）/`evt.Start`（:135），replay 写出 :67、实时事件写出 :140；**该类已注入 `TranscriptTaskRepository`**（:17-21），流内读取 `OffsetMs` 无需新增依赖；`ReplaySegmentsAsync` 为 `protected virtual`（:119），测试可重写。
- **映射与渲染**：`TranscriptMapping.ToSegmentDto`（`Contracts/TranscriptMapping.cs:42-43`）为 `new(segment.StartTime, segment.EndTime, segment.Text)`；`ToTaskDto`（:24-40）共 13 个字段、无 offset；`SubtitleRenderer.FormatSrtTimestamp`/`FormatAssTimestamp`（`Subtitles/SubtitleRenderer.cs:63-71/:74-82`）首行均已 `Math.Max(0, ms)`，负值钳 0 为既有行为无需改动。
- **api 代理**：`RecordingTranscriptProxyController.ForwardAsync`（:77-142）在 :85 处 `method == HttpMethod.Post || method == HttpMethod.Put` 即透传 `Request.Body`（`StreamContent` + Content-Type），PUT body 透传已支持；PUT 镜像路由先例存在（`PUT transcripts/models/{modelId}`，:73-75），路由模板统一 `Uri.EscapeDataString(taskId)` 转义。
- **web**：`TaskListTab.tsx` `mergeSegments`（:63-75，以 `segment.start` 为 Map key）；识别结果标题行 :379（`<h3>识别结果</h3>`），右侧 flex 容器 :380-414 含下载 SRT/ASS 链接；`getTranscriptTask` 详情拉取 :190、`openTranscriptStream` :208（cleanup :224-229，effect 依赖 `[selectedTaskId, refreshKey]`）；`SegmentAudioPlayer.tsx` :101 以 `segment.start/end` 调 `recordingAudioClipUrl`，置灰逻辑 :29（`task.file` 为空），播放按钮 :203；`client.ts` PUT 先例 `updateTranscriptModel`（:458-469），统一 `request<T>`（:39-73，非 ok 抛 `ApiError{status, serverMessage}`）。
- **契约**：`contracts/recording-transcript.openapi.yaml` `TranscriptTask` schema（:454-497，15 字段，无 offsetMs）、`TranscriptSegment`（:505-518，start 描述为「整场录播时间轴上的句首（毫秒，含续跑 offset）」）；端点列表确认无 offset 端点；`contracts/bilibili-crawler.openapi.yaml` transcripts 镜像路由齐备（PUT 先例 models/{modelId} :757-758）。
- **测试设施**：`TranscriptApiTests.cs`（778 行，Mongo+Redis fixture，:589 起为 SSE 测试——直接 `new TranscriptSegmentStream(...)` + `DefaultHttpContext` + `MemoryStream`，临时改静态 `HeartbeatInterval`，Redis 发布 `TranscriptSegmentEvent`）；`TranscriptSubtitleApiTests`（SRT/ASS golden）；`RecordingTranscriptProxyControllerTests.cs`（422 行，`StubHandler` 记录方法/路径/body/ContentType）；`TranscriptTaskRepositoryTests`（仓储 CRUD）。
- **偏差（唯一一处）**：提案所引 consumer 侧 `servers/recording-transcript-consumer/FunAsrProtocol.cs` 实际路径为 `servers/recording-transcript-consumer/Asr/FunAsrProtocol.cs`（:154-182 一致，落库时间 = ASR 返回 + 起始 offset，播放偏移不影响 consumer，结论不变）。

### 1.1 实施范围

- **做**：
  - `contracts/`：`recording-transcript.openapi.yaml` + PUT offset 端点 + `TranscriptTask.offsetMs` + `TranscriptSegment` 语义注释更新；`bilibili-crawler.openapi.yaml` + 代理镜像；`apps/web` `npm run codegen` 重新生成（勿手改）。
  - `servers/common`：`TranscriptTaskRecord` + `OffsetMs`；`TranscriptTaskRepository` + `UpdateOffsetAsync`。
  - `servers/recording-transcript-api`：`TranscriptTasksController` + PUT offset action、详情查询传入 `OffsetMs`；`TranscriptSubtitleController` 字幕导出传入 `OffsetMs`；`TranscriptMapping`（`ToTaskDto` + offsetMs、`ToSegmentDto` + offset 参数）；`TranscriptSegmentStream` 流建立时读一次偏移、replay/实时事件 +offset 写出。
  - `servers/api`：`RecordingTranscriptProxyController` + PUT offset 镜像路由。
  - `apps/web`：`client.ts` + `updateTranscriptTaskOffset`；`TaskListTab` 偏移设置入口 + 保存后重置刷新；`SegmentAudioPlayer` 试听起点/终点钳 0、整场出界置灰。
  - `tests/Z.Vtuber.Tests` 与 `apps/web` Vitest 测试（清单见 §3 Phase 1）；3 份 `AGENTS.md` 文档同步（common、recording-transcript-api、api、web 共 4 份）。
- **不做**：识别过程（consumer/ASR 链路）零改动；`transcript_segments` 集合与索引零改动；片段级/区间级偏移与非线性校正；偏移自动探测；recording 音频片段端点参数语义变更（试听钳制在前端完成）；无数据迁移、无新增 NuGet/npm 依赖。

### 1.2 现状基线

- **仓储更新范本**：`TranscriptTaskRepository.UpdatePositionAsync`（:80-86）。
- **端点范本**：`TranscriptTasksController`（`ObjectId.TryParse` 404、`ErrorResponse`）与 `TranscriptApiTests`；PUT 端点范本 `TranscriptModelsController` 更新模型。
- **SSE 测试范本**：`TranscriptApiTests` :589 起（含 `PublishesDuringReplayStream` 重写 `ReplaySegmentsAsync` 模式）。
- **代理范本**：`RecordingTranscriptProxyController` PUT models 镜像（:73-75）与 `RecordingTranscriptProxyControllerTests` `StubHandler`。
- **web 范本**：`client.ts` `updateTranscriptModel`（PUT）、`recordingAudioClipUrl`（URL 构造器）；`TaskListTab.test.tsx`、`SegmentAudioPlayer.test.tsx`、`client.test.ts`。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash、分支不复用）与 Conventional Commits：

- 本需求开发全程只使用需求总分支 `feat/transcript-playback-offset`：契约、实现、测试、文档全部直接在该分支提交，不切开发子分支（沿用前几期需求既定做法）。
- 需求总分支已推送 origin，需求总 PR 已存在（#132），提交照常推送更新分支。
- 提交信息使用 Conventional Commits：契约先行 `feat(contracts):`、测试先行 `test:`、实现 `feat:`、文档 `docs:`；测试与实现分开提交；scope 使用 `transcript`/`web` 等既有口径。

## 3. 实施阶段

按「契约先行（0）→ TDD 测试先行（红）→ 实现（绿）→ 收口验证」推进。Phase 0 串行先行；Phase 1/2 按四条线并行（线内串行）；Phase 3 串行收口。

### Phase 0: 契约先行（串行，全部实现的联调基线）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | `recording-transcript.openapi.yaml` + `PUT /api/transcripts/task/{taskId}/offset`：body `UpdateTranscriptTaskOffsetRequest`（`offsetMs` int64 必填，范围 ±86400000，超出 400 `{"error": "offsetMs out of range"}`）；200 返回更新后 `TranscriptTask`（含 `offsetMs`）；404 `{"error": "task not found"}`（taskId 非法或不存在）；`TranscriptTask` schema + `offsetMs`（int64，默认 0，说明三出口口径）；`TranscriptSegment.start/end` description 更新为「含任务播放偏移的展示时间（毫秒）= 整场录播时间轴（含续跑 offset）+ 任务播放偏移」 | `contracts/recording-transcript.openapi.yaml` | 契约自洽，与提案 §3.1 一致 |
| 0.2 | `bilibili-crawler.openapi.yaml` + 代理镜像 `PUT /api/transcripts/task/{taskId}/offset`（body 原样透传；200/400/404 透传；上游不可达 502 `{"error": "transcript service unavailable"}` 现状口径） | `contracts/bilibili-crawler.openapi.yaml` | 与 0.1 语义一致 |
| 0.3 | web codegen：`npm run codegen` 重新生成 `schema-recording-transcript.d.ts`、`schema.d.ts`（勿手改）；提交契约 + codegen 作为评审基线 | `apps/web/src/api/schema*.d.ts` | 生成物与契约一致；`npm run lint` 通过 |

### Phase 1: 测试先行（红）

**线 A：数据层 + PUT 端点（common + transcript-api）**

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | `TranscriptTaskRepositoryTests` 扩展：插入任务后 `UpdateOffsetAsync(id, 2500)` → 重读 `OffsetMs=2500` 且 `UpdatedAt` 刷新；负值/-2500 落库正确；不存在的 id → 返回 false；旧文档无 `OffsetMs` 字段（BSON 插入缺字段文档）→ 反序列化为 0 | `tests/Z.Vtuber.Tests/TranscriptTaskRepositoryTests.cs` | 红 |
| 1.2 | `TranscriptTaskOffsetApiTests`（新，Mongo+Redis fixture，沿用 `TranscriptApiTests` 模式）：合法正/负/零值 → 200 且响应 `offsetMs` 正确；`±86400001` → 400 `offsetMs out of range`；非法 taskId / 不存在 → 404；RUNNING 任务可设置；设置后详情 `segments[].start/end` = 原始 + offset；字幕导出（SRT/ASS golden）条目时间含偏移、负值条目钳 0；旧任务（无字段）行为与现状一致 | `tests/Z.Vtuber.Tests/TranscriptTaskOffsetApiTests.cs` | 红 |
| 1.3 | `TranscriptMappingTests`（新/扩展）：`ToSegmentDto` 偏移正/负/零（负值**不钳制**，保持单射）；`ToTaskDto` 输出 `offsetMs` | `tests/Z.Vtuber.Tests/TranscriptMappingTests.cs` | 红 |

**线 B：SSE 偏移（transcript-api）**

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.4 | SSE 偏移测试（扩展 `TranscriptApiTests` SSE 模式）：预置任务 `OffsetMs=2500` + 已入库片段 → 连接 SSE，replay 写出 `start/end` = 原始 + 2500；Redis 发布实时事件 → 写出同样 + 2500；去重不受偏移影响（原始 StartTime 键，重复事件不重发）；`OffsetMs=0` 行为与现状一致 | `tests/Z.Vtuber.Tests/TranscriptApiTests.cs` | 红 |

**线 C：api 代理镜像**

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.5 | `RecordingTranscriptProxyControllerTests` 扩展：PUT offset 镜像路由——方法/路径/body/Content-Type 透传断言（`StubHandler`）；200/400/404 透传；上游不可达 502 | `tests/Z.Vtuber.Tests/RecordingTranscriptProxyControllerTests.cs` | 红 |

**线 D：web（Vitest + RTL）**

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.6 | `client.test.ts` 扩展：`updateTranscriptTaskOffset(taskId, offsetMs)` PUT 路径/body 断言；400 时抛 `ApiError` 含服务端消息 | `apps/web/src/api/client.test.ts` | 红 |
| 1.7 | `TaskListTab.test.tsx` 扩展：识别结果标题行出现「播放偏移」入口；输入 2.5 保存 → PUT body `offsetMs=2500`；保存成功后重新 `getTranscriptTask` 拉全量并重建 EventSource（旧实例 close）；输入负值 `-1.5` → `offsetMs=-1500`；非法输入（非数字/超 ±24h）提示「偏移无效」且不发起请求；400 响应提示「偏移无效」并保持原值；当前生效值展示（offsetMs→秒回显） | `apps/web/src/pages/transcripts/TaskListTab.test.tsx` | 红 |
| 1.8 | `SegmentAudioPlayer.test.tsx` 扩展：偏移后负值片段试听 URL `startMs` 钳 0、`endMs = max(0, end)`；`segment.end <= 0` 播放按钮置灰 + 「音频暂不可用」 | `apps/web/src/pages/transcripts/SegmentAudioPlayer.test.tsx` | 红 |

### Phase 2: 实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | **数据层（common）**：① `TranscriptTaskRecord` + `public long OffsetMs { get; set; }`（默认 0，创建任务处不显式赋值）；② `TranscriptTaskRepository` + `UpdateOffsetAsync(ObjectId taskId, long offsetMs, CancellationToken ct = default)`：套 `UpdatePositionAsync` 模式（Set `OffsetMs` + `UpdatedAt`），按 `MatchedCount` 返回 bool；无需索引变更与回填 | `servers/common/Persistence/Entities/TranscriptTaskRecord.cs`、`servers/common/Persistence/TranscriptTaskRepository.cs` | 1.1 转绿 |
| 2.2 | **映射与三出口（transcript-api）**：① `TranscriptMapping.ToSegmentDto` 加 `long offsetMs = 0` 参数（`new(segment.StartTime + offsetMs, segment.EndTime + offsetMs, segment.Text)`，不钳制负值）；`ToTaskDto` + `offsetMs` 输出；② 详情 `GetTask`（:157-159）传 `task.OffsetMs`；③ 字幕 `DownloadSubtitle`（:63）传任务 `OffsetMs`（`SubtitleRenderer` 钳 0 既有不动）；④ `TranscriptSegmentStream.StreamAsync` 订阅建立后读一次任务 `OffsetMs`（复用已注入的 `TranscriptTaskRepository`），replay（:67）与实时事件（:140）写出时 +offset；`seen` 去重键保持原始值 | `servers/recording-transcript-api/Contracts/TranscriptMapping.cs`、`Controllers/TranscriptTasksController.cs`、`Controllers/TranscriptSubtitleController.cs`、`Sse/TranscriptSegmentStream.cs` | 1.2（详情/字幕部分）、1.3、1.4 转绿 |
| 2.3 | **PUT offset 端点（transcript-api）**：`TranscriptTasksController` + `[HttpPut("api/transcripts/task/{taskId}/offset")]`：`ObjectId.TryParse` 失败 → 404（既有口径）；`offsetMs` 超出 ±86400000 → 400 `{"error": "offsetMs out of range"}`；`UpdateOffsetAsync` 返回 false → 404；成功 → 重读任务返回 `ToTaskDto`（含 `offsetMs`），Info 日志 `transcript task offset updated: taskId={TaskId} offsetMs={OffsetMs}` | `servers/recording-transcript-api/Controllers/TranscriptTasksController.cs` | 1.2 全绿 |
| 2.4 | **api 代理镜像**：`RecordingTranscriptProxyController` + `[HttpPut("transcripts/task/{taskId}/offset")]` → `ForwardAsync(HttpMethod.Put, $"api/transcripts/task/{Uri.EscapeDataString(taskId)}/offset", ct)`；`ForwardAsync` 零改动 | `servers/api/Controllers/RecordingTranscriptProxyController.cs` | 1.5 转绿 |
| 2.5 | **web 偏移设置与刷新**：① `client.ts` + `updateTranscriptTaskOffset(taskId, offsetMs)`（PUT，`updateTranscriptModel` 同模式）；② `TaskListTab.tsx` 识别结果标题行（与「下载 SRT/ASS」并排）+「播放偏移」入口：内联输入框（单位秒，step 0.1，允许负值，展示当前生效值 `offsetMs/1000`），旁注「正值=识别提前需后移」；保存时 `Math.round(seconds * 1000)` 调 PUT，本地预校验范围 ±86400 秒与非数字 → 提示「偏移无效」不请求；400 → 提示「偏移无效」保持原值；③ 保存成功后：以响应更新当前 task → 清空 `mergeSegments` 的 Map 与片段列表 → 重新 `getTranscriptTask` 拉全量 → 关闭并重建 `openTranscriptStream`（利用既有 `refreshKey` effect 依赖重建） | `apps/web/src/api/client.ts`、`apps/web/src/pages/transcripts/TaskListTab.tsx` | 1.6、1.7 转绿 |
| 2.6 | **web 试听钳制**：`SegmentAudioPlayer.tsx` 播放前 `start = Math.max(0, segment.start)`、`end = Math.max(start, segment.end)`；`segment.end <= 0` 时该句播放按钮置灰 + title「音频暂不可用」（并入既有 `canPlay` 口径） | `apps/web/src/pages/transcripts/SegmentAudioPlayer.tsx` | 1.8 转绿 |
| 2.7 | codegen 一致性复核 + `npm run lint`/`npm run build`、`dotnet build` 全通过 | 全部 | 构建零告警 |

### Phase 3: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 3.1 | 全量测试与覆盖率：`dotnet test tests/Z.Vtuber.Tests` 全绿（含既有回归：SSE/任务链路/字幕导出/代理/仓储测试、`AliFunAsrOffsetTests` 等 ASR 测试不受影响的确认）；`apps/web` `npm run test` 全绿；`ToSegmentDto` 偏移/范围校验/试听钳制核心逻辑覆盖率 ≥ 80%（common-rules） | 全部通过 |
| 3.2 | 兼容性抽查：构造无 `OffsetMs` 字段的旧任务文档 → 详情/SSE/字幕/试听行为与现状一致（offset=0）；偏移不影响 consumer 落库（`Asr/FunAsrProtocol.cs` 零改动 diff 确认） | 断言/grep 无违规 |
| 3.3 | 文档同步：`servers/common/AGENTS.md`（`TranscriptTaskRecord.OffsetMs` 与 `UpdateOffsetAsync`）、`servers/recording-transcript-api/AGENTS.md`（API 面 + PUT offset 端点、三出口偏移口径、`TranscriptMapping`/SSE 描述）、`servers/api/AGENTS.md`（代理镜像路由）、`apps/web/AGENTS.md`（`TaskListTab` 偏移设置入口、`SegmentAudioPlayer` 钳制、`client.ts` 新封装）；本 Plan 与提案/PRD 链接互相有效 | 文档与实现一致 |
| 3.4 | 手工验证（z-aspire-orchestrator，可选但推荐）：本地 Aspire 拉起 → 任选识别任务设置 +2.5s → 片段时间即时按新口径展示、试听点哪句听哪句、下载 SRT 条目 +2.5s → 改 -1.5s 验证前移与负值钳 0 → 清零恢复原始口径 → 识别进行中任务设置偏移后新片段按新口径展示且无重复片段 | 手工验证记录 |

## 4. 并行策略

- **Phase 0 串行先行**（0.1→0.2→0.3）：契约是全部线的联调与 codegen 基线，必须最先合入。
- **Phase 1/2 四条线并行，线内串行**：
  - **线 A 数据层 + 端点**：1.1→1.2→1.3→2.1→2.2①②③→2.3（仓储 → 映射 → 端点）；
  - **线 B SSE**：1.4→2.2④（与线 A 共享 `TranscriptSegmentStream.cs` 单文件，若并行由同一人/同一提交序完成，避免映射签名改动与 SSE 改动交叉冲突）；
  - **线 C 代理**：1.5→2.4（与 A/B 零文件交集，可最先开工）；
  - **线 D web**：1.6→1.7→1.8→2.5→2.6（依赖 Phase 0 codegen；组件以 mock fetch/EventSource 测试驱动，不阻塞于后端联调，联调在 3.4 收口）。
- **文件冲突规避**：`TranscriptTasksController.cs` 同时承载 PUT 端点与详情偏移（线 A 内串行）；`TranscriptSegmentStream.cs` 归线 B；web 改动全部收敛线 D 单线。
- **依赖关系**：2.2/2.3 依赖 0.1 契约口径；2.4 依赖 0.2；2.5 依赖 0.3 codegen；3.x 全线完成后串行收口。
- 全部提交在需求总分支 `feat/transcript-playback-offset`。

## 5. 验收对照

| PRD 功能点 | 实现任务 | 验证方式 |
|-----------|---------|---------|
| 功能点 1：识别任务播放偏移设置（±值/0.1s 精度/持久/即时生效） | 0.1、2.1、2.3、2.4、2.5①② | 1.1、1.2、1.5、1.6、1.7 + 3.4 手工验证 |
| 功能点 2：偏移统一作用于展示与试听（含进行中任务新片段） | 2.2①②④、2.5③、2.6 | 1.2、1.3、1.4、1.7、1.8 + 3.4 手工验证 |
| 功能点 3：偏移作用于 SRT/ASS 字幕导出 | 0.1、2.2③ | 1.2（SRT/ASS golden）+ 3.4 手工验证 |
| 非功能：兼容（历史任务/无字段文档行为不变、不影响识别） | 2.1（默认 0）、consumer 零改动 | 1.1、1.2 兼容性用例 + 3.2 |
| 非功能：安全（沿用既有代理路径，不新增公开面） | 2.4（既有代理模式） | 1.5 + 3.1 既有回归 |
| 契约与质量门槛（codegen 一致/覆盖率 ≥ 80%/TDD） | 0.3、2.7、3.1 | `npm run codegen` diff、覆盖率报告、红-绿提交记录 |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| offset 变更后旧口径片段残留 | web `mergeSegments` 以 `segment.start` 为键，偏移变更后旧/新口径并存重复 | 2.5③ 保存成功后清空缓存 + 重拉详情 + 重建 SSE；1.7 断言锁死 |
| SSE 流存续期间 offset 变更不重读 | 已连接客户端短时间内仍是旧口径 | 提案决策（低频变更，web 保存后主动重连）；1.7 断言重建行为 |
| 映射层钳 0 破坏 `start` 唯一性 | 多句同钳到 0 会互相覆盖（web Map key） | 映射层不钳制（2.2①）；钳制只在字幕渲染（既有）与试听前端（2.6）；1.3 断言负值不钳 |
| `TranscriptTasksController.cs` 多任务并改冲突 | PUT 端点与详情偏移同文件 | 线 A 内串行（§4） |
| 前端秒↔毫秒换算误差 | 0.1s 精度须无损 | `Math.round(seconds * 1000)`（2.5②）；1.7 断言 2.5→2500、-1.5→-1500 |
| 依赖 | Mongo 旧文档缺字段反序列化默认 0（无迁移）；既有 `TranscriptTaskRepository` 注入（SSE 无需新增依赖）；`ForwardAsync` PUT 透传已支持；`SubtitleRenderer` 钳 0 既有 | 均已核实存在（§1.0），零新增外部依赖 |

## 7. 相关文档

- PRD：`docs/product/draft/20260927-transcript-playback-offset-prd.md`（v1.0）
- 技术提案：`docs/engineering/proposals/20260927-proposal-transcript-playback-offset.md`（v1.0）
- 前序 Plan：[[20260916-plan-recording-transcript]]、[[20260922-plan-transcript-display-optimization]]
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`docs/engineering/frontend-rules.md`
- 模块规范：`servers/common/AGENTS.md`、`servers/recording-transcript-api/AGENTS.md`、`servers/api/AGENTS.md`、`apps/web/AGENTS.md`
- 既有契约：`contracts/recording-transcript.openapi.yaml`、`contracts/bilibili-crawler.openapi.yaml`
