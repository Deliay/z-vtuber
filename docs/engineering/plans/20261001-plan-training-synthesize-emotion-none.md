---
description: AI 训练模块「音频生成 - 情绪参考关闭（emotionMode=none）与默认值调整」实现 Plan：synthesize 的 query emotionMode 新增 none 并把缺省值由 llm 改为 none，none 不传任何情绪参考且跳过 modelId/模型解析；双契约 + web codegen，TDD 覆盖服务/控制器
type: Permanent
---

# AI 训练 - 音频生成情绪参考关闭（emotionMode=none） - 实现 Plan

**功能名称**: 训练 - 音频生成情绪参考关闭（emotionMode=none）与默认值调整
**关联 PRD**: `docs/product/draft/20260929-training-audio-synthesize-prd.md`
**关联技术提案**: `docs/engineering/proposals/20261001-proposal-training-synthesize-emotion-none.md`（v1.0）
**Plan 版本**: v1.0
**创建日期**: 2026-10-01
**作者**: Workflow Agent（代起草）
**feat-branch**: `feat/training-synthesize-emotion-none`

> 口径说明（2026-10-01 用户确认）：`emotionMode` 新增 `none`（不传任何情绪参考），且缺省值由 `llm` 调整为 `none`；`dataset`/`none` 均**完全跳过 modelId/模型解析**。此为对 `synthesize` 默认行为的变更，依赖缺省 `llm` 的调用方需显式传 `emotionMode=llm`。

## 1. 概述

在前序 `emotionMode=llm|dataset` 基础上，新增「关闭情绪参考」模式并调整默认值。改动分三条线：① 编排——`Synthesis/EmotionMode.cs` 增 `None` + `SynthesizeService` 默认 `None` 与 none 分支；② 接口——`TrainingSynthesizeController` 解析 `none`、缺省改 `none`、非 llm 跳过模型解析；③ 接入面——双 OpenAPI + web codegen。实施遵循 `docs/engineering/common-rules.md`（契约先行、TDD、覆盖率 ≥ 80%、单用例 ≤ 5s）、`backend-rules.md`、z-coding 流程与 `servers/training/AGENTS.md`。

### 1.0 现状核实结论

- `Synthesis/EmotionMode.cs`：既有 `Llm = 0` / `Dataset = 1`。
- `SynthesizeService.SynthesizeAsync(..., EmotionMode emotionMode = EmotionMode.Llm, ...)`：`embedding → SearchTextAsync → ScanCandidatesAsync(speaker) →（dataset|llm 情绪参考）→ ITtsClient`。
- `TrainingSynthesizeController.TryParseEmotionMode`：`null/空` → `Llm`；`llm`/`dataset` 匹配；其他 400。
- `servers/api` `ForwardAsync` 用 `Request.QueryString.Value` 拼接目标路径 → **query 天然透传**，代理实现无需改。
- codegen：`apps/web/package.json` `codegen` 覆盖 training → `schema-training.d.ts`（另经 bilibili-crawler → `schema.d.ts`）。
- 测试范式：`TrainingSynthesizeApiTests`（控制器直测 + `MongoDbFixture` + fakes）、`SynthesizeServiceTests`（service 直测 + `StubChatFactory`）。

### 1.1 实施范围

- **做**：`servers/training`（`EmotionMode.cs`、`SynthesizeService.cs`、`TrainingSynthesizeController.cs`）；`contracts/`（training + bilibili-crawler 的 `emotionMode` enum/默认值/描述）+ web codegen；`tests/Z.Vtuber.Tests`（服务/控制器用例）；`servers/training/AGENTS.md`、`servers/api/AGENTS.md` 同步。
- **不做**：`llm`/`dataset` 语义改动；新端点/新配置/新依赖/数据迁移；Web 合成界面；批量合成。

### 1.2 现状基线（范本）

- 控制器 query 白名单口径：`lang` 白名单（`SupportedLangs`）→ 400 `invalid lang`，本模式照此。
- service 候选扫描/降级：既有 `ScanCandidatesAsync` + `BuildDatasetEmotionReference` / `TryBuildEmotionReferenceAsync`。
- 契约 query 参数范本：`EmotionModeQuery` / `TrainingEmotionModeQuery`（前序）。

## 2. 分支与提交流程

- feat-branch `feat/training-synthesize-emotion-none`，基于 `release`。
- 提交顺序：`docs(training):` 提案/Plan → `feat(contracts):` 契约 + codegen → `feat(training):` 实现 → `test(training):` 测试 → `docs(training):` 模块规范。Conventional Commits，测试与实现分开。

## 3. 实施阶段

### Phase 0: 技术文档（先行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | 技术提案 | `docs/engineering/proposals/20261001-proposal-training-synthesize-emotion-none.md` | 与本 Plan 口径一致 |
| 0.2 | 实现 Plan | `docs/engineering/plans/20261001-plan-training-synthesize-emotion-none.md` | 任务/文件/验收明确 |

### Phase 1: 契约先行

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | `training.openapi.yaml`：`EmotionModeQuery` enum 增 `none`、默认改 `none`，summary/description 更新 | `contracts/training.openapi.yaml` | 契约自洽 |
| 1.2 | `bilibili-crawler.openapi.yaml`：`TrainingEmotionModeQuery` 同步 | `contracts/bilibili-crawler.openapi.yaml` | 与 1.1 一致 |
| 1.3 | `npm run codegen` 再生成 `schema.d.ts`/`schema-training.d.ts`（勿手改） | `apps/web/src/api/*` | 生成物与契约一致 |

### Phase 2: 实现

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | `EmotionMode` 增 `None = 2`，注释更新 | `servers/training/Synthesis/EmotionMode.cs` | 编译通过 |
| 2.2 | `SynthesizeService`：默认参数改 `EmotionMode.None`；`None` 直接返回无情绪参考（不进 dataset/llm 链路） | `servers/training/Synthesis/SynthesizeService.cs` | 用例转绿 |
| 2.3 | `TrainingSynthesizeController.TryParseEmotionMode`：默认 `none`、匹配 `none`；仅 `Llm` 解析模型 | `servers/training/Controllers/TrainingSynthesizeController.cs` | 用例转绿 |

### Phase 3: 测试与收口

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 3.1 | `SynthesizeServiceTests`：默认 none 不调用大模型且不传情绪；显式 llm 回归 | `tests/Z.Vtuber.Tests/SynthesizeServiceTests.cs` | 全绿 |
| 3.2 | `TrainingSynthesizeApiTests`：默认 none / 显式 none 跳过模型解析；llm 回归仍 404/400 | `tests/Z.Vtuber.Tests/TrainingSynthesizeApiTests.cs` | 全绿 |
| 3.3 | `dotnet build` + 相关 `dotnet test` 全绿；`npm run codegen` 复核 | 全部 | 通过 |
| 3.4 | 文档同步：`servers/training/AGENTS.md`、`servers/api/AGENTS.md`；提案/Plan 互链 | 文档 | 与实现一致 |

## 4. 并行策略

- Phase 0/1 串行先行（文档 → 契约 → codegen）。
- Phase 2/3 线内串行：2.1→2.2→2.3；3.x 依附实现；文件无冲突。

## 5. 验收对照

| 提案目标 | 实现任务 | 验证 |
|-----------|---------|------|
| query `emotionMode=none|llm|dataset`、默认 none | 1.1、1.2、2.3 | 3.2 |
| none 不传情绪参考 | 2.1、2.2 | 3.1、3.2 |
| none 跳过大模型与 modelId/模型解析 | 2.2、2.3 | 3.2 |
| 非法 emotionMode → 400 | 2.3、1.1 | 3.2 |
| llm/dataset 零回退 | 2.2、1.1 | 3.1、3.2 既有用例 |
| 双契约与 codegen 一致 | 1.1–1.3 | 3.3 |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| 默认值变更 | 缺省不再等价 llm | 提案/Plan/AGENTS 明确；依赖缺省 llm 的调用方显式传 `emotionMode=llm` |
| 与「音色 speaker 与情绪来源不同条目」 | none 无情绪来源，不涉及 | - |
| 无新增依赖/迁移 | 纯编排分支 | - |

## 7. 相关文档

- PRD：`docs/product/draft/20260929-training-audio-synthesize-prd.md`
- 技术提案：`docs/engineering/proposals/20261001-proposal-training-synthesize-emotion-none.md`
- 前序提案/Plan：`20260930-proposal-training-synthesize-emotion-mode.md`、`20260930-plan-training-synthesize-emotion-mode.md`
- 模块规范：`servers/training/AGENTS.md`、`servers/api/AGENTS.md`
- 规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`
