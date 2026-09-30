---
description: AI 训练模块「音频生成 - 情绪参考来源可选（emotionMode）」实现 Plan：在 synthesize 增加 query emotionMode=llm|dataset，llm 保留大模型生成情感向量 + Milvus 八维检索，dataset 跳过大模型直接用音色命中条目自身 Extensions.emotion（无则沿候选向下找、全无降级中性），dataset 完全跳过 modelId/模型解析；双契约 + api 代理 query 透传 + web codegen，TDD 覆盖控制器/服务/代理
type: Permanent
---

# AI 训练 - 音频生成情绪参考来源可选（emotionMode） - 实现 Plan

**功能名称**: 训练 - 音频生成情绪参考来源可选（emotionMode）
**关联 PRD**: `docs/product/draft/20260929-training-audio-synthesize-prd.md`
**关联技术提案**: `docs/engineering/proposals/20260930-proposal-training-synthesize-emotion-mode.md`（v1.0）
**Plan 版本**: v1.0
**创建日期**: 2026-09-30
**作者**: Workflow Agent（代起草）
**feat-branch**: `feat/training-synthesize-emotion-mode`

> 口径说明（2026-09-30 用户确认）：`emotionMode=dataset` 时情绪参考取**文本向量命中（音色）条目自身**的 `Extensions.emotion`；命中条目无情绪则**继续沿音色候选（文本向量 top-k）向下找**首个带情绪且音频可取的条目；全部无情绪 → **降级中性**；dataset 模式**完全跳过 modelId/模型解析**。query 命名采用 camelCase `emotionMode`。本次不提交 commit（用户选择），仅产出工作区改动。

## 1. 概述

在既有 `synthesize`（`servers/training`）上增加情绪来源开关。改动分三条线：① 编排——`Synthesis/EmotionMode.cs`（新增枚举）+ `SynthesizeService`（单轮候选扫描，dataset 用命中条目情绪，llm 原样）；② 接口——`TrainingSynthesizeController` 解析 `emotionMode` query 并在 dataset 模式跳过模型解析；③ 接入面——双 OpenAPI + `servers/api` 代理声明 + web codegen。实施遵循 `docs/engineering/common-rules.md`（契约先行、TDD、覆盖率 ≥ 80%、单用例 ≤ 5s）、`backend-rules.md`、z-coding 流程与 `servers/training/AGENTS.md`。

### 1.0 现状核实结论

- `SynthesizeService.SynthesizeAsync(ObjectId, string, string, TrainingModelRecord?, CancellationToken)`（`servers/training/Synthesis/SynthesizeService.cs`）：`embedding → SearchTextAsync → FetchFirstAvailableAsync(speaker) → TryBuildEmotionReferenceAsync(大模型+SearchEmotionAsync) → ITtsClient`；情绪 best-effort，失败降级中性。
- `TrainingSynthesizeController.Synthesize(string datasetId, [FromBody] request, CancellationToken)`：dataset 404 → text 400 → lang 400 → `ResolveEmotionModelAsync`（llm 用）→ service。`ResolveEmotionModelAsync`：显式 modelId 404/400，缺省配置默认模型，再缺省首个启用模型。
- 条目实体 `TrainingDatasetItemRecord.Extensions.emotion`（`EmotionResult`）；`ReadEmotion`（service 内）/`TrainingMapping.FromEmotionDocument` 可读回；`ReferenceMatch(Item, Clip, Match)` 私有 record struct 已可承载候选。
- `servers/api` `TrainingProxyController.ForwardAsync` 用 `Request.QueryString.Value` 拼接目标路径 → **query 天然透传**，代理实现无需改。
- codegen：`apps/web/package.json` `codegen` 覆盖 training → `schema-training.d.ts`（另经 bilibili-crawler → `schema.d.ts`）。
- 测试范式：`TrainingSynthesizeApiTests`（控制器直测 + `MongoDbFixture` + `InMemoryVectorStore`/`FakeEmbeddingClient`/`StubAudioClient`/`StubTtsClient`/`ApiStubChatFactory`）、`SynthesizeServiceTests`（service 直测 + `StubChatFactory`）、`TrainingSynthesizeProxyTests`（`StubHandler`）。`SynthesizeService` 现有 9 处调用均 4 参；采用「可选参数默认 `Llm`」保持零改动。

### 1.1 实施范围

- **做**：`contracts/`（training + bilibili-crawler 各 + `emotionMode` query 与 `invalid emotion mode` 400）+ web codegen；`servers/training`（`Synthesis/EmotionMode.cs`、`SynthesizeService`、`TrainingSynthesizeController`）；`servers/api`（代理动作声明 `emotionMode`，转发不变）；`tests/Z.Vtuber.Tests`（3 个测试文件扩展）；`servers/training/AGENTS.md`、`servers/api/AGENTS.md` 同步。
- **不做**：`llm` 模式语义改动；新端点/新配置/新依赖/数据迁移；dataset 模式下的二次向量检索；Web 合成界面；批量合成；提交 commit（用户选择）。

### 1.2 现状基线（范本）

- 控制器 query 白名单口径：`lang` 白名单（`SupportedLangs`）→ 400 `invalid lang`，本次 `emotionMode` 照此。
- service 候选扫描/降级：既有 `FetchFirstAvailableAsync` + `TryBuildEmotionReferenceAsync`。
- 契约 query 参数范本：`AudioTypeQuery`/`TrainingModelEnabledQuery`。

## 2. 分支与提交流程

- 提案中标注 feat-branch `feat/training-synthesize-emotion-mode`；**本次按用户选择不提交 commit**，改动留在工作区供 review。
- 若后续提交：`docs(training):` → `feat(contracts):` → `feat(training):` / `feat(api):` → `test:`，Conventional Commits，测试与实现分开。

## 3. 实施阶段

### Phase 0: 技术文档（先行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | 技术提案 | `docs/engineering/proposals/20260930-proposal-training-synthesize-emotion-mode.md` | 与本 Plan 口径一致 |
| 0.2 | 实现 Plan | `docs/engineering/plans/20260930-plan-training-synthesize-emotion-mode.md` | 任务/文件/验收明确 |

### Phase 1: 契约先行

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | `training.openapi.yaml`：synthesize 增 `EmotionModeQuery`（`emotionMode`，enum `llm`/`dataset`，默认 `llm`），400 增 `invalid emotion mode`，summary/description 更新 | `contracts/training.openapi.yaml` | 契约自洽 |
| 1.2 | `bilibili-crawler.openapi.yaml`：synthesize 镜像 + `TrainingEmotionModeQuery` | `contracts/bilibili-crawler.openapi.yaml` | 与 1.1 一致 |
| 1.3 | `npm run codegen` 再生成 `schema.d.ts`/`schema-training.d.ts`（勿手改） | `apps/web/src/api/*` | 生成物与契约一致 |

### Phase 2: 实现

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | 新增 `EmotionMode { Llm, Dataset }` | `servers/training/Synthesis/EmotionMode.cs` | 编译通过 |
| 2.2 | `SynthesizeService`：`SynthesizeAsync` 增可选 `EmotionMode`（默认 `Llm`）；`FetchFirstAvailableAsync` → `ScanCandidatesAsync`（单轮扫描，dataset 同轮捕获情绪来源）；dataset 用 `BuildDatasetEmotionReference`，llm 走既有链路；日志加 `emotionMode` | `servers/training/Synthesis/SynthesizeService.cs` | 用例转绿 |
| 2.3 | `TrainingSynthesizeController`：动作增 `[FromQuery] string? emotionMode`；校验（合法 `llm`/`dataset`，否则 400 `invalid emotion mode`）；dataset 跳过 `ResolveEmotionModelAsync` | `servers/training/Controllers/TrainingSynthesizeController.cs` | 用例转绿 |
| 2.4 | `TrainingProxyController.SynthesizeDataset` 增 `[FromQuery] string? emotionMode` 形参（转发不变） | `servers/api/Controllers/TrainingProxyController.cs` | 编译通过 |

### Phase 3: 测试与收口

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 3.1 | `SynthesizeServiceTests`：dataset 成功（chat 零调用）、命中无情绪向下找、全无情绪降级中性、默认 llm 回归 | `tests/Z.Vtuber.Tests/SynthesizeServiceTests.cs` | 全绿 |
| 3.2 | `TrainingSynthesizeApiTests`：dataset 跳过模型解析（非法 modelId 不 404）、非法 mode 400、dataset 命中头 | `tests/Z.Vtuber.Tests/TrainingSynthesizeApiTests.cs` | 全绿 |
| 3.3 | `TrainingSynthesizeProxyTests`：`emotionMode` query 透传 | `tests/Z.Vtuber.Tests/TrainingSynthesizeProxyTests.cs` | 全绿 |
| 3.4 | `dotnet build` + `dotnet test tests/Z.Vtuber.Tests` 全绿（含既有回归）；`npm run codegen` 复核 | 全部 | 通过 |
| 3.5 | 文档同步：`servers/training/AGENTS.md`（synthesize query/口径/`EmotionMode`）、`servers/api/AGENTS.md`（镜像 query）；提案/Plan 互链 | 文档 | 与实现一致 |

## 4. 并行策略

- Phase 0/1 串行先行（文档 → 契约 → codegen）。
- Phase 2/3 线内串行：2.1→2.2→2.3（training 内）与 2.4（api）、3.x（测试）依附实现；文件无冲突（`servers/training`、`servers/api`、`tests` 各自独立）。

## 5. 验收对照

| 提案目标 | 实现任务 | 验证 |
|-----------|---------|------|
| query `emotionMode=llm|dataset`、默认 llm | 1.1、1.2、2.3 | 3.2、3.3 |
| dataset 跳过大模型、用命中条目情绪 | 2.1、2.2 | 3.1、3.2 |
| 无情绪沿音色候选向下找 / 全无降级中性 | 2.2 | 3.1 |
| dataset 完全跳过 modelId/模型解析 | 2.3 | 3.2 |
| 非法 emotionMode → 400 | 2.3、1.1 | 3.2 |
| llm 模式与既有端点零回退 | 2.2（可选默认）、1.1 | 3.4 既有用例 |
| 双契约与 codegen 一致 | 1.1–1.3 | 3.4 |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| 既有调用方未传 emotionMode | 默认 llm，行为不变 | 可选参数默认 `Llm`；回归用例 |
| dataset 情绪质量依赖标注 | 命中条目无标注时跟随候选 | 向下找 + 降级中性；`X-Emo-Audio-Key` 可观测 |
| 与「音色 speaker 与情绪来源不同条目」 | dataset 下 speaker 取首个可用音频、情绪取首个带标注，可能非同一候选 | 需求确认口径（情绪优先有标注）；`X-*` 头分别可观测 |
| 无新增依赖/迁移 | 纯编排分支 | - |

## 7. 相关文档

- PRD：`docs/product/draft/20260929-training-audio-synthesize-prd.md`
- 技术提案：`docs/engineering/proposals/20260930-proposal-training-synthesize-emotion-mode.md`
- 前序 Plan：`20260929-plan-training-audio-synthesize.md`
- 模块规范：`servers/training/AGENTS.md`、`servers/api/AGENTS.md`
- 规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`
