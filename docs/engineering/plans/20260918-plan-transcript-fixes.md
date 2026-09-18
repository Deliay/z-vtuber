---
description: 转写功能体验修复与任务恢复实现 Plan——基于 PRD v1.0 与技术提案（prd/transcript-fixes @0ea027a）：契约先行（recording 新增批量 file-stats 磁盘权威端点、transcript 契约 RecordingItem 补 anchorName/size 并新增 candidate-rooms 与 DELETE settings 端点、api 代理契约追加），common 仓储扩展（DistinctRoomsAsync/DeleteByRoomIdAsync/ListByModelAndStatusAsync），recording file-stats 实现，transcript-api 空场次过滤与聚合（recording 不可达降级不过滤），consumer 扫描器并发计数改「持锁 RUNNING」口径修复僵尸任务自我死锁并补专项测试，api 代理路由，web 场次/主播选择器模板文案、设置页选择器与删除、详情容器收起限高滚动，AppHost 配置接入
type: Permanent
---

# 转写功能体验修复与任务恢复 - 实现 Plan

**功能名称**: 转写功能体验修复与任务恢复
**关联 PRD**: `docs/product/draft/20260918-transcript-fixes-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20260918-proposal-transcript-fixes.md`
**Plan 版本**: v1.0
**创建日期**: 2026-09-18
**作者**: Z.Vtuber Workflow Agent
**feat-branch**: `prd/transcript-fixes`（需求总分支，已推送 origin，HEAD `0ea027a`；需求总 PR：<https://github.com/Deliay/z-vtuber/pull/123>）

## 1. 概述

本 Plan 基于 PRD v1.0 与技术提案编写，指导「转写功能体验修复与任务恢复」的编码实施。改动分六块：① 契约——`contracts/recording.openapi.yaml` 新增 `POST /api/recordings/file-stats` 批量磁盘权威端点；`contracts/recording-transcript.openapi.yaml` 的 `RecordingItem` 补 `anchorName`/`size`、新增 `GET /api/transcripts/candidate-rooms` 与 `DELETE /api/recordings/{roomId}/transcripts/settings`；`contracts/bilibili-crawler.openapi.yaml` 追加两条代理端点（先于实现）；② common——`RecordingRepository.DistinctRoomsAsync`、`TranscriptAutoEnqueueRepository.DeleteByRoomIdAsync`、`TranscriptTaskRepository.ListByModelAndStatusAsync` 三个仓储扩展；③ `servers/recording`——file-stats 端点实现（磁盘存在性 + 大小，复用下载接口路径穿越防护）；④ `servers/recording-transcript-api`——`RecordingFileStatsClient`（typed HttpClient）、录播查询空场次过滤与 `files` 收缩/`size` 求和/`anchorName` 聚合（recording 不可达降级不过滤）、candidate-rooms 与 DELETE settings 端点；⑤ `servers/recording-transcript-consumer`——`TranscriptTaskScanner.TryReserveConcurrencyAsync` 并发计数由「RUNNING 总数」改为「持锁 RUNNING 数」，修复僵尸 RUNNING 自我死锁并补扫描器专项测试；⑥ `servers/api` 代理两条新路由 + `apps/web` 选择器模板文案/设置页选择器与删除/详情容器收起限高滚动 + AppHost 配置接入。实施全程遵循 `docs/engineering/common-rules.md`（TDD、契约式开发、核心逻辑覆盖率 ≥ 80%）、`backend-rules.md`、`frontend-rules.md` 及各子项目 AGENTS.md。

### 1.0 上游核实结论（代码现状复查）

对需求总分支 `prd/transcript-fixes`（HEAD `0ea027a`）代码核实，提案技术路径**可行**，关键事实均已复验：

- **Q3 根因属实**：`TranscriptTaskScanner.TryReserveConcurrencyAsync`（`servers/recording-transcript-consumer/Tasks/TranscriptTaskScanner.cs:139-153`）以 `CountByModelAndStatusAsync(model.Id, RUNNING)`（`servers/common/Persistence/TranscriptTaskRepository.cs:88-90`）计数，把待接管的僵尸 RUNNING 自身计入 `MaxConcurrency=1` 额度 → 每轮 `ScanOnceAsync` 在 :73 被挡回，形成自我死锁；`ListScannableAsync`（:52-61）返回 `PENDING + RUNNING` 正确；锁检查（`TranscriptTaskScanner.cs:59-64`，`IsHeldAsync`）正确。
- **retry 同根受阻**：`TranscriptTasksController.cs:156` retry 置 RUNNING 不持锁，同样被并发门控挡回；扫描器修复后该路径自然解锁，retry 端点本身零改动。
- **扫描器内存预留释放时机注意**：`TranscriptTaskScanner.cs:98-100` 注释表明置 RUNNING 成功后即 `ReleaseConcurrency`，因 DB 的 RUNNING 计数会接管额度统计。改持锁口径后语义依然成立——任务持锁后即被「持锁 RUNNING」计数覆盖，释放时机保持不变（任务 2.4 落实）。
- **Q2 现状属实**：`RecordingsArchiveController.ListRecordings`（`servers/recording-transcript-api/Controllers/RecordingsArchiveController.cs:21-31`）仅查 Mongo 无磁盘校验；该控制器现有构造注入 `options/recordings/tasks/models/autoEnqueue`，新增 `RecordingFileStatsClient` 与 `AccountBasicInfoRepository` 依赖即可。
- **anchorName 先例属实**：`TranscriptSettingsController.cs:19-29` 已用 `AccountBasicInfoRepository.GetLatestByMidsAsync` + `TranscriptMapping.ExtractAnchorName`；`formatBytes` 先例在 `apps/web/src/pages/recording/RecordingStatusPage.tsx:13-30`（本文件私有函数，web 侧需提取共用或就近复刻）。
- **锁接口齐备**：`ITranscriptTaskLockProvider.IsHeldAsync(ObjectId, ct)` 已存在（`servers/recording-transcript-consumer/Tasks/TranscriptTaskLock.cs:12`），扫描器修复无需新增锁能力。
- **codegen 就绪**：`apps/web/package.json:12` codegen 已串行生成 6 份 schema（含 recording 与 recording-transcript），契约变更后 `npm run codegen` 即可。
- **测试设施就绪**：`tests/Z.Vtuber.Tests` 已有 Testcontainers MongoDB + Redis fixture 与 `Fakes.cs`，本需求**无需新增测试项目引用**，仅需新增测试文件；扫描器当前确无专项测试（tests/ 下无 Scanner 测试文件）。

### 1.1 实施范围

- **做**：
  - 契约：`contracts/recording.openapi.yaml` + `POST /api/recordings/file-stats`；`contracts/recording-transcript.openapi.yaml`：`RecordingItem` +`anchorName`/`size`、+`GET /api/transcripts/candidate-rooms`、+`DELETE /api/recordings/{roomId}/transcripts/settings`、`GET /api/recordings` 与 `GET /api/recordings/{roomId}/transcripts` description 标注空场次过滤行为；`contracts/bilibili-crawler.openapi.yaml` + 两条代理端点。
  - common：三处仓储扩展（`DistinctRoomsAsync`/`DeleteByRoomIdAsync`/`ListByModelAndStatusAsync`）。
  - recording：file-stats 端点（`{StorageDir}/{roomId}/{fileName}` 存在性 + 长度，路径穿越防护，items 上限 100 超限 400）。
  - transcript-api：`RecordingFileStatsClient` + `RecordingService:BaseUrl` 配置；`GET /api/recordings` 与 `GET /api/recordings/{roomId}/transcripts` 空场次过滤 + `files` 收缩 + `size` 求和 + `anchorName` 聚合 + 降级；candidate-rooms 端点；DELETE settings 端点。
  - consumer：扫描器并发计数改「持锁 RUNNING」口径（Redis 异常按「持有」保守计数）。
  - api：`RecordingTranscriptProxyController` 追加两条转发路由。
  - web：codegen 重新生成；`client.ts` 新增 `getTranscriptCandidateRooms`/`deleteTranscriptSettings`；`TaskListTab` 场次选项模板文案 + 主播选择器名称 + 详情容器收起/限高滚动/底部跟随；`SettingsTab` 房间号输入改候选主播下拉 + 删除按钮（二次确认）。
  - 工程：AppHost transcript-api 追加 `RecordingService:BaseUrl`；各子项目 AGENTS.md 同步。
  - 测试：全部新增/改写测试，核心逻辑覆盖率 ≥ 80%，无回归。
- **不做**：存量 `recordings.Files` 数据清洗；enqueue 前置文件校验；`TranscriptTask` 补 anchorName；crawler/cookie/notifier 零改动；无新服务、无 Dockerfile/CI 新增、无数据迁移。

### 1.2 现状基线

- **anchorName 补全范本**：`TranscriptSettingsController.cs:19-29`（`GetLatestByMidsAsync` + `ExtractAnchorName`）。
- **文件物理元信息范本**：`RecordingController` 的 `GET /api/{roomId}/recordings`（`servers/recording/Controllers/RecordingController.cs:24-68`）磁盘枚举附 `size`；路径穿越防护先例在同文件下载端点（:70-99）。
- **typed HttpClient 范本**：consumer `RecordingServiceClient`（`RecordingService:BaseUrl` 配置 + Program 注册）。
- **代理范本**：`RecordingTranscriptProxyController` 既有转发（`ResponseHeadersRead` + `CopyToAsync`、不可达 502）。
- **前端共用工具**：`formatBytes` 提取为 `apps/web/src/` 下共用模块（如 `utils/format.ts`）供 RecordingStatusPage 与 TaskListTab 共用，或就近在 TaskListTab 实现——实现时按 frontend-rules 取舍，避免重复。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash）与 Conventional Commits：

- 本需求开发全程只使用需求总分支 `prd/transcript-fixes`：契约、实现、测试、文档全部直接在该分支提交，不切开发子分支（沿用近两期需求既定做法）。
- 需求总分支已推送 origin，需求总 PR 已存在（#123），提交照常推送更新分支。
- 提交信息使用 Conventional Commits：契约 `feat:`、测试先行 `test:`、实现 `feat:`/`fix:`（扫描器修复用 `fix:`）、工程接入 `chore:`、文档 `docs:`；测试与实现分开提交。
- 并行 subagent 按子项目划分（recording / transcript-api / consumer / api / apps-web），共享文件（common 仓储、契约）归入主串行线先行，避免并行冲突（见第 4 节）。

## 3. 实施阶段

按「契约先行 → TDD 红-绿 → 工程接入 → 收口验证」推进。Phase 0 串行先行；Phase 1 全部测试按子项目并行；Phase 2 中 2.1（common）串行先行，随后 recording / transcript-api / consumer / api / web 多线并行；Phase 3 与 Phase 2 可部分并行；Phase 4 串行收口。

### Phase 0: 契约先行

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | `contracts/recording.openapi.yaml` 新增 `POST /api/recordings/file-stats`：请求 body `{items: [{roomId: int64, files: string[]}]}`（items 总量上限 100，超限 400）；响应 200 `{items: [{roomId, files: [{name, exists: boolean, size: int64|null}]}]}`；description 注明磁盘权威、非法文件名按 `exists=false` 处理 | `contracts/recording.openapi.yaml` | `npx openapi-typescript` 可成功生成类型；与提案 §3.1 逐字段一致 |
| 0.2 | `contracts/recording-transcript.openapi.yaml` 变更：① `RecordingItem` +`anchorName`（string, nullable）+`size`（int64, nullable，磁盘实际存在文件总字节，recording 不可达为 null）；② `GET /api/recordings` 与 `GET /api/recordings/{roomId}/transcripts` description 标注「所有文件均不存在的空场次被过滤、返回 files 收缩为存在子集」；③ 新增 `GET /api/transcripts/candidate-rooms`（200 `{items: [{roomId, mid, anchorName: string|null}]}`，按 roomId 升序）；④ 新增 `DELETE /api/recordings/{roomId}/transcripts/settings`（204 删除成功 / 404 不存在；description 注明不影响已存在任务） | `contracts/recording-transcript.openapi.yaml` | codegen 通过；与提案 §3.2 逐字段一致 |
| 0.3 | `contracts/bilibili-crawler.openapi.yaml` 追加 `GET /api/transcripts/candidate-rooms` 与 `DELETE /api/recordings/{roomId}/transcripts/settings` 同路径代理端点 | `contracts/bilibili-crawler.openapi.yaml` | codegen 通过；路径与 0.2 一致 |

### Phase 1: 测试先行（红）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | `TranscriptTaskScannerTests`（Testcontainers MongoDB + Redis 集成；扫描器本次新增测试覆盖重点）：① 僵尸接管——`maxConcurrency=1` 模型 + RUNNING 无锁任务 → `ScanOnceAsync` 后任务被接管（抢锁成功、runner 被调用，可用假 runner 记录调用）；② 僵尸不占额度——同模型僵尸 RUNNING 在场时 PENDING 任务正常被领取；③ 持锁仍占额度——RUNNING 且锁 key 存在（模拟他节点在跑）→ 不接管且占额度，同模型 PENDING 被挡；④ 多僵尸不饿死——同模型多个僵尸 RUNNING 全部被接管（额度允许范围内），PENDING 不被永久饿死；⑤ retry 解锁——FAILED 置 RUNNING（无锁，模拟 retry 端点行为）后可被接管 | `tests/Z.Vtuber.Tests/TranscriptTaskScannerTests.cs`（新） | 红 |
| 1.2 | `RecordingsArchiveFilterTests`（transcript-api 集成，假 `RecordingFileStatsClient` 或假 HTTP handler）：① 所有文件 `exists=false` 的场次被过滤不出现在 `GET /api/recordings`；② 部分文件存在 → `files` 收缩为存在子集、`size` 为存在文件大小求和；③ `anchorName` 按 mid 批量补全、资料缺失为 null；④ recording 不可达/调用失败 → 记 Warn 降级返回未过滤原始列表（`size=null`），接口不 500；⑤ `GET /api/recordings/{roomId}/transcripts` 同样过滤空场次 | `tests/Z.Vtuber.Tests/RecordingsArchiveFilterTests.cs`（新） | 红 |
| 1.3 | `CandidateRoomsTests`：① `recordings` 聚合 distinct roomId + 最新 mid 去重；② anchorName 补全与缺失兜底（null）；③ 按 roomId 升序；④ 无录播时返回空 items | `tests/Z.Vtuber.Tests/CandidateRoomsTests.cs`（新） | 红 |
| 1.4 | `TranscriptSettingsDeleteTests`：① 存在设置 → DELETE 204 且 `transcript_auto_enqueue` 记录物理消失；② 不存在 → 404；③ 删除后该主播已存在任务不受影响（任务记录保持原状） | `tests/Z.Vtuber.Tests/TranscriptSettingsDeleteTests.cs`（新） | 红 |
| 1.5 | `RecordingFileStatsTests`（recording，临时目录预置文件）：① 存在文件返回 `exists=true` + 实际字节数；② 不存在文件/roomId 目录不存在返回 `exists=false`、`size=null`；③ 路径穿越文件名（含 `..`、分隔符）按 `exists=false` 处理；④ items 超上限 400 | `tests/Z.Vtuber.Tests/RecordingFileStatsTests.cs`（新） | 红 |
| 1.6 | api 代理测试（改写既有 `RecordingTranscriptProxyControllerTests`）：① `GET /api/transcripts/candidate-rooms` 转发至 transcript-api；② `DELETE /api/recordings/{roomId}/transcripts/settings` 转发且透传 204/404；③ 上游不可达 502 | `tests/Z.Vtuber.Tests/RecordingTranscriptProxyControllerTests.cs`（改） | 红 |
| 1.7 | 前端测试（Vitest + RTL，改写 `TaskListTab.test.tsx`/`SettingsTab.test.tsx`/`client.test.ts`）：① 场次选项严格按模板 `[{anchorName ?? roomId}][{formatBytes(size)}] {title} - {yyyy-mm-dd-HH-mm} - {endedAt ? 同格式 : '进行中'}` 渲染；② 主播筛选选择器 label 为 `{anchorName}（{roomId}）`、anchorName 缺失兜底 roomId；③ SettingsTab 添加自动识别为候选主播下拉、候选为空禁用提交并提示；④ 删除按钮 `confirm` 二次确认后调用 `deleteTranscriptSettings` 并刷新列表；⑤ 详情区收起按钮可完全收起、segments 容器有 maxHeight + overflowY auto、进行中追加内容且滚动在底部时自动跟随 | `apps/web/src/pages/transcripts/*.test.tsx`、`apps/web/src/api/client.test.ts`（改） | `npm run test` 红 |

### Phase 2: 实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | common 仓储扩展（串行先行）：① `RecordingRepository.DistinctRoomsAsync()`——聚合 distinct `RoomId` + 各房间最新场次 `Mid`；② `TranscriptAutoEnqueueRepository.DeleteByRoomIdAsync(roomId)`——物理删除，返回是否删到；③ `TranscriptTaskRepository.ListByModelAndStatusAsync(modelId, status)`——按模型 + 状态列举（供扫描器持锁判定） | `servers/common/Persistence/RecordingRepository.cs`、`TranscriptAutoEnqueueRepository.cs`、`TranscriptTaskRepository.cs` | 仓储方法随对应测试转绿；既有测试无回归 |
| 2.2 | recording file-stats 端点：`RecordingController` 新增 `POST /api/recordings/file-stats`——按 `{StorageDir}/{roomId}/{fileName}` 逐个 `File.Exists`/`FileInfo.Length`；文件名规范化后须仍落在 `{StorageDir}/{roomId}` 内（复用下载端点 :70-99 防护逻辑），非法按 `exists=false`；roomId 目录不存在全部 `exists=false`；items 上限 100 超限 400 | `servers/recording/Controllers/RecordingController.cs` | 1.5 转绿 |
| 2.3 | transcript-api 改造：① `Configuration/` 新增 recording 服务配置（节 `RecordingService:BaseUrl`，默认 `http://recording/`，照抄 consumer 同款）；② `Clients/RecordingFileStatsClient.cs` 新增 typed HttpClient（入参 `(roomId, files)[]`，单次请求聚合整页，无逐房间循环）；③ `Program.cs` 注册；④ `RecordingsArchiveController`——`ListRecordings`/`GetRoomTranscripts` 取一页后一次调用 file-stats，交集为空剔除场次、非空收缩 `files` 并求和 `size`，按本页 mid 批量补 `anchorName`（settings 先例），调用失败记 Warn 降级返回未过滤列表（`size=null`）；⑤ 新增 `GET /api/transcripts/candidate-rooms`（`DistinctRoomsAsync` + anchorName 补全，roomId 升序）；⑥ 新增 `DELETE /api/recordings/{roomId}/transcripts/settings`（`DeleteByRoomIdAsync`，204/404）；⑦ `Contracts/Dtos.cs`/`TranscriptMapping.cs` 同步 | `servers/recording-transcript-api/`（`Configuration/`、`Clients/`、`Controllers/RecordingsArchiveController.cs`、`Contracts/`、`Program.cs`） | 1.2、1.3、1.4 转绿 |
| 2.4 | consumer 扫描器修复（Q3 核心）：`TranscriptTaskScanner.TryReserveConcurrencyAsync` 计数口径由「该模型 RUNNING 总数」改为「该模型持锁 RUNNING 数」——`ListByModelAndStatusAsync(modelId, RUNNING)` 逐个 `IsHeldAsync`（Redis 异常按「持有」保守计数，与锁实现既有语义一致），`heldCount + inFlight >= Math.Max(1, model.MaxConcurrency)` 才拒绝；置 RUNNING + 持锁后的内存 `inFlight` 释放时机保持现状（:98-100，由持锁计数接管额度） | `servers/recording-transcript-consumer/Tasks/TranscriptTaskScanner.cs` | 1.1 全绿；既有测试无回归 |
| 2.5 | api 代理：`RecordingTranscriptProxyController` 追加 `GET /api/transcripts/candidate-rooms` 与 `DELETE /api/recordings/{roomId}/transcripts/settings` 转发（沿用既有流式转发模式，DELETE 透传 204/404） | `servers/api/Controllers/RecordingTranscriptProxyController.cs` | 1.6 转绿 |
| 2.6 | web 改造（仅依赖 Phase 0 契约，与后端并行）：① `npm run codegen` 重新生成 `schema-recording.d.ts`/`schema-recording-transcript.d.ts`/`schema.d.ts`（勿手改）；② `client.ts` 新增 `getTranscriptCandidateRooms()`/`deleteTranscriptSettings(roomId)`；③ `TaskListTab.tsx`——场次 option 按模板渲染（时间 `yyyy-mm-dd-HH-mm`、进行中兜底、anchorName 缺失兜底 roomId）；主播筛选选择器 label `{anchorName}（{roomId}）`；详情区标题栏加「收起」按钮（清空选中、关闭 EventSource）、segments 包 `maxHeight`（如 24rem）+ `overflowY: 'auto'` 容器、实时追加时滚动在底部附近自动滚到底；④ `SettingsTab.tsx`——房间号输入框替换为候选主播 `<select>`（数据源 candidate-rooms，展示主播名称，候选为空禁用提交并提示）；操作列加「删除」按钮，`window.confirm` 二次确认后删除并刷新；⑤ `formatBytes` 提取共用或就近实现（§1.2） | `apps/web/src/api/client.ts`、`apps/web/src/pages/transcripts/TaskListTab.tsx`、`SettingsTab.tsx`、`apps/web/package.json`（无需改，codegen 已含两契约） | 1.7 转绿；`npm run lint`/`npm run build` 通过 |

### Phase 3: 工程接入

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 3.1 | Aspire 编排：AppHost 中 transcript-api 追加 `WithReference(recording)` + `WithEnvironment("RecordingService:BaseUrl", ...)`（consumer 同款先例）；生产部署说明补 transcript-api 新配置项（PR #123 描述更新） | `infra/apphost/AppHost.cs` | Aspire 一键拉起，transcript-api 可解析 recording 地址 |

### Phase 4: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 4.1 | `dotnet test tests/Z.Vtuber.Tests` 全绿（含既有回归）；核心逻辑（扫描器并发口径/过滤聚合/file-stats/删除设置）覆盖率 ≥ 80%；`npm run test`/`lint`/`build` 全绿；grep 确认契约端点与实现路由一致、契约 description 与实现行为一致 | 全部通过 |
| 4.2 | 编排手工验证（z-aspire-orchestrator）：本地 Aspire 拉起全量 → 手动发起识别的场次选项按模板展示（主播名/大小/起止时间、进行中兜底）→ 主播筛选显示名称 → 设置页从候选主播下拉添加自动识别、删除设置（二次确认）且不影响已有任务 → 详情可收起、长内容限高滚动 → 停掉 recording 验证列表降级可用 → 恢复后空录播场次被过滤 → 杀掉 consumer 容器，RUNNING 任务 1 分钟内被接管续跑、segments 不重复不丢失 → FAILED 任务重试正常恢复 | 手工验证记录 |
| 4.3 | AGENTS.md 同步：`servers/recording/AGENTS.md`（+file-stats 端点）、`servers/recording-transcript-api/AGENTS.md`（RecordingFileStatsClient/新配置/新端点/过滤行为）、`servers/recording-transcript-consumer/AGENTS.md`（扫描器持锁口径）、`servers/common/AGENTS.md`（三处仓储扩展）、`servers/api/AGENTS.md`（+两条代理路由）、`apps/web/AGENTS.md`（Transcripts 页描述更新：模板文案/候选主播/删除/详情容器） | 文档与实现一致 |

## 4. 并行策略

- Phase 0（契约 0.1-0.3）串行先行——transcript-api、api 代理、前端 codegen 均依赖契约。
- Phase 1：后端测试（1.1-1.6）与前端测试（1.7）互不依赖，可按子项目拆 subagent 并行；1.1（扫描器）与 1.2-1.4（transcript-api）依赖 2.1 的 common 仓储扩展的**测试先行**部分可同步编写（测试只依赖接口签名，签名以提案 §3.4 为准）。
- Phase 2：2.1（common）串行先行——consumer 与 transcript-api 均依赖新仓储方法；随后 recording 线（2.2）、transcript-api 线（2.3）、consumer 线（2.4）、api 线（2.5）、web 线（2.6）分属不同子项目，按 z-coding 规范拆 subagent 并行，各 subagent 负责「实现 → 提交 → 测试修复 → 提交」闭环。
- Phase 3（3.1）与 Phase 2 互不依赖可并行。
- Phase 4 串行收口。全部提交在需求总分支 `prd/transcript-fixes`。

## 5. 验收对照

| PRD 功能点 | 实现任务 | 验证方式 |
|-----------|---------|---------|
| 功能点 1：场次选项信息补全 | 0.1、0.2①、2.2、2.3④、2.6③ | 1.2②③、1.5、1.7① + 手工验证（4.2） |
| 功能点 2：主播选择显示名称 | 0.2①、2.3④、2.6③ | 1.2③、1.7② + 手工验证（4.2） |
| 功能点 3：自动识别选择主播 + 删除 | 0.2③④、0.3、2.1①②、2.3⑤⑥、2.5、2.6②④ | 1.3、1.4、1.6、1.7③④ + 手工验证（4.2） |
| 功能点 4：详情收起与限高滚动 | 2.6③ | 1.7⑤ + 手工验证（4.2） |
| 功能点 5：无文件空录播过滤 | 0.1、0.2②、2.2、2.3④ | 1.2①②④⑤、1.5 + 手工验证（4.2） |
| 功能点 6：中断任务自动恢复 | 2.1③、2.4 | 1.1①-⑤ + 杀容器接管手工验证（4.2） |
| 非功能：覆盖率 ≥ 80%、全量测试绿、recording 不可达降级可用 | 4.1、2.3④ | `dotnet test` / `npm run test` / 1.2④ |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| recording 不可达影响查询 | transcript-api 新增对 recording 的只读依赖，故障可能波及列表接口 | 调用失败记 Warn 降级返回未过滤原始列表（2.3④），绝不让列表 500；1.2④ 锁定降级语义 |
| 每轮扫描新增 Redis 检查 | 持锁口径需对同模型 RUNNING 任务逐个 `IsHeldAsync` | 模型 `MaxConcurrency` 很小（默认 1），数量级可忽略；Redis 异常按「持有」保守计数不放大执行（2.4） |
| 扫描器行为回归 | 并发门控是任务调度核心，改动影响所有任务领取路径 | TDD 先行补专项测试（1.1 五用例覆盖接管/额度/饿死/retry），修复随测试落地（2.4） |
| file-stats 路径安全 | 端点接受文件名参数，存在路径穿越面 | 复用下载端点防护先例，非法文件名按 `exists=false`（2.2）；1.5③ 锁定 |
| 查询性能 | 每页 recordings 增加一次 file-stats 往返 | 单次请求聚合整页无 N+1（2.3②）；PRD 要求秒级，本地磁盘 `File.Exists` 开销极小 |
| 共享文件并行冲突 | 契约与 common 仓储为多线共同依赖 | 全部归入主串行线先行（Phase 0/2.1），并行线只动各自子项目（第 4 节） |
| 依赖 | Testcontainers（MongoDB + Redis）、openapi-typescript、ffmpeg（consumer 既有）、recording 服务内网可达 | 设施均已就绪（§1.0）；recording 不可达有降级兜底 |

## 7. 相关文档

- PRD：`docs/product/draft/20260918-transcript-fixes-prd.md`（v1.0）
- 技术提案：`docs/engineering/proposals/20260918-proposal-transcript-fixes.md`
- 契约：`contracts/recording.openapi.yaml`、`contracts/recording-transcript.openapi.yaml`、`contracts/bilibili-crawler.openapi.yaml`
- 前序 Plan：[[20260916-plan-recording-transcript]]（转写功能）、[[20260917-plan-transcript-model-context]]、[[20260917-plan-transcript-model-isdefault-fix]]
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`docs/engineering/frontend-rules.md`、`servers/common/AGENTS.md`、`servers/recording/AGENTS.md`、`servers/recording-transcript-api/AGENTS.md`、`servers/recording-transcript-consumer/AGENTS.md`、`servers/api/AGENTS.md`、`apps/web/AGENTS.md`
