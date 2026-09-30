---
description: AI 训练模块「音频生成 - 音色参考字符相似匹配」实现 Plan：synthesize 的音色参考由「Ollama embedding + Milvus 文本向量 COSINE」改为「输入文本与条目 Text 的归一化编辑距离（Levenshtein ratio，内容 + 长度）全量字符匹配」，同分 itemId 升序；情绪参考仍走 Milvus 八维向量；双契约 + web codegen，TDD 覆盖相似度/服务/控制器
type: Permanent
---

# AI 训练 - 音频生成音色参考字符相似匹配 - 实现 Plan

**功能名称**: 训练 - 音频生成音色参考字符相似匹配
**关联 PRD**: `docs/product/draft/20260929-training-audio-synthesize-prd.md`
**关联技术提案**: `docs/engineering/proposals/20261001-proposal-training-synthesize-char-match.md`（v1.0）
**Plan 版本**: v1.0
**创建日期**: 2026-10-01
**作者**: Workflow Agent（代起草）
**feat-branch**: `feat/training-synthesize-char-match`

> 口径说明（2026-10-01 用户确认）：仅**音色参考**改为按输入字符最相似匹配（内容 + 长度，归一化编辑距离）；**情绪参考**保持向量匹配；候选**直接查 Mongo 全量条目**；**保留** Milvus/情绪向量与文本集合（文本侧不再用于音色）。

## 1. 概述

在既有 `synthesize`（音色向量 top-1 + 情绪按 `emotionMode` 取参考）之上，替换音色参考的匹配口径。改动分三条线：① 匹配算法——新增 `Synthesis/TextSimilarity.cs`（归一化编辑距离）；② 编排——`SynthesizeService` 音色候选人 Mongo 全量字符匹配、`ReferenceMatch` 收敛、移除 `IEmbeddingClient` 依赖；③ 接入面——双 OpenAPI 描述/502 + web codegen。实施遵循 `docs/engineering/common-rules.md`（契约先行、TDD、覆盖率 ≥ 80%、单用例 ≤ 5s）、`backend-rules.md`、z-coding 流程与 `servers/training/AGENTS.md`。

### 1.0 现状核实结论

- `Synthesis/EmotionMode.cs`：既有 `Llm`/`Dataset`/`None`（默认 `None`）。
- `SynthesizeService.SynthesizeAsync`：`embedding → store.SearchTextAsync → ScanCandidatesAsync(speaker) →（情绪参考按 mode）→ ITtsClient`；`ReferenceMatch(Item, Clip, Match)`。
- `ScanCandidatesAsync(datasetId, IReadOnlyList<VectorMatch>, captureDatasetEmotion, ct)`：按 `match.ItemId` 回读 Mongo 条目再取音频。
- `TrainingDatasetItemRepository.ListAsync(datasetId, ...)`：返回数据集全部条目（含 `Text`）。
- codegen：`apps/web/package.json` `codegen` 覆盖 training → `schema-training.d.ts`（另经 bilibili-crawler → `schema.d.ts`）。
- 测试范式：`TrainingSynthesizeApiTests`（控制器直测 + `MongoDbFixture` + `MilvusFixture` + fakes）、`SynthesizeServiceTests`（service 直测 + `MongoDbFixture`）。

### 1.1 实施范围

- **做**：`servers/training`（`Synthesis/TextSimilarity.cs` 新增、`SynthesizeService.cs`、`TrainingSynthesizeController.cs` 注释）；`contracts/`（training + bilibili-crawler 描述/502）+ web codegen；`tests/Z.Vtuber.Tests`（新增 `TextSimilarityTests`、调整服务/控制器用例）；`servers/training/AGENTS.md` + PRD 同步。
- **不做**：情绪来源语义、`EmotionVector`、Milvus 情感集合、`IndexTtsClient`、数据模型、`vectors/rebuild`、`servers/api` 代理实现；不删除文本 embedding/文本集合；不新增端点/配置/依赖。

### 1.2 现状基线（范本）

- 控制器错误矩阵：`text is required`/`invalid lang`/`invalid emotion mode`/`model not found`/`model disabled`/`no speaker reference`/`audio unavailable`/`tts service unavailable`（本 Plan 不动）。
- 候选扫描与降级：既有 `ScanCandidatesAsync` + `BuildDatasetEmotionReference` / `TryBuildEmotionReferenceAsync`（复用）。
- 确定性 tie-break 范本：Milvus 检索「相似度降序、itemId 升序」——本次内存排序沿用同口径。

## 2. 分支与提交流程

- feat-branch `feat/training-synthesize-char-match`，基于 `release`。
- 提交顺序：`docs(training):` 提案/Plan → `feat(training):` 实现（`TextSimilarity` + `SynthesizeService`）→ `feat(contracts):` 契约 + codegen → `test(training):` 测试 → `docs(training):` 模块规范。Conventional Commits，测试与实现分开。

## 3. 实施阶段

### Phase 0: 技术文档（先行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | 技术提案 | `docs/engineering/proposals/20261001-proposal-training-synthesize-char-match.md` | 与本 Plan 口径一致 |
| 0.2 | 实现 Plan | `docs/engineering/plans/20261001-plan-training-synthesize-char-match.md` | 任务/文件/验收明确 |

### Phase 1: 实现

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | 新增 `TextSimilarity.Ratio` + 两行滚动数组 Levenshtein（空串/相等口径） | `servers/training/Synthesis/TextSimilarity.cs` | 单测转绿 |
| 1.2 | `SynthesizeService`：新增 `RankSpeakerCandidatesAsync`（Mongo 全量、过滤空 Text、Ratio 降序 + itemId 升序、Take Limit）；`ScanCandidatesAsync` 改收条目；`ReferenceMatch` 收敛 `(Item, Clip)`；移除 `IEmbeddingClient` | `servers/training/Synthesis/SynthesizeService.cs` | 编译通过、用例转绿 |
| 1.3 | `llm` 情绪链路：8 维检索命中后回 Mongo 取条目再扫描音频 | `servers/training/Synthesis/SynthesizeService.cs` | 既有 llm 用例转绿 |
| 1.4 | 控制器注释口径同步（字符相似音色匹配/dataset 用音色命中条目情绪） | `servers/training/Controllers/TrainingSynthesizeController.cs` | 与实现一致 |

### Phase 2: 契约先行收口

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | `training.openapi.yaml`：synthesize description 改字符相似；502 收敛为 `audio`/`tts` | `contracts/training.openapi.yaml` | 契约自洽 |
| 2.2 | `bilibili-crawler.openapi.yaml`：synthesize 502 描述同步 | `contracts/bilibili-crawler.openapi.yaml` | 与 2.1 一致 |
| 2.3 | `npm run codegen` 再生成 `schema.d.ts`/`schema-training.d.ts`（勿手改） | `apps/web/src/api/*` | 生成物与契约一致 |

### Phase 3: 测试与收口

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 3.1 | `TextSimilarityTests`：相等/空串/替换/长度惩罚/对称/排序性 | `tests/Z.Vtuber.Tests/TextSimilarityTests.cs` | 全绿 |
| 3.2 | `SynthesizeServiceTests`：音色改字符匹配（最相似命中、空文本跳过、次优兜底、全失败 502）；embedding 用例改情绪向量降级 | `tests/Z.Vtuber.Tests/SynthesizeServiceTests.cs` | 全绿 |
| 3.3 | `TrainingSynthesizeApiTests`：新增最相似命中；真 Milvus 用例更名/断言字符命中；移除 synthesize embedding-502 用例 | `tests/Z.Vtuber.Tests/TrainingSynthesizeApiTests.cs` | 全绿 |
| 3.4 | `dotnet build` + podman 容器 UT（Mongo/Milvus）全绿；`npm run codegen` 复核 0 diff | 全部 | 通过 |
| 3.5 | 文档同步：`servers/training/AGENTS.md`、PRD；提案/Plan 互链 | 文档 | 与实现一致 |

## 4. 并行策略

- Phase 0/1 串行先行（文档 → 算法 → 编排）；Phase 2 与 3.1–3.3 可并行（文件无冲突：契约/codegen vs 测试文件）。
- 1.1 与 1.2 有依赖（1.2 调 `TextSimilarity`）；1.2/1.3 同文件串行。

## 5. 验收对照

| 提案目标 | 实现任务 | 验证 |
|-----------|---------|------|
| 音色按归一化编辑距离匹配（内容 + 长度） | 1.1、1.2 | 3.1、3.2 |
| 候选直读 Mongo、不再经 embedding/Milvus 文本 | 1.2 | 3.2、3.3 |
| 同分 itemId 升序稳定 tie-break | 1.2 | 3.2 |
| 情绪 `llm`/`dataset`/`none` 零回退 | 1.3 | 3.2、3.3 既有用例 |
| 空文本跳过 / 无候选 409 / 全失败 502 | 1.2 | 3.2 |
| 双契约与 codegen 一致 | 2.1–2.3 | 3.4 |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| 行为变更 | 音色由语义相似改为字面相似，语义近但字面远的条目不命中 | 提案/Plan/PRD/AGENTS 明确；符合「越像越好」预期 |
| 性能 | Levenshtein O(m·n) × 条目数；长文本 × 大数据集成本升高 | 千级短文本可忽略；候选上限 `ReferenceCandidateLimit` 约束音频取数；必要时后续加长度上限/预筛 |
| 502 收敛 | synthesize 不再报 embedding/vector-store 不可用 | 契约同步；情绪向量不可用降级中性 |
| 无新增依赖/迁移 | Mongo 现有字段直读 | - |

## 7. 相关文档

- PRD：`docs/product/draft/20260929-training-audio-synthesize-prd.md`
- 技术提案：`docs/engineering/proposals/20261001-proposal-training-synthesize-char-match.md`
- 前序提案/Plan：`20260929-proposal-training-audio-synthesize.md`、`20260930-proposal-training-synthesize-emotion-mode.md`、`20261001-proposal-training-synthesize-emotion-none.md`
- 模块规范：`servers/training/AGENTS.md`、`servers/api/AGENTS.md`
- 规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`
