---
description: AI训练模块「数据集工具优化」实现 Plan——基于 PRD v1.0 与技术提案的契约先行与任务分解：四条实施线（common 数据层 3 实体/3 仓储 + training 服务 AI 客户端与 Providers/Models/重新识别/情绪任务端点 + api 代理镜像 14 路由 + web 模型设置 Tab/重新识别对话框/情绪任务队列/展示类型列显隐），情绪任务「周期扫描 + Redis 抢锁」后台消费写入条目 Extensions.emotion；录音片段端点要求 startMs/endMs 必填，音频取数 endMs 兜底口径与情绪识别输入口径在本 Plan 收敛
type: Permanent
---

# AI 训练 - 数据集工具优化（模型设置 / 重新识别 / 情绪识别 / 列表展示） - 实现 Plan

**功能名称**: 训练 - 数据集工具优化
**关联 PRD**: `docs/product/draft/20260928-training-dataset-emotion-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20260928-proposal-training-dataset-emotion.md`（v1.0）
**Plan 版本**: v1.0
**创建日期**: 2026-09-28
**作者**: Workflow Agent（代起草）
**feat-branch**: `feat/training-dataset-emotion`（需求总分支，已推送 origin；需求总 PR：<https://github.com/Deliay/z-vtuber/pull/144>）

> v1.1 返工说明（2026-09-29，PR #146 代码 Review 打回）：修复 4 项 P2——① 重新识别保存携带内联草稿（`apps/web` `ReRecognizeDialog`/`DatasetItemList`）；② 情绪任务补 `model.Enabled` 校验（`TrainingItemToolsController.CreateEmotionTasks` + `EmotionTaskRunner`）；③ 任务状态机并发原子（`TrainingEmotionTaskRepository.TryInsertActiveAsync`/`RetryFailedAsync`/`UpdateRunningAsync`、`TrainingMongoSetup` 加 `ItemId_1_Active` 部分唯一索引、`TrainingEmotionTasksController.Retry` CAS + 409）；④ `EmotionTaskLock.RenewAsync` 异常兜底 + 续期循环不终止。并随返工处理 P3/P4（错误码口径统一、失败原因白名单、条目缺失 FAILED、防重复提交、轮询防覆盖、LLM 禁自动重定向）。契约同步（emotion-tasks 400 `model disabled`、retry 409 `active task exists`）与 AGENTS.md 已更新。

## 1. 概述

本 Plan 基于 PRD v1.0 与技术提案 v1.0 编写，指导「AI 训练 - 数据集工具优化」的编码实施。改动分四条线：① 数据层——`servers/common/training` 新增 `TrainingProviderRecord`/`TrainingModelRecord`/`TrainingEmotionTaskRecord` 三实体、三仓储、三集合常量与索引，`TrainingDatasetItemRepository` 增 `SetEmotionAsync`（`$set Extensions.emotion`，零表结构变更）；② training 服务——`Ai/`（`IChatModelClient` + OpenAI-like/Claude-like 两适配器 + JSON 输出解析 + 识别/情绪 prompt）、`Clients/RecordingAudioClient`（服务端取音频转 base64）、`Emotion/`（任务锁 + 扫描器 + Runner，**周期扫描 + Redis 抢锁**后台消费，不新建服务）、4 组控制器共 14 端点；③ api 代理——`/api/training/**` 14 端点镜像（`ForwardAsync` 既有透传模式）；④ web——「模型设置」Tab（Providers/Models）、重新识别对话框、情绪识别提交与「任务队列」区块、「展示类型」列显隐（音频信息默认隐藏 / 情绪信息默认展示）。转写链路（consumer/ASR/transcript 端点）与 recording 音频片段端点零改动。实施全程遵循 `docs/engineering/common-rules.md`（契约先行、TDD、核心逻辑覆盖率 ≥ 80%）、`backend-rules.md`、`frontend-rules.md`、z-coding 开发流程与各服务 `AGENTS.md`。

### 1.0 上游核实结论（代码现状复查）与偏差

对需求总分支 `feat/training-dataset-emotion` 代码逐项核实，提案技术路径**可行**，引用与现状描述属实；下列核实要点在对应任务中直接使用：

- **情绪承载位**：`TrainingDatasetItemRecord`（`servers/common/training/Entities/TrainingDatasetItemRecord.cs`）含 `Extensions`（`BsonDocument?`）预留字段，注释即「情感识别、二次 ASR 等加工结果按键名挂接」——情绪结果落 `Extensions.emotion` 无需改表结构；DTO 映射 `TrainingMapping.ToItemDto` 已把 `Extensions` 透出为 `extensions`（`JsonElement?`）。
- **集合与索引范式**：`TrainingCollectionNames`（`training_datasets`/`training_dataset_items`）、`TrainingMongoSetup.EnsureIndexesAsync`（幂等 `CreateOneAsync` + 命名索引）；新增 3 集合沿用同一模式。`CollectionNamesEquivalenceTests` 锁定集合名常量值（新增常量需补断言），`ProjectReferenceGuardTests` 引用矩阵 `training → [core, training, transcript]`（Redis 用 `Aspire.StackExchange.Redis` 包引用，**不改矩阵**）。
- **任务锁范式**：`servers/recording-transcript-consumer/Tasks/TranscriptTaskLock.cs`（`RedisTranscriptTaskLockProvider`：key `transcript:task-lock:{taskId}`，SET NX PX + 周期续期 + Lua token 比对释放 + `CheckOwnershipAsync` fencing，Redis 抖动 `IsHeldAsync` **保守按持有**）与 `TranscriptTaskScanner.cs`（`BackgroundService` 周期扫描 `PENDING` 或 `RUNNING` 无锁任务、并发门控、异常不中断循环）语义可逐条仿写；Core 的 `IDistributedLockProvider`（`servers/common/core/Locking/`）为短临界区语义（无续期、无失锁判定），不适合秒级 LLM 调用（提案决策 4 采用 A，已核实理由成立）。
- **音频取数现状（偏差 1 落点）**：音频片段端点 `GET api/{roomId:long}/recordings/{fileName}/audio`（`servers/recording/Controllers/RecordingController.cs:129`）**要求 `startMs` 与 `endMs` 必填**（缺任一 → 400 `startMs and endMs are required`），且区间 ≤ `RecordingOptions.AudioClipMaxDurationMs`（默认 300000 = 5 分钟）；前端 `trainingAudioClipUrl`（`apps/web/src/api/client.ts:638`，正则 `^b\/live\/(\d+)\/recordings\/(.+)$`）对 `endMs` 空已用 `startMs + 300000` 兜底。
- **web 现状**：`DatasetsPage.tsx` 为单 Tab（`type TabKey = 'audio'`、`TABS`、`role="tablist"`），扩 Tab 是最小改动；`DatasetItemList.tsx` 为 `table-layout:fixed` + `colgroup` 9 列（勾选/音频类型/音频来源/音频地址/音频标识/开始时间/结束时间/文本/操作），编辑态按钮组为「保存」「取消」（`saveEdit`/`cancelEdit`），展开行 `colSpan=9`，分组切换 `<select aria-label="分组方式">` 在工具栏——「重新识别」挂编辑态、「情绪识别」挂工具栏、「展示类型」勾选挂分组切换之前，均在既有骨架上扩展。
- **契约与代理现状**：`contracts/training.openapi.yaml` 438 行 8 端点（含 `/health`），`TrainingDatasetItem` 已含 `extensions`；`TrainingProxyController`（`servers/api/Controllers/`）8 个 action 走 `ForwardAsync`（query 透传、POST/PUT body 透传、状态码与响应体透传、502 `training service unavailable`）；`apps/web/package.json` `codegen` 已串联 7 契约（含 training）。
- **编排与部署现状**：`infra/apphost/AppHost.cs` 中 training 现仅 `.WithReference(mongo)` + `TranscriptService__BaseUrl`，`messaging`(Redis) 资源已存在（api/cookie/crawler/recording 等均引用）；`.github/workflows/build-training-image.yml` paths 已含 `servers/training/**` 与 `servers/common/training/**`（提案 §9.2 属实，无需调整）。
- **测试设施**：`tests/Z.Vtuber.Tests` 为控制器直测 + `MongoDbFixture`/`RedisFixture`（Testcontainers）范式（范本 `TranscriptApiTests`、`TranscriptTaskScannerTests`、`TranscriptTaskLockTests`），假外部服务范本 `FakeFunAsrServer`；`InternalsVisibleTo` 已开给测试项目。

**偏差与提案待澄清（4 处，均在本 Plan 收敛）**：

1. **音频取数 `endMs` 兜底（提案 §3.2/§5.2 实现点 4 与现状不符）**：提案写「`endMs` 空则不传，交由 recording 侧口径」，但 recording 片段端点**强制要求 `endMs`**（缺失即 400），条目 `EndMs` 可为 `null`（= 到音频末尾）会导致重新识别/情绪取数必失败。本 Plan 改为：`endMs` 空时服务端取 **`startMs + Ai:AudioClipDefaultDurationMs`（默认 300000）** 补齐再请求，与前端 `trainingAudioClipUrl` 兜底同口径；仍受 recording 侧 5 分钟上限约束（条目区间本就秒级），recording 端点零改动。落点仅 `RecordingAudioClient` 一处 + 对应断言（1.7）。
2. **情绪识别输入口径（PRD 与提案各有一处不严谨）**：PRD 异常表「音频地址失效（重新识别/**情绪识别**时）」暗示情绪识别也取音频，但 PRD 功能点 3 与需求原文均只说「使用 prompt 让 AI 产出结构数据」；提案决策 6 明确「情绪识别只用文本，两类模型均可用」。本 Plan 采**提案口径：情绪识别以条目 `text` 为输入、不取音频**（因此情绪任务不会因音频失效失败，PRD 该边界行仅对重新识别适用；已在风险表登记为 PRD 建议修订点）。空文本条目提交时记 `skipped`（提案接口 12 口径不变）。
3. **测试设施口径**：提案 §8.2 写「WebApplicationFactory」，仓库实测全部为**控制器直测 + fixture**（无 WebApplicationFactory 用法）。本 Plan 采用仓库既有范式（直测控制器 + `MongoDbFixture`/`RedisFixture`），断言面不变。
4. **两处归属/命名收敛（提案留白或小口径漂移）**：① 提案 §7 对 `TrainingMapping` 标注「归属见下」未定——本 Plan 定为**留在 `servers/training/Contracts/TrainingMapping.cs`**（现状位置），`ParseAudioUri`/`BuildModelSlug`/情绪 `EmotionResult`↔`BsonDocument` 映射一并加在此处，`servers/common/training` 只加实体/仓储/集合/索引（`SetEmotionAsync` 直接收 `BsonDocument`，无需 common 侧映射）；② 提案项目结构把接口 11-14 全列在 `TrainingItemToolsController`、§5.2 又写「承载接口 11-12」——本 Plan 定为 `TrainingItemToolsController`（接口 11-12）+ `TrainingEmotionTasksController`（接口 13-14，路由 `api/training/emotion-tasks`）。另：现有 `TrainingOptions.SectionName = "TranscriptService"`（TranscriptService 专用），新增配置用**独立 options 类型**（`AiOptions`/`EmotionTaskOptions`/`RecordingServiceOptions`）避免语义混淆，`ValidateOnStart` 口径不变。

### 1.1 实施范围

- **做**：
  - `contracts/`：`training.openapi.yaml` + 14 端点与 4 schema（`TrainingProvider`/`TrainingModel`/`EmotionResult`/`TrainingEmotionTask`，`TrainingDatasetItem` 增 `emotion`）；`bilibili-crawler.openapi.yaml` + `/api/training/**` 14 端点镜像；`npm run codegen` 重新生成 `schema-training.d.ts`（勿手改）。
  - `servers/common/training`：3 实体 + 3 仓储（`TrainingProviderRepository`/`TrainingModelRepository`/`TrainingEmotionTaskRepository`）+ `TrainingDatasetItemRepository.SetEmotionAsync` + 3 集合常量 + 3 组索引（`(Name)` 唯一、`(ProviderId, Model)` 唯一 + `ProviderId`、`(Status, CreatedAt)` + `(DatasetId, CreatedAt)` + `ItemId`）。
  - `servers/training`：`Ai/`（抽象 + 两适配器 + `ModelJsonParser` + 异常 + 两 prompt builder）、`Emotion/`（`EmotionTaskLock`/`EmotionTaskScanner`/`EmotionTaskRunner`）、`Clients/RecordingAudioClient`、`Controllers/`（Providers 1-6、Models 7-10、ItemTools 11-12、EmotionTasks 13-14）、DTO/映射扩展、3 个新 options 节、`Program.cs` 注册（Redis + 客户端 + HostedService + 新仓储）。
  - `servers/api`：`TrainingProxyController` + 14 镜像 action。
  - `infra/apphost`：training 增 `.WithReference(messaging).WaitFor(messaging)` 与 `RecordingService__BaseUrl`。
  - `apps/web`：`DatasetsPage` Tab 扩展、`ModelSettingsTab`/`ProviderDialog`/`ReRecognizeDialog`/`EmotionSubmitDialog`/`EmotionTaskQueue` 新组件、`DatasetItemList`（重新识别入口 / 情绪识别按钮 / 展示类型勾选 / 情绪列）、`client.ts` + 14 封装。
  - `tests/Z.Vtuber.Tests` 与 `apps/web` Vitest 测试（清单见 §3 Phase 1）；文档同步（3.3）。
- **不做**：转写链路（consumer/ASR/transcript 既有端点）零改动；recording 音频片段端点与 5 分钟上限零改动；既有 8 个 training 端点语义零改动（仅 `TrainingDatasetItem` 响应**增** `emotion` 字段）；情绪维度结构扩展（本期固定十字段）与人工情绪编辑；任务优先级/配额/限流（本期 FIFO + 低并发）；Provider/Model 用量统计与多租户；重新识别回填起止时间/音频地址（只回填文本）；模型训练与导出；`transcript_*`/`training_dataset*` 存量数据迁移（无存量）。

### 1.2 现状基线（范本）

- 数据层范本：`TrainingMongoSetup.EnsureIndexesAsync`（幂等命名索引）、`TrainingDatasetItemRepository.InsertManyIgnoreDuplicatesAsync`（unordered bulk 计数）。
- 任务锁/扫描范本：`RedisTranscriptTaskLockProvider` + `TranscriptTaskScanner` + `TranscriptTaskRunner`（逐条仿写，含 fencing 与保守降级口径）。
- 代理范本：`TrainingProxyController` + `TrainingProxyControllerTests`（`StubHandler`）。
- 假外部服务范本：`FakeFunAsrServer`（本地假 HTTP server，不打外网）；LLM/模型服务 discover 假服务照此实现。
- web 范本：`TranscriptsPage.tsx`（Tab `role="tablist"`）、`TranscriptImportDialog.tsx`（模态弹窗结构）、`SettingsTab.tsx`（模型增删改/启停、apiKey 不回显）、`client.ts`（`request<T>` + `apiErrorMessage`）。
- 测试范本：`TranscriptApiTests`（控制器直测 + Mongo fixture）、`TranscriptTaskLockTests`/`TranscriptTaskScannerTests`（Redis/Mongo fixture）、`TrainingMappingTests`（纯函数）。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash、分支不复用）与 Conventional Commits：

- 本需求开发全程只使用需求总分支 `feat/training-dataset-emotion`：契约、实现、测试、文档全部直接在该分支提交，不切开发子分支（沿用前几期需求既定做法）；需求总 PR #144 已存在，提交照常推送更新分支。
- 提交信息：契约先行 `feat(contracts):`、测试先行 `test:`、实现 `feat:`、文档 `docs:`；测试与实现分开提交；scope 使用 `training`/`common`/`api`/`web` 既有口径。
- 编码实现按 z-coding 流程以 subagent 按子项目并行（§4 线划分即子项目划分）。

## 3. 实施阶段

按「契约先行（0）→ TDD 测试先行（红）→ 实现（绿）→ 收口验证」推进。Phase 0 串行先行；Phase 1/2 按四条线并行（线内串行）；Phase 3 串行收口。

### Phase 0: 契约先行（串行，全部实现的联调基线）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | `training.openapi.yaml` + 14 端点：① Providers：GET/POST `/api/training/providers`、PUT/DELETE `/api/training/providers/{providerId}`、GET `.../models/discover`、POST `.../models/import`；② Models：GET `/api/training/models?enabled=`、POST `/api/training/models`、PUT/DELETE `/api/training/models/{modelId}`；③ POST `/api/training/datasets/{datasetId}/items/{itemId}/recognize`（body `{modelId}` → 200 `{text}`，**不写库**）；④ POST `/api/training/datasets/{datasetId}/items/emotion-tasks`（body `{itemIds, modelId}` → 200 `{created, skipped}`）、GET `/api/training/emotion-tasks?datasetId=&status=&limit=`（→ `{items, counts}`）、POST `/api/training/emotion-tasks/{taskId}/retry`。+ 4 schema（提案 §3.1 schema 增量逐字采纳：`TrainingProvider` 仅 `apiKeySet`、`TrainingModel` 含服务端拼好的 `slug`、`EmotionResult` 十字段、`TrainingEmotionTask` 含 `modelSlug` 快照与 `emotion`）；`TrainingDatasetItem` 增 `emotion: EmotionResult \| null`。要点：错误体统一 `{"error":"..."}`；`*Id` 非法 ObjectId 按不存在 → 404；Provider `name` 全局唯一（409 `provider name exists`）、`type` 白名单 `openai\|claude`、`endpoint` 仅 http/https 且去尾 `/`、`apiKey` 只进不出；`(ProviderId, Model)` 唯一（409 `model exists`）；删除 Provider **级联删其下 Models**（任务仍持 `modelSlug` 快照）；recognize 错误码面（400 `model does not support audio input`/`audio uri is empty`/`audio too large`、404 三类、502 `audio unavailable`/`model service unavailable`/`invalid model output`）；emotion-tasks `skipped` 口径（条目已有 PENDING/RUNNING 任务、或 `text` 为空）；`limit` 默认 100 最大 500、`CreatedAt` 降序 | `contracts/training.openapi.yaml` | 契约自洽，与提案 §3.1 + 本 Plan §1.0 偏差一致 |
| 0.2 | `bilibili-crawler.openapi.yaml` + `/api/training/**` 14 端点镜像（query/body/状态码透传；上游不可达 502 `{"error":"training service unavailable"}`，与既有 8 个镜像同口径） | `contracts/bilibili-crawler.openapi.yaml` | 与 0.1 语义一致 |
| 0.3 | web codegen：`npm run codegen` 重新生成 `src/api/schema-training.d.ts`（勿手改）；提交契约 + codegen 作为评审基线 | `apps/web/src/api/schema-training.d.ts` | 生成物与契约一致；`npm run lint` 通过 |

### Phase 1: 测试先行（红）

**线 A：数据层 + AI/音频/情绪任务框架（common + training 内部）**

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | `TrainingProviderRepositoryTests`：新建落库（Name trim/Type/Endpoint 去尾 `/`）；重名 DuplicateKey；`ListAsync` CreatedAt 升序；`UpdateAsync` 部分字段（apiKey 空串不改）；`DeleteAsync` 命中/未命中；`GetByIdAsync` | `tests/Z.Vtuber.Tests/TrainingProviderRepositoryTests.cs` | 红 |
| 1.2 | `TrainingModelRepositoryTests`：`(ProviderId, Model)` 重复插入 DuplicateKey；`ListAsync(enabled)` 过滤；`InsertManyIgnoreDuplicatesAsync` 全新/全跳过/混合批次计数（imported+skipped=提交数）；`UpdateAsync`（改名撞唯一索引）；`DeleteByProviderAsync` 级联计数 | `tests/Z.Vtuber.Tests/TrainingModelRepositoryTests.cs` | 红 |
| 1.3 | `TrainingEmotionTaskRepositoryTests`：`InsertAsync` 落库快照字段（`modelSlug`/`audioKey`）；`HasActiveAsync`（PENDING/RUNNING 计活跃，DONE/FAILED 不计）；`ListClaimableAsync`（PENDING 或 RUNNING、`CreatedAt` 升序、batch 截断）；`ListAsync` 按 `datasetId`/`status` 过滤 + limit + `CreatedAt` 降序；`CountByStatusAsync` 分组计数；`UpdateAsync` 状态迁移 | `tests/Z.Vtuber.Tests/TrainingEmotionTaskRepositoryTests.cs` | 红 |
| 1.4 | `TrainingMapping` 扩展测试：`ParseAudioUri`（合法 `b/live/{roomId}/recordings/{file}` → roomId/file、非法/空/`file` 含斜杠或空 → null，与前端 `trainingAudioClipUrl` 正则同口径）；`BuildModelSlug` → `{providerName}/{model}`；`ToEmotionDocument`/`FromEmotionDocument`（十字段小驼峰往返、缺失/格式不符 → `null` 不抛、八维越界钳 0~1、`emotionControlWeight` 上限 10 超界钳制记 Warn） | `tests/Z.Vtuber.Tests/TrainingMappingTests.cs`（扩展） | 红 |
| 1.5 | `ModelJsonParserTests`：剥 \`\`\`json 围栏（含尾部杂文）→ 取首个平衡 `{...}`（嵌套括号/字符串内括号）→ 反序列化（大小写不敏感）；非 JSON/无数值字段抛 `ModelOutputException`；数值钳制纯函数断言 | `tests/Z.Vtuber.Tests/ModelJsonParserTests.cs` | 红 |
| 1.6 | Prompt 测试：`RecognizePromptBuilder`/`EmotionPromptBuilder` 组装含硬约束（「只输出 JSON」「`{"text":"..."}`」「十字段固定名」「数值 0~1」「描述 ≤ 200 字」）与 user 文本/音频位注入 | `tests/Z.Vtuber.Tests/ModelPromptTests.cs` | 红 |
| 1.7 | `RecordingAudioClientTests`（假 recording HTTP server）：合法 uri → 请求路径 `/api/{roomId}/recordings/{file}/audio?startMs=&endMs=`；**`endMs` 空 → 按 `Ai:AudioClipDefaultDurationMs`（默认 300000）补 `endMs` 再请求（偏差 1 锁定）**；404/超时/非 2xx → `AudioUnavailableException`；字节超 `Ai:MaxAudioBytes` → `AudioTooLargeException`；uri 不可解析 → 空/非法口径 | `tests/Z.Vtuber.Tests/RecordingAudioClientTests.cs` | 红 |
| 1.8 | `EmotionTaskLockTests`（Redis fixture）+ `EmotionTaskScannerTests`（Mongo fixture）：抢到/拒二抢/释放后可抢、续期、`CheckOwnershipAsync` fencing、Redis 异常 `IsHeldAsync` 保守按持有；扫描领取 PENDING、接管无锁 RUNNING（僵尸）、`MaxAttempts` 超限置 FAILED、`MaxConcurrentTasks` 门控、扫描异常不中断循环；`EmotionTaskRunnerTests`：置 RUNNING → 空文本 FAILED → 调模型 → 解析 → `SetEmotionAsync`（`Extensions.emotion` 十字段）→ DONE + `Emotion` 快照；单条失败置 FAILED 不影响其他、失锁放弃写入、`finally` 释放锁 | `tests/Z.Vtuber.Tests/EmotionTaskLockTests.cs`、`EmotionTaskScannerTests.cs`、`EmotionTaskRunnerTests.cs` | 红 |

**线 B：training 14 端点 API（controllers）**

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.9 | `TrainingProviderApiTests`：新建 201 / 400 四类 / 409；**响应永不含 `apiKey`、仅 `apiKeySet`**；编辑部分字段（缺省不改、`apiKey` 空串不改）；删除 204/404 且**级联删其下 Models**；discover（假模型服务）：OpenAI-like `GET {endpoint}/models` 与 Claude-like `GET {endpoint}/v1/models` 各自解析去重排序、Endpoint 不可达/非 2xx 502 `provider service unavailable`、响应非法 502 `invalid provider response`；导入 `{imported, skipped}`（撞唯一索引计 skipped） | `tests/Z.Vtuber.Tests/TrainingProviderApiTests.cs` | 红 |
| 1.10 | `TrainingModelApiTests`：列表 `enabled` 过滤 + **`slug == "{providerName}/{model}"`**；手动添加（`providerId` 不存在 404、`model` 空/超长 400、重复 409）；更新（启停/改名 409）；删除 204/404；识别类端点仅允许 `enabled` 模型 | `tests/Z.Vtuber.Tests/TrainingModelApiTests.cs` | 红 |
| 1.11 | `TrainingRecognizeTests`（假音频 + 假 LLM）：正常回填 `{text}` 且**不写库**（保存前条目 `text` 不变）；base64 音频与 prompt 确实发往所选模型（OpenAI `input_audio` / Claude 文本）；Claude-like + 音频 → 400 `model does not support audio input`；`audioUri` 空 400；音频 404/超时 502 `audio unavailable`；模型输出非 JSON 502 `invalid model output`；传输失败/超时 502 `model service unavailable`；dataset/item/model 不存在 404；模型 `enabled=false` 400 | `tests/Z.Vtuber.Tests/TrainingRecognizeTests.cs` | 红 |
| 1.12 | `EmotionTaskApiTests`：提交 `{created, skipped}`（重复提交幂等 skipped、空文本 skipped）；队列 `items` 字段（含 `modelSlug` 快照、`emotion`）与 `counts` 分组正确、`datasetId`/`status`/`limit` 过滤；retry：FAILED → PENDING 且 `attempts` 累加、`error` 清空，非 FAILED 400 `task not failed`、404；扫描执行后条目 `emotion` 十字段齐全且 0~1、重复识别以最新覆盖 | `tests/Z.Vtuber.Tests/EmotionTaskApiTests.cs` | 红 |

**线 C：api 代理镜像**

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.13 | `TrainingProxyControllerTests` 扩展（`StubHandler` 模式）：14 镜像路由方法/路径断言（含 query 透传、POST/PUT body 与 Content-Type 透传）；200/201/204/400/404 透传；上游不可达 502 `training service unavailable` | `tests/Z.Vtuber.Tests/TrainingProxyControllerTests.cs` | 红 |

**线 D：web（Vitest + RTL）**

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.14 | `client.test.ts` 扩展：14 个封装的路径/method/body 断言（providers/models CRUD、discover/import、`recognizeTrainingDatasetItem`、`createEmotionTasks`、`listEmotionTasks`（query 串）、`retryEmotionTask`）；400 抛 `ApiError` 含 `{"error":"..."}` 服务端消息 | `apps/web/src/api/client.test.ts` | 红 |
| 1.15 | `DatasetsPage.test.tsx` 扩展：「音频数据集」「模型设置」两 Tab 并列、切换渲染对应区块、既有数据集选择器/导入入口不回退 | `apps/web/src/pages/training/DatasetsPage.test.tsx` | 红 |
| 1.16 | `ModelSettingsTab.test.tsx`：Providers 表格（名称/类型/Endpoint/API key 状态/操作）增删改；**编辑时 key 留空即不改且提示不回显、列表永不出现明文**；类型下拉仅 OpenAI/Claude；「获取模型」走 discover 并勾选导入反馈 `{imported, skipped}`（不可达提示「模型服务不可用」）；Models 表格 slug 展示 `{provider}/{model}`、手动添加 **Provider 为下拉选择已有 Providers**、启停/删除；删除 Provider `confirm` 二次确认并提示「其下 N 个模型将一并删除」；未配置任何模型的空态引导 | `apps/web/src/pages/training/ModelSettingsTab.test.tsx`（含 `ProviderDialog` 交互） | 红 |
| 1.17 | `ReRecognizeDialog.test.tsx`：编辑态出现「重新识别」按钮并弹出对话框（`aria-modal` 遮罩 + 居中面板，复用 `TranscriptImportDialog` 结构）；model 下拉列已配置 Models（slug）且**无模型时提示「请先在模型设置中配置」**；「识别」loading + 禁用防重复提交、成功回填「文本」框可继续微调、失败提示且**原文本不被破坏**；「保存」与内联编辑保存同一 `updateTrainingDatasetItem` 调用并关闭；「取消」不产生任何请求/修改 | `apps/web/src/pages/training/ReRecognizeDialog.test.tsx` | 红 |
| 1.18 | `EmotionSubmitDialog` + `EmotionTaskQueue.test.tsx`：工具栏「情绪识别」**未多选置灰（`aria-disabled`）**、多选可用；确认框 model **必选**下拉（slug）、确认才提交并反馈 `{created, skipped}`、取消不请求；任务队列区块：状态统计 chips + 任务表格（条目/模型 slug/状态/次数/错误/时间）+ FAILED「重试」按钮 + `setInterval` 10s 轮询 + 卸载清理 | `apps/web/src/pages/training/EmotionSubmitDialog.test.tsx`、`EmotionTaskQueue.test.tsx` | 红 |
| 1.19 | `DatasetItemList.test.tsx` 扩展：「展示类型」勾选组位于**分组切换控件之前**；默认「音频信息」不勾选（六列隐藏）、「情绪信息」勾选（十列展示）；勾选即时生效；文本列与操作列恒展示；无情绪数据行情绪列空态「-」；情绪数值列限窄、情绪描述列截断 + `title` 悬浮；**展开行 `colSpan` 随可见列数动态计算**、既有编辑/多选/分组/波形回归不回退 | `apps/web/src/pages/training/DatasetItemList.test.tsx` | 红 |

### Phase 2: 实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | **数据层（common/training）**：① `TrainingCollectionNames` + `TrainingProviders`(`training_providers`)/`TrainingModels`(`training_models`)/`TrainingEmotionTasks`(`training_emotion_tasks`)；② 3 实体（`[BsonIgnoreExtraElements]`、时间 `[BsonDateTimeOptions(Kind = Utc)]`，`TrainingEmotionTaskRecord` 含 `ModelSlug`/`AudioKey` 快照、`Emotion`(BsonDocument?)）；③ 3 仓储（签名按提案 §3.2，唯一索引冲突交由 `DuplicateKey` 上抛供控制器映 409）；④ `TrainingDatasetItemRepository.SetEmotionAsync` 仅 `$set: {"Extensions.emotion": ...}`（不动其他键）；⑤ `TrainingMongoSetup.EnsureIndexesAsync` + 3 组幂等索引 | `servers/common/training/` | 1.1–1.3 转绿 |
| 2.2 | **守护测试同步**：`CollectionNamesEquivalenceTests` 补 3 常量断言（`training_providers`/`training_models`/`training_emotion_tasks`）；`ProjectReferenceGuardTests` 矩阵不变（Redis 走 `Aspire.StackExchange.Redis` 包引用，不引领域项目） | `tests/Z.Vtuber.Tests/` | 断言与实现一致 |
| 2.3 | **映射扩展（偏差 1/4 落点）**：`TrainingMapping` + `ParseAudioUri`/`BuildModelSlug`/`ToEmotionDocument`/`FromEmotionDocument`；`TrainingDatasetItem` DTO 增 `emotion`（取 `Extensions["emotion"]`，缺失/不符 → null 不抛） | `servers/training/Contracts/TrainingMapping.cs`、`Contracts/Dtos.cs` | 1.4 转绿 |
| 2.4 | **AI 客户端（training/Ai）**：`IChatModelClient`/`IChatModelClientFactory`（按 `provider.Type` 派发，未知类型抛 `ProviderTypeException`）+ `OpenAiLikeChatClient`（`POST {endpoint}/chat/completions`、Bearer、`text` + 可选 `input_audio`、`response_format: json_object` 且 400 降级重试一次）+ `ClaudeLikeChatClient`（`POST {endpoint}/v1/messages`、`x-api-key` + `anthropic-version: 2023-06-01`、仅 `text`、**音频直接抛 `ModelAudioNotSupportedException`**）+ `ModelJsonParser` + `ModelExceptions` + `Prompts/` 两 builder；named client `AiModel`（无 BaseAddress，逐请求拼 endpoint，超时 `Ai:RequestTimeoutSeconds`）；日志不打 apiKey 与音频内容 | `servers/training/Ai/` | 1.5、1.6 转绿 |
| 2.5 | **音频取数（偏差 1）**：`Clients/RecordingAudioClient.GetClipAsync(audioUri, startMs, endMs, ct)` → `(Bytes, ContentType)`；`endMs` 空按 `Ai:AudioClipDefaultDurationMs`（默认 300000）补齐后调 `{RecordingService:BaseUrl}/api/{roomId}/recordings/{file}/audio`；uri 空/不可解析 → 400 `audio uri is empty`；取数失败/超时/非 2xx → `AudioUnavailableException`（502 `audio unavailable`）；超 `Ai:MaxAudioBytes` → 400 `audio too large` | `servers/training/Clients/RecordingAudioClient.cs` | 1.7 转绿 |
| 2.6 | **Providers / Models 控制器（接口 1-10）**：`TrainingProvidersController`（1-6）+ `TrainingModelsController`（7-10）；校验口径按 0.1（trim/长度/白名单/去尾 `/`）；`DuplicateKey` → 409；**响应 DTO 一律不含 `apiKey`**（`apiKeySet` 表示是否已配置）；discover 按 `type` 选协议并去重排序；`apiKey` 与音频内容绝不入日志 | `servers/training/Controllers/` | 1.9、1.10 转绿 |
| 2.7 | **重新识别与情绪提交（接口 11-12）**：`TrainingItemToolsController`——recognize 顺序 dataset → item → model（含 `enabled`）→ 取音频 → base64 → 调模型 → 解析 → 200 `{text}`（**不写库**）；emotion-tasks 逐条 `HasActiveAsync` 去重 + 空文本 skipped → 批量插 PENDING（含 `modelSlug`/`audioKey` 快照）→ `{created, skipped}`；Info 日志 `training item recognize: datasetId={} itemId={} modelId={} bytes={} elapsedMs={}` | `servers/training/Controllers/TrainingItemToolsController.cs` | 1.11、1.12 转绿 |
| 2.8 | **情绪任务队列与重试（接口 13-14）**：`TrainingEmotionTasksController`——队列查询（`datasetId`/`status`/`limit` + `counts`）、retry（仅 FAILED → PENDING、`attempts` 保留、`error` 清空） | `servers/training/Controllers/TrainingEmotionTasksController.cs` | 1.12 转绿 |
| 2.9 | **情绪任务执行框架**：`Emotion/EmotionTaskLock.cs`（`IEmotionTaskLockProvider`/`RedisEmotionTaskLock`，key `training:emotion-task-lock:{taskId}`，SET NX PX + 续期 + Lua 释放 + fencing + `IsHeldAsync` 保守降级，语义逐条仿 `TranscriptTaskLock`）+ `EmotionTaskScanner`（HostedService：`ScanIntervalSeconds` 周期 + 启动抖动、`ListClaimableAsync(ScanBatchSize)`、`MaxConcurrentTasks` 信号量门控、僵尸 RUNNING 接管、`MaxAttempts` 超限置 FAILED、Redis 异常本轮跳过 Warn）+ `EmotionTaskRunner`（单任务编排 + 逐阶段 fencing，失锁放弃写入）；状态机 `PENDING → RUNNING → DONE \| FAILED`，retry 仅 `FAILED → PENDING` | `servers/training/Emotion/` | 1.8 转绿 |
| 2.10 | **配置与编排**：`AiOptions`（`RequestTimeoutSeconds=120`、`MaxAudioBytes=8388608`、`AudioClipDefaultDurationMs=300000`）/`EmotionTaskOptions`（`ScanIntervalSeconds=10`、`ScanBatchSize=10`、`LockTtlSeconds=30`、`LockRenewIntervalSeconds=10`、`MaxConcurrentTasks=2`、`MaxAttempts=3`）/`RecordingServiceOptions`（`BaseUrl` 默认 `http://recording`）均 `ValidateOnStart`；`Program.cs` 注册 `Aspire.StackExchange.Redis`（`ConnectionStrings:messaging`）+ named client + 3 仓储 + `EmotionTaskScanner`；`infra/apphost/AppHost.cs` training 增 `.WithReference(messaging).WaitFor(messaging)` + `.WithEnvironment("RecordingService__BaseUrl", recording.GetEndpoint("http"))` | `servers/training/`、`infra/apphost/AppHost.cs` | Aspire 可拉起且扫描器就绪 |
| 2.11 | **api 代理镜像**：`TrainingProxyController` + 14 action（全走既有 `ForwardAsync`，GET/DELETE 无 body 已兼容） | `servers/api/Controllers/TrainingProxyController.cs` | 1.13 转绿 |
| 2.12 | **web 实现**：① `DatasetsPage.tsx`：`TabKey = 'audio' \| 'models'`、`TABS = [音频数据集, 模型设置]`；② `ModelSettingsTab.tsx` + `ProviderDialog.tsx`（Providers 双表 + discover 导入 + 手动添加 Provider 下拉 + 删除级联提示 + apiKey 不回显）；③ `ReRecognizeDialog.tsx`（复用 `TranscriptImportDialog` 模态结构；「识别」loading 防重复、失败不动原文、「保存」走 `updateTrainingDatasetItem`）；④ `EmotionSubmitDialog.tsx` + `EmotionTaskQueue.tsx`（确认框 model 必选 + 队列轮询/重试）；⑤ `DatasetItemList.tsx`：编辑态按钮组增「重新识别」、工具栏增「情绪识别」（未多选置灰）、**分组切换前**增「展示类型」勾选组（音频信息默认隐藏 / 情绪信息默认展示）、十情绪列 + 六音频列条件渲染（`colgroup`/`<th>`/`<td>` 同步、展开行 `colSpan` 动态、情绪列限宽截断 + `title`、空态「-」）；⑥ `client.ts` + 14 封装（错误统一 `apiErrorMessage`）+ `npm run codegen` | `apps/web/src/` | 1.14–1.19 转绿 |
| 2.13 | codegen 一致性复核（`npm run codegen` diff 为空）+ `npm run lint`/`npm run build`/`npm run test`、`dotnet build` 全通过 | 全部 | 构建零告警 |

### Phase 3: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 3.1 | 全量测试与覆盖率：`dotnet test tests/Z.Vtuber.Tests` 全绿（含既有回归）；`apps/web` `npm run test` 全绿；核心逻辑（JSON 解析与钳制、情绪 Bson 映射、`ParseAudioUri`、slug、任务状态机与去重判定）覆盖率 ≥ 80%（common-rules） | 全部通过 |
| 3.2 | 兼容性回归：`git diff` 确认 `servers/recording-transcript-consumer`、`servers/recording-transcript-api`、`servers/recording`、ASR 链路与 `transcript_*` 集合零改动；既有 training 8 端点语义不回退（仅响应增 `emotion` 字段）；api 既有代理回归正常 | diff/grep 无违规 |
| 3.3 | 文档同步：更新 `servers/training/AGENTS.md`（Ai/Emotion/Clients 模块 + 22 端点 API 面 + 新配置节 + Redis 依赖与部署要点）、`servers/common/AGENTS.md`（3 实体 3 仓储 + `SetEmotionAsync` + 3 集合索引）、`servers/api/AGENTS.md`（TrainingProxyController 镜像路由数）、`apps/web/AGENTS.md`（目录 + 模型设置/任务队列/重新识别/展示类型）；本 Plan 与提案/PRD 链接互验 | 文档与实现一致 |
| 3.4 | 手工验证（z-aspire-orchestrator，可选但推荐）：Aspire 拉起 → 「模型设置」建 Provider（OpenAI-like/Claude-like）→ 获取模型并导入 → 手动加模型 → 单条「重新识别」（识别回填 → 微调 → 保存 / 取消无修改）→ 多选「情绪识别」提交 → 「任务队列」看排队/执行/失败重试 → 列表情绪列展示、勾选「音频信息」展开六列 → 既有导入/编辑/波形/多选删除回归正常 | 手工验证记录 |

## 4. 并行策略

- **Phase 0 串行先行**（0.1→0.2→0.3）：契约是全部线的联调与 codegen 基线。
- **Phase 1/2 四条线并行，线内串行**（亦为 subagent 子项目划分）：
  - **线 A 数据层 + AI/音频/任务框架**：1.1→1.2→1.3→1.4→1.5→1.6→1.7→1.8→2.1→2.2→2.3→2.4→2.5→2.9→2.10（common → Ai/音频 → 锁与扫描器 → 配置编排收尾）；
  - **线 B API**：1.9→1.10→1.11→1.12→2.6→2.7→2.8（依赖线 A 的实体/仓储/映射/AI 客户端与 0.1 契约；控制器文件归线 B 单线）；
  - **线 C 代理**：1.13→2.11（与 A/B 零文件交集，可最先开工）；
  - **线 D web**：1.14→1.15→1.16→1.17→1.18→1.19→2.12（依赖 0.3 codegen；组件以 mock fetch 测试驱动，不阻塞后端联调，联调在 3.4 收口）。
- **文件冲突规避**：`servers/common/training` 归线 A 单线；training `Controllers/` 归线 B；`Ai/`、`Emotion/`、`Clients/RecordingAudioClient.cs` 归线 A；`client.ts`/`DatasetsPage.tsx`/`DatasetItemList.tsx` 全部收敛线 D；AppHost 归线 A 收尾。
- **依赖关系**：2.6–2.8 依赖 0.1 与 2.3/2.4/2.5；2.11 依赖 0.2；2.12 依赖 0.3；3.x 全线完成后串行收口。
- 全部提交在需求总分支 `feat/training-dataset-emotion`。

## 5. 验收对照

| PRD 功能点 / 成功标准 | 实现任务 | 验证方式 |
|-----------|---------|---------|
| 功能点 1：「模型设置」Tab（Providers 四字段 + apiKey 不回显 + discover 导入 + 手动加 Model + slug 展示 + 删除处理提示） | 0.1、2.6、2.12①② | 1.9、1.10、1.16 |
| 功能点 2：单条「重新识别」（编辑态按钮 → 对话框 model/文本/识别·保存·取消 → 音频 base64 + prompt + JSON 回填 → 保存同内联） | 0.1、2.4、2.5、2.7、2.12③ | 1.7、1.11、1.17 |
| 功能点 3：批量「情绪识别」+ 任务队列（多选解锁按钮 → 确认框 model 必选 → 后台任务 → 队列可见 → 十字段情绪数据 + 列表展示） | 0.1、2.7、2.8、2.9、2.12④⑤ | 1.8、1.12、1.18、1.19 |
| 功能点 4：列表展示优化（分组切换前「展示类型」勾选：音频信息六列默认隐藏、情绪信息十列默认展示、空态「-」） | 0.1（`emotion` 字段）、2.12⑤ | 1.19 |
| 成功标准：模型配置闭环（添加 Provider → 获取模型 → 可用于识别） | 2.6、2.12② | 1.9、1.16 + 3.4 |
| 成功标准：重新识别闭环（识别回填 → 微调 → 保存） | 2.5、2.7、2.12③ | 1.11、1.17 + 3.4 |
| 成功标准：情绪批量加工透明可追踪（队列状态/进度/成功失败数、重复识别覆盖） | 2.8、2.9、2.12④ | 1.12、1.18 |
| 边界：单条失败不影响其他、失败可重试、模型不可用/输出非法明确提示不破坏原文 | 2.7、2.8、2.9、2.12③④ | 1.8、1.11、1.12 |
| 非功能：安全（apiKey 仅写入不回显、日志无密钥与音频内容、沿用管理后台口径） | 2.4、2.6、2.7 | 1.9、3.2 |
| 非功能：性能（后台批量 + 轮询、识别加载态 + 超时、低并发防限流） | 2.9、2.10、2.12③④ | 1.8、1.18 + 3.4 |
| 非功能：兼容（既有数据集/导入/编辑/多选/分组/波形不回退、情绪数据零表结构变更） | 1.1 范围约束、2.1④ | 3.2 diff、1.19 回归用例 |
| 契约与质量门槛（契约先行 + codegen 一致 + 覆盖率 ≥ 80% + TDD） | 0.x、2.13、3.1 | codegen diff、覆盖率报告、红-绿提交记录 |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| 音频取数 `endMs` 必填（偏差 1） | 提案「endMs 空则不传」与 recording 端点强制 `endMs`（缺失 400）冲突，条目 `EndMs` 可空会取数必失败 | `RecordingAudioClient` 补 `endMs = startMs + Ai:AudioClipDefaultDurationMs`（默认 300000，与前端 `trainingAudioClipUrl` 兜底同口径）；1.7 锁定；recording 零改动 |
| 情绪识别输入口径（偏差 2） | PRD 边界表暗示情绪识别取音频，与提案决策 6「只用文本」不一致 | 采提案口径（文本判定，两类模型均可用于情绪识别）；PRD 该边界行登记为建议修订；空文本提交记 skipped（1.12 锁定） |
| LLM 输出不可控 | 围栏/杂文/越界数值/非 JSON | `ModelJsonParser` 剥围栏 + 首个平衡对象 + 钳制；解析失败 502 `invalid model output` 且不写库（1.5、1.11 锁定） |
| Claude-like 无音频模态 | 强行塞音频会静默错配 | 按能力显式建模：适配器拒绝音频 400（提案决策 6）；前端下拉不屏蔽但错误提示清晰（1.11、1.17 锁定） |
| 情绪任务锁双跑/失锁 | LLM 调用常超 TTL，通用锁无续期 | 逐条仿 `TranscriptTaskLock`（SET NX PX + 续期 + fencing + 保守降级）；1.8 并发用例锁定不双跑、接管续跑、失锁不写 |
| 僵尸 RUNNING 饿死 PENDING | 上一实例崩溃留 RUNNING | 扫描器按 `IsHeldAsync == false` 接管，`MaxAttempts` 上限保护（1.8 锁定） |
| 重复提交/重复识别 | 多选重复点、任务重叠 | `HasActiveAsync` 幂等 skipped；重复识别以最新结果覆盖 `Extensions.emotion`（1.3、1.12 锁定） |
| 删除 Provider 级联 | 已入队任务引用的模型消失 | 任务存 `modelSlug` 快照保持可读；UI 删除前提示「其下 N 个模型将一并删除」（1.9、1.16 锁定） |
| 情绪列默认展示列过宽 | 十列挤占文本列 | 情绪数值列限窄宽、描述列截断 + `title` 悬浮，沿用既有列宽策略；展开行 `colSpan` 动态计算（1.19 锁定） |
| training 首次引入 Redis | 裸部署缺连接串则启动失败 | `ConnectionStrings:messaging` + `ValidateOnStart`；部署说明写入 AGENTS.md（2.10、3.3） |
| 超时与限流 | 单条识别同步等待、批量低并发 | `Ai:RequestTimeoutSeconds=120` + 前端 loading 禁用；`MaxConcurrentTasks=2` 顺序/低并发消费（2.9、2.10） |
| 测试设施口径（偏差 3） | 提案写 WebApplicationFactory，仓库无此用法 | 沿用控制器直测 + Testcontainers fixture，断言面不变 |
| 依赖 | recording 音频片段端点（5 分钟上限，已核实）；外部 OpenAI-like/Claude-like 服务（需 JSON 输出，重新识别另需音频输入）；Redis（`ConnectionStrings:messaging`）；Mongo 3 新集合（无迁移） | 均已核实存在（§1.0）；.NET 10、TDD、覆盖率 ≥ 80% 为项目既有规范 |

## 7. 相关文档

- PRD：`docs/product/draft/20260928-training-dataset-emotion-prd.md`（v1.0）
- 技术提案：`docs/engineering/proposals/20260928-proposal-training-dataset-emotion.md`（v1.0）
- 前序 Plan：[[20260927-plan-training-audio-dataset]]、[[20260916-plan-recording-transcript]]
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`docs/engineering/frontend-rules.md`
- 模块规范：`servers/common/AGENTS.md`、`servers/training/AGENTS.md`、`servers/api/AGENTS.md`、`apps/web/AGENTS.md`
- 既有契约：`contracts/training.openapi.yaml`、`contracts/bilibili-crawler.openapi.yaml`、`contracts/recording-transcript.openapi.yaml`
