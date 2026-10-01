---
description: AI 训练「数据集模型设置支持模型能力」实现 Plan：training_models 增补输入/输出能力（audio/video/text/image + Normalize/Matches 纯函数，marker 幂等存量清洗为输入「语音、文本」/输出「文本」）、新增 training_feature_points 功能点登记与模型指定（slug 自动登记 upsert + 指定优先 + 取消恢复默认 = 可用模型中 CreatedAt 最早者）、models 列表按能力过滤、三条链路接入 TrainingModelResolver（dataset-clean / emotion-from-audio / emotion-from-text），契约先行（training.openapi.yaml 3 变更 + 4 新端点 + 6 schema、bilibili-crawler 镜像 + TrainingProxyController 4 action），web 模型能力列/勾选 + FeaturePointPanel 功能点区块 + 两条入口下拉按能力过滤与默认选中，TDD 覆盖纯函数/解析器/存量清洗/API/组件
type: Permanent
---

# AI 训练 - 数据集模型设置支持模型能力 - 实现 Plan

**功能名称**: 训练 - 数据集模型设置支持模型能力
**关联 PRD**: `docs/product/draft/20261001-dataset-model-capability-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20261001-proposal-dataset-model-capability.md`（v1.0）
**Plan 版本**: v1.0
**创建日期**: 2026-10-01
**作者**: Workflow Agent（代起草）
**feat-branch**: `feat/dataset-model-capability`

## 1. 概述

依据 PRD 与技术提案落地三件事：① 模型补「能力」维度（语音/视频/文本/图片，输入输出分别多选，存量模型一次性清洗为「输入：语音、文本；输出：文本」）；② 功能点按能力查询可用模型（可用 = 启用 + 输入/输出能力覆盖所需）；③ 功能点维度模型设置（slug 自动登记、指定模型优先返回、取消指定恢复默认，默认为未设置）。实施遵循 `docs/engineering/common-rules.md`（契约先行、TDD、覆盖率 ≥ 80%、单用例 ≤ 5s）、`backend-rules.md`、`frontend-rules.md`、z-coding 流程与各模块 `AGENTS.md`。

### 1.0 现状核实结论（读码确认）

- `servers/common/training/Entities/TrainingModelRecord.cs`：`Id/ProviderId/Model/Enabled/CreatedAt/UpdatedAt`，已带 `[BsonIgnoreExtraElements]`（新增能力字段对旧版本读取无感）；`TrainingCollectionNames` 现有 5 集合，`TrainingMongoSetup.EnsureIndexesAsync` 幂等建索引。
- `TrainingModelRepository.ListAsync` 按 `CreatedAt` 升序（`SortBy(m => m.CreatedAt)`）——「默认选择 = 可用模型中 `CreatedAt` 最早者」与现状「首个启用模型」完全一致（提案决策 7 成立）。
- `TrainingSynthesizeController.ResolveEmotionModelAsync`（`Controllers/TrainingSynthesizeController.cs:164`）现为「显式 `modelId` → `Synthesize:DefaultEmotionModelId` → 首个启用模型 → null（情绪降级中性）」。
- DTO/映射在 `servers/training/Contracts/Dtos.cs` + `TrainingMapping.cs`（**training 服务侧，不在 common**；提案 §7 项目结构中 `TrainingMapping` 括注歧义以本 Plan §1.1 为准）。
- 契约：`contracts/training.openapi.yaml` 25 个 operationId（health + 24 业务）；`contracts/bilibili-crawler.openapi.yaml` 现有 `/api/training/**` 18 条路径镜像；`TrainingProxyController` 24 个 action 走 `ForwardAsync`（POST/PUT 透传 body、query 原样、上游不可达 502 `training service unavailable`），`TrainingProxyTimeoutConsistencyTests` 守护 180s 代理超时 > 120s 上游预算。
- web：`ModelSettingsTab.tsx` 为 Providers + Models 双表（模型手动添加/编辑为内联表单，无独立 ModelDialog）；`ReRecognizeDialog.tsx`（数据清洗-重新识别）与 `EmotionSubmitDialog.tsx`（批量情绪识别）均 `listTrainingModels(true)` 取「全部启用模型」；**synthesize 无 web 入口**（`apps/web/src` 无 synthesize 组件与 `client.ts` 封装），「输入文本识别情绪」为纯后端链路（详见 §1.1）。
- 测试设施：`MongoDbFixture`（`AllCollectionsCore` 集合名单 + `ResetAsync` 清数据）、`CollectionNamesEquivalenceTests`、`TrainingMongoSetupTests`；marker 幂等迁移先例 `TranscriptMongoSetup.CleanupAsync`（`MetaCollection` + `MarkMigrationAppliedAsync`，调用点 `recording-transcript-api/Program.cs:67`）。
- 镜像流水线 `build-training-image.yml` paths 已含 `servers/training/**` + `servers/common/{core,training,transcript}/**`（与 `WorkflowPathsGuardTests` 期望一致），本期无需调整 workflows。

### 1.1 口径澄清（规划期裁定，随实现评审）

1. **「输入文本识别情绪」的模型选择处**：synthesize 无 web UI（现状核实），PRD「模型下拉」落到 ① `FeaturePointPanel` 的「指定模型」下拉（只列该功能点可用模型）与 ② synthesize 请求可选 `modelId`（服务端按功能点口径解析）；本期**不新增合成 UI**，PRD 功能点 2 的该项验收按此口径在 `FeaturePointPanel` 下拉与服务端解析处核对。
2. **`TrainingMapping`/`Dtos.cs` 归属**：留 `servers/training/Contracts/`（既有约定）；common 侧只放能力纯函数、实体、仓储与存量清洗。
3. **存量清洗按字段粒度补齐**：`InputCapabilities` 缺失只补输入、`OutputCapabilities` 缺失只补输出（提案原文双字段同补，细化为逐侧 `$set`，杜绝覆盖任一侧已设置值，严格对齐 PRD「不覆盖用户已手动调整的能力」）。
4. **slug 校验**沿用模型名口径：trim 非空 ≤ 128，空 → 400 `feature point slug is required`，超长 → 400 `feature point slug too long`。

### 1.2 实施范围

- **做**：`servers/common/training`（能力常量与纯函数、`TrainingModelRecord` 能力字段、`TrainingFeaturePointRecord` + 仓储、`training_feature_points` 集合/索引、存量能力清洗）；`servers/training`（`FeaturePointIds`、`TrainingModelResolver`、`TrainingFeaturePointsController`、`TrainingModelsController` 能力读写与过滤、合成情绪解析改走功能点、`Program.cs` 注册）；`contracts/`（training + bilibili-crawler）与 web codegen；`servers/api/TrainingProxyController` 4 个镜像 action；`apps/web`（能力列与勾选、`FeaturePointPanel`、两条入口候选过滤与默认选中）；`tests/Z.Vtuber.Tests` 与 `apps/web` Vitest；四份 `AGENTS.md` 与 PRD 同步。
- **不做**：各功能点识别/合成加工逻辑（prompt、JSON 解析、情绪任务状态机、`Extensions.emotion` 结构）；Provider/Model discover/import、启用语义、`{provider}/{model}` slug 口径；能力自动推断；自定义能力扩展；合成（synthesize）web UI；用量统计与计费；新增配置、依赖、环境变量与流水线调整。

## 2. 分支与提交流程

- 需求总分支 `feat/dataset-model-capability`（基于 `release`，PRD 与技术提案已随 `6ce50af` 提交）；本 Plan 及后续实现均提交到该分支，随需求总 PR（#166）评审。
- 提交顺序（Conventional Commits，测试与实现分开）：
  1. `docs(training):` 本实现 Plan；
  2. `feat(contracts):` 契约变更 + codegen；
  3. `feat(common):` 能力纯函数 / 实体 / 仓储 / 存量清洗（含 `test(common):` 配套）；
  4. `feat(training):` 解析器 + 控制器 + 三条链路接入（含 `test(training):` 配套）；
  5. `feat(api):` 代理镜像 action；
  6. `feat(web):` 能力列 / 功能点区块 / 入口过滤（含组件测试）；
  7. `docs(training):` `AGENTS.md` / PRD 同步。

## 3. 实施阶段

### Phase 0: 技术文档（先行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | PRD（上游节点产出） | `docs/product/draft/20261001-dataset-model-capability-prd.md` | 已完成（`6ce50af`） |
| 0.2 | 技术提案（上游节点产出） | `docs/engineering/proposals/20261001-proposal-dataset-model-capability.md` | 已完成（`6ce50af`） |
| 0.3 | 实现 Plan（本文档） | `docs/engineering/plans/20261001-plan-dataset-model-capability.md` | 任务/文件/验收明确，与提案口径一致 |

### Phase 1: 契约先行

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | models 3 端点变更：列表 + `input`/`output` query（逗号分隔、模型须覆盖集合）、create/update body + `inputCapabilities`/`outputCapabilities`（可选；update 缺省不改、空数组清空）；新增 4 端点：`GET /api/training/feature-points`、`POST /api/training/feature-points/{slug}/model/resolve`、`PUT`/`DELETE /api/training/feature-points/{slug}/model`；新增 6 schema（`TrainingModelCapability`/`TrainingFeaturePoint`/`TrainingFeaturePointList`/`TrainingFeaturePointModel`/`TrainingFeaturePointResolveRequest`/`TrainingFeaturePointSetModelRequest`） | `contracts/training.openapi.yaml` | 契约自洽；错误口径 400 `unsupported capability`/`model not qualified`、404/409 与既有 24 端点一致 |
| 1.2 | `/api/training/feature-points/**` 3 条路径 4 个 action 镜像（query/body/状态码透传，上游不可达 502 `{"error":"training service unavailable"}`） | `contracts/bilibili-crawler.openapi.yaml` | 与 1.1 一致 |
| 1.3 | `npm run codegen` 再生成 `schema.d.ts`/`schema-training.d.ts`（勿手改） | `apps/web/src/api/` | 生成物与契约一致 |

### Phase 2: 共享域 servers/common/training（TDD：每任务测试先行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | 能力常量与纯函数：`Audio/Video/Text/Image` 四值 + `Normalize`（trim + 小写 + 去重 + 白名单，未知值抛 `CapabilityException`）+ `Matches`（`required ⊆ model`，输入/输出各判一次） | `servers/common/training/Contracts/TrainingModelCapabilities.cs`（新增） | `TrainingModelCapabilitiesTests` 转绿（大小写/去重/非法值/空集合语义：模型能力为空不匹配任何非空要求） |
| 2.2 | `TrainingModelRecord` + `InputCapabilities`/`OutputCapabilities`（`string[]`，缺省空 = 未声明，不进入任何功能点可用模型） | `servers/common/training/Entities/TrainingModelRecord.cs` | 编译通过、既有用例不回退 |
| 2.3 | `TrainingFeaturePointRecord`（`Slug`/`Name`/能力要求/`ModelId?`/`CreatedAt`/`UpdatedAt`）+ `TrainingFeaturePointRepository`（`ListAsync` Slug 升序、`GetBySlugAsync`、`RegisterAsync` 按 Slug upsert（`SetOnInsert CreatedAt`，已存在仅更新 Name/能力要求/UpdatedAt）、`SetModelAsync` 指定/置空） | `servers/common/training/Entities/TrainingFeaturePointRecord.cs`、`Persistence/TrainingFeaturePointRepository.cs`（新增） | `TrainingFeaturePointRepositoryTests` 转绿 |
| 2.4 | `TrainingCollectionNames` + `training_feature_points`；`TrainingMongoSetup.EnsureIndexesAsync` + `Slug` 唯一索引 | `servers/common/training/Mongo/` | `CollectionNamesEquivalenceTests`、`TrainingMongoSetupTests`、`MongoDbFixture.AllCollectionsCore` 同步登记 |
| 2.5 | `TrainingModelRepository.ListAsync(enabled?, input?, output?)` 能力过滤（`Normalize` 后内存 `Matches` 判定，模型量级数十条） | `servers/common/training/Persistence/TrainingModelRepository.cs` | `TrainingModelRepositoryTests` 补过滤用例（含空能力模型不入候选） |
| 2.6 | 存量能力清洗 `TrainingMongoSetup.BackfillModelCapabilitiesAsync`：marker `training_model_capabilities_backfill_v1` 落 `zvtuber_meta`（先例 `TranscriptMongoSetup.CleanupAsync` 手法，`BsonDocument` 级 `Exists` 判定 + 逐侧 `$set`：输入 `["audio","text"]`、输出 `["text"]`，只补缺失字段、不覆盖已设置） | `servers/common/training/Mongo/TrainingMongoSetup.cs` | `TrainingModelCapabilityBackfillTests` 转绿（补齐/单侧缺失只补单侧/已设置不覆盖/marker 幂等重复执行零副作用） |

### Phase 3: training 服务

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 3.1 | 功能点常量：`dataset-clean`（输入 `audio,text`/输出 `text`）、`emotion-from-audio`（输入 `audio,text`/输出 `text`）、`emotion-from-text`（输入 `text`/输出 `text`），含中文名（`数据清洗`/`输入语音识别情绪`/`输入文本识别情绪`） | `servers/training/FeaturePoints/FeaturePointIds.cs`（新增） | 与 PRD §3.2 能力表一一对应 |
| 3.2 | `TrainingModelResolver`：`ModelResolution(Slug, ModelId?, ModelSlug?, Source)` + `ITrainingModelResolver`（`ResolveAsync`：Normalize → 登记 upsert → `ListCandidatesAsync`（Enabled + 能力匹配，`CreatedAt` 升序）→ 指定命中 `specified` → 首候选 `default`（指定存在但不可用同样回 `default`）→ 无 `none`；`ListCandidatesAsync` 供指定校验与列表标注复用） | `servers/training/FeaturePoints/TrainingModelResolver.cs`（新增） | `TrainingModelResolverTests` 转绿（指定优先/默认取最早/指定失效回退/无候选 none/登记更新能力要求） |
| 3.3 | 模型能力读写与过滤：create/update 接受能力数组（`JsonElement?` 缺失/显式 null 区分口径：缺省不改、空数组清空；非法 400 `unsupported capability`）；`GET /api/training/models` 增 `input`/`output` 过滤；`ToModelDto` + 能力字段映射 | `servers/training/Controllers/TrainingModelsController.cs`、`Contracts/Dtos.cs`、`Contracts/TrainingMapping.cs` | `TrainingModelCapabilityApiTests` 转绿（400/404/409 回归不回退） |
| 3.4 | `TrainingFeaturePointsController`（`[Route("api/training/feature-points")]`）4 端点：列表（实时计算 `specifiedModelAvailable`/`defaultModelId`/`defaultModelSlug`）、resolve（400 `feature point slug is required`/`unsupported capability`）、PUT 指定（未登记 404 `feature point not found`、模型不存在 404 `model not found`、非可用模型 400 `model not qualified`）、DELETE 取消（置空恢复默认口径）；错误体统一 `{"error":"..."}` | `servers/training/Controllers/TrainingFeaturePointsController.cs`（新增） | `TrainingFeaturePointApiTests` 转绿（全链路 + 失效标注 + 恢复回优先） |
| 3.5 | 合成情绪解析改走功能点：`ResolveEmotionModelAsync` 三级回退改「显式 `modelId` → resolver（slug `emotion-from-text`）→ null（情绪降级中性）」；`Synthesize:DefaultEmotionModelId` 移除（`SynthesizeOptions` 删属性，启动时读 `IConfiguration` 残留值非空则 Warn 一次并忽略） | `servers/training/Controllers/TrainingSynthesizeController.cs`、`Configuration/SynthesizeOptions.cs` | `TrainingSynthesizeApiTests`/`SynthesizeServiceTests` 既有用例不回退（显式优先、无模型降级中性） |
| 3.6 | DI 注册与启动：`ITrainingModelResolver`/`TrainingFeaturePointRepository`；启动 `EnsureIndexesAsync()` 后执行 `BackfillModelCapabilitiesAsync()`（幂等） | `servers/training/Program.cs` | 启动幂等、重复执行零副作用 |

### Phase 4: api 代理镜像

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 4.1 | 4 个镜像 action：`GET training/feature-points`、`POST training/feature-points/{slug}/model/resolve`（slug `Uri.EscapeDataString`）、`PUT`/`DELETE training/feature-points/{slug}/model`，走既有 `ForwardAsync`（200/400/404 透传） | `servers/api/Controllers/TrainingProxyController.cs` | `TrainingProxyControllerTests` 补新路由透传用例 |
| 4.2 | 超时口径回归（`TrainingService:RequestTimeoutSeconds` 180s > 上游 120s 预算，不变） | tests | `TrainingProxyTimeoutConsistencyTests` 全绿 |

### Phase 5: web 前端

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 5.1 | `api/client.ts`：`listTrainingModels(enabled?, input?, output?)` 扩参 + `listTrainingFeaturePoints`/`resolveFeaturePointModel`/`setFeaturePointModel`/`clearFeaturePointModel`；`apiErrorMessage` 补 `unsupported capability`/`model not qualified`/`feature point not found`/`feature point slug is required` 中文文案 | `apps/web/src/api/client.ts` | `client.test.ts` 补用例 |
| 5.2 | `ModelSettingsTab`：Models 表 +「输入能力」「输出能力」两列（能力标签展示、空能力「未设置」提示补全）；模型手动添加/编辑表单 + 能力勾选组（语音/视频/文本/图片 × 输入/输出，默认无勾选） | `apps/web/src/pages/training/ModelSettingsTab.tsx` | `ModelSettingsTab.test.tsx` 补用例（列展示/勾选保存/空态） |
| 5.3 | 新增 `FeaturePointPanel`：功能点列表（名称/标识/所需输入输出能力/当前模型三态：未设置（展示「未设置」并附默认模型 slug 提示「未设置（默认 {slug}）」）/指定模型 slug/指定不可用标注）、「指定模型」下拉（候选 `listTrainingModels({enabled:true, input, output})` 按该功能点能力过滤）、「恢复默认」按钮（仅已指定时出现） | `apps/web/src/pages/training/FeaturePointPanel.tsx`（新增） | `FeaturePointPanel.test.tsx` 转绿（三态/指定/恢复默认/失效标注/无可用模型引导） |
| 5.4 | 两条入口接入功能点口径：`ReRecognizeDialog`（`dataset-clean`，`input=audio,text&output=text`）与 `EmotionSubmitDialog`（`emotion-from-audio`，同口径）加载时调 resolve（完成自动登记并取解析结果）→ 候选按能力过滤 → 默认选中解析结果（`specified` 优先、`default` 兜底）→ 无候选引导提示「先在模型设置中配置模型与能力」 | `apps/web/src/pages/training/ReRecognizeDialog.tsx`、`EmotionSubmitDialog.tsx` | 两组件测试补用例（候选过滤/默认选中/临时改选不拦截/无候选引导） |
| 5.5 | `ModelSettingsTab` 挂载 `FeaturePointPanel`（Providers、Models 同级区块，加载不阻塞主流程） | `apps/web/src/pages/training/ModelSettingsTab.tsx` | 随 5.3 |

### Phase 6: 测试收口与文档同步

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 6.1 | 全量回归：`dotnet build` + podman 容器 UT（Mongo）+ `npm run test`/`npm run lint`；既有 `TrainingModelApiTests`/`TrainingRecognizeTests`/`EmotionTaskApiTests`/`EmotionTaskRunnerTests`/`TrainingSynthesizeApiTests`/`SynthesizeServiceTests`/`TrainingSynthesizeProxyTests`/`TrainingMappingTests` 不回退 | 全部 | 全绿 |
| 6.2 | 跑测口径：trx + `tests/Z.Vtuber.TestReport --check`（单用例 ≤ 5s，新增慢用例须登记 `TimeoutExceptions.json`） | 全部 | 通过 |
| 6.3 | 覆盖率：核心逻辑（`Normalize`/`Matches`、解析优先级与默认选择、登记 upsert、清洗判定、DTO 映射）≥ 80% | 全部 | 达标 |
| 6.4 | `npm run codegen` 复核 0 diff（生成物勿手改） | `apps/web/src/api/` | 一致 |
| 6.5 | 文档同步：`servers/training/AGENTS.md`（能力字段/`training_feature_points` 集合/feature-points 4 端点/API 面表/`FeaturePoints` 模块/`Synthesize` 配置移除 `DefaultEmotionModelId`）、`servers/common/AGENTS.md`（training 域新增实体与仓储）、`servers/api/AGENTS.md`（24 → 28 action）、`apps/web/AGENTS.md`（能力列/`FeaturePointPanel`/入口过滤）；PRD 验收项勾选、提案/Plan 互链 | 文档 | 与实现一致 |

## 4. 并行策略

- Phase 1 契约先行（1.1 → 1.2 → 1.3）；1.2/1.3 与 Phase 2 可并行（契约文件与 common 代码无冲突）。
- Phase 3 依赖 Phase 2（纯函数/实体/仓储）；3.1/3.2 先于 3.3–3.5；3.3 与 3.4 可并行（不同控制器文件），3.5 依赖 3.2。
- Phase 4 依赖 1.2 契约定稿与 3.4 路由（路径/状态码）；Phase 5 依赖 1.3 codegen；5.2/5.3/5.4 可并行（不同组件文件）。
- Phase 6 收口最后；6.5 文档同步在实现定稿后。

## 5. 验收对照

| PRD/提案目标 | 实现任务 | 验证 |
|--------------|---------|------|
| 模型输入/输出能力四值多选、非法 400、列表可见 | 2.1、2.2、3.3、5.2 | 2.1、3.3 测试；PRD 功能点 1 |
| 存量清洗「输入：语音、文本；输出：文本」，幂等不覆盖已设置 | 2.6 | 2.6 测试 |
| 可用模型 = 启用 + 能力覆盖；三条链路候选按能力过滤、无候选引导 | 2.5、3.2、3.3、5.2、5.4 | 2.5、3.2、3.3 测试；PRD 功能点 2 |
| 功能点 slug 自动登记（名称/能力要求随声明更新） | 2.3、3.2、3.4 | 2.3、3.2、3.4 测试 |
| 指定优先返回 → 默认（可用模型中 `CreatedAt` 最早）→ 无 | 3.2、3.4、3.5 | 3.2 测试；PRD 功能点 3 |
| 可取消指定恢复未设置，默认即未设置、与指定前一致 | 3.4、5.3 | 3.4 测试；PRD 功能点 3 |
| 指定模型删除/停用/能力不匹配 → 回退默认 + 标注，恢复后回优先（指定不被清除） | 2.3、3.2、3.4、5.3 | 3.2、3.4 测试；PRD 3.5 边界 |
| 指定只能从可用模型中选（否则 400 `model not qualified`） | 3.4、5.3 | 3.4 测试 |
| 合成情绪：显式优先不变、无可用模型降级中性 | 3.5 | 3.5 回归用例 |
| 契约 + api 镜像 + codegen 一致 | 1.1–1.3、4.1 | 6.1、6.4 |
| 回归不回退、核心逻辑覆盖率 ≥ 80%、单用例 ≤ 5s | 6.1–6.3 | 6.1–6.3 |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|----|------|------|
| 行为变更 | `Synthesize:DefaultEmotionModelId` 移除，配置过该值的部署「合成情绪默认模型」口径变为功能点默认（可用模型中 `CreatedAt` 最早） | 提案决策 11；启动读残留配置非空则 Warn 一次并忽略；迁移路径 = 在功能点区块「指定模型」指定到原模型，回滚安全（能力字段与新集合对旧版本无害） |
| 精度口径 | 指定模型实时判定「可用」（`defaultModelId`/`specifiedModelAvailable` 读取时计算，不落库） | 模型增删改立即反映；`TrainingFeaturePointRepository` 不做外键级联，`ModelId` 保留 |
| 并发登记 | resolve 并发同 slug upsert | `Slug` 唯一索引 + `RegisterAsync` upsert（`SetOnInsert CreatedAt`） |
| 任务面 | 情绪任务沿用 `ModelId`/`ModelSlug` 快照 + `Enabled` 校验，不因调能力使已入队任务失败 | 提案决策 10；能力是「选择期」约束 |
| 测试时长 | 新增迁移/解析/API 用例不得拖慢套件 | 共享 `MongoDbFixture`、`TestWait.UntilAsync` 轮询断言、5s 护栏 + `TestReport --check` |
| 部署顺序 | 先 training（新集合/索引/清洗），再 api 代理，最后 web | 纯增量面，旧版本无感（`[BsonIgnoreExtraElements]`）；回滚不影响已清洗数据 |
| 无新增依赖 | 不新增配置/环境变量/流水线/引用（`ProjectReferenceGuardTests`、`WorkflowPathsGuardTests` 口径不变） | - |

## 7. 相关文档

- PRD：`docs/product/draft/20261001-dataset-model-capability-prd.md`
- 技术提案：`docs/engineering/proposals/20261001-proposal-dataset-model-capability.md`
- 关联 PRD/提案：`20260928-training-dataset-emotion-prd.md`、`20260927-training-audio-dataset-prd.md`、`20260928-proposal-training-dataset-emotion.md`、`20260927-proposal-training-audio-dataset.md`
- 存量清洗先例：`docs/engineering/plans/20260917-plan-transcript-model-isdefault-fix.md`
- 模块规范：`servers/training/AGENTS.md`、`servers/common/AGENTS.md`、`servers/api/AGENTS.md`、`apps/web/AGENTS.md`
- 规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`docs/engineering/frontend-rules.md`
- 既有契约：`contracts/training.openapi.yaml`、`contracts/bilibili-crawler.openapi.yaml`
