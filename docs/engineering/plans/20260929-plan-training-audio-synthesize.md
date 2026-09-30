---
description: AI 训练模块「音频生成（数据集语音合成）」实现 Plan——基于 PRD v1.0 与技术提案（2026-09-30 修订版）的契约先行与任务分解：training 新增 /synthesize 与 vectors/rebuild 两端点，文本向量走本地 Ollama embedding + Milvus（FLAT + 稳定 tie-break top-1 音色匹配），情感向量走配置的可用模型（EmotionPromptBuilder + IChatModelClient）+ Milvus 八维检索（情绪匹配），手写 multipart 调 index-tts-api 返回 audio/wav；api 代理镜像 +2 路由、AppHost 新增 Milvus；确定性口径（temperature=0 + tie-break）、参考音频取数失败口径与 vectors/rebuild 回填前置在本 Plan 收敛
type: Permanent
---

# AI 训练 - 音频生成（数据集语音合成） - 实现 Plan

**功能名称**: 训练 - 音频生成（数据集语音合成）
**关联 PRD**: `docs/product/draft/20260929-training-audio-synthesize-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20260929-proposal-training-audio-synthesize.md`（2026-09-30 修订版）
**Plan 版本**: v1.0
**创建日期**: 2026-09-30
**作者**: Workflow Agent（代起草）
**feat-branch**: `feat/training-audio-synthesize`（需求总分支，已推送 origin；需求总 PR：<https://github.com/Deliay/z-vtuber/pull/149>）

> 口径说明（2026-09-30 用户确认，已同步修订技术提案与 PRD）：**文本转向量（embedding）用本地 Ollama**（Milvus 相似匹配），不依赖三方；**情感向量识别（语音/台词与输入文本）用配置的可用模型**（既有 Models 管理的 OpenAI-like / Claude-like + `EmotionPromptBuilder`），不走 Ollama。本 Plan 按修订后口径编写。

## 1. 概述

本 Plan 基于 PRD v1.0 与技术提案（2026-09-30 修订版）编写，指导「AI 训练 - 音频生成（数据集语音合成）」的编码实施。改动分三条线：① 向量与匹配——`servers/training/Vectors/`（`LocalEmbeddingClient` 调本地 Ollama `/api/embeddings` 生成文本向量；`MilvusVectorStore` 两集合 `item_text_embeddings`（dim=`Embedding:Dimensions`，COSINE）/`item_emotion_vectors`（dim=8，L2），FLAT + 稳定 tie-break top-1；`VectorSyncService` Mongo→Milvus 重建/增量同步）；② 合成编排——`Synthesis/`（`EmotionVector` 八维 + `SynthesizeService`：文本向量 top-1 取 `speaker_prompt`、可用模型生成情感向量 top-1 取 `emo_prompt`/`emo_vector`）+ `Clients/IndexTtsClient`（手写 multipart 调 index-tts-api `/synthesize`）+ `TrainingSynthesizeController`（`synthesize` + `vectors/rebuild`）；③ 接入面——`contracts/` +2 端点与 api 代理镜像 +2 路由（`audio/wav` 二进制透传）、`infra/apphost` 新增 Milvus 资源与 `Embedding`/`Tts` BaseUrl 注入。实施全程遵循 `docs/engineering/common-rules.md`（契约先行、TDD、核心逻辑覆盖率 ≥ 80%）、`backend-rules.md`、z-coding 开发流程与各服务 `AGENTS.md`。

### 1.0 上游核实结论（代码现状复查）与偏差

对需求总分支 `feat/training-audio-synthesize` 代码逐项核实，提案技术路径**可行**，引用与现状描述属实；下列核实要点在对应任务中直接使用：

- **情绪结构与上游 `emo_vector` 对齐**：`EmotionResult`（`servers/training/Contracts/Dtos.cs:137`）十字段，八维顺序 `happy,angry,sad,afraid,disgusted,melancholic,surprised,calm` 与上游 `emo_vector` 一致；`emotionControlWeight`（0~10）→ `emo_alpha` 归一、`emotionDescription` → `emo_text`；`TrainingMapping.FromEmotionDocument` 可把条目 `Extensions.emotion` 读回 `EmotionResult`（缺失/格式不符 → null 不抛）。
- **条目实体**：`TrainingDatasetItemRecord`（`servers/common/training/Entities/`）含 `AudioUri`/`AudioKey`/`StartMs`/`EndMs`(long?) /`Text`/`Extensions`(BsonDocument?)；情绪落 `Extensions.emotion`（既有情绪识别任务产出）。
- **音频取数已就绪**：`IRecordingAudioClient.GetClipAsync(audioUri, startMs, endMs?, ct)` → `AudioClip(Bytes, ContentType)`，`endMs` 空自动按 `Ai:AudioClipDefaultDurationMs` 补齐、uri 解析走 `TrainingMapping.ParseAudioUri`——speaker/emo 参考取数**零新客户端**，直接复用。
- **情感向量生成链路已就绪**：`EmotionPromptBuilder.Build(text)`（`ChatPrompt.Audio` 恒 null）+ `IChatModelClientFactory.Create(provider.Type)` + `IChatModelClient.CompleteJsonAsync(provider, model, prompt)` + `ModelJsonParser.ParseEmotion`（`EmotionTaskRunner` 同链路）；`modelId` 语义与情绪任务一致（Models 管理 id）；模型调用经 `ModelSessionHeaderHandler` 固定携带 `x-opencode-session`，复用 named client `AiModel` 自动获得。
- **代理二进制透传已支持**：`TrainingProxyController.ForwardAsync` 原样透传 `Content-Type`/响应体（`CopyToAsync`）/`Cache-Control`/`Content-Disposition` 与状态码——`audio/wav` 直接复用，无需新分支（对照 `BilibiliLiveRecordingController` 试听片段镜像同口径）。
- **契约与 codegen 现状**：`contracts/training.openapi.yaml` 22 端点、`contracts/bilibili-crawler.openapi.yaml` `/api/training/**` 22 镜像；`apps/web/package.json` codegen 含 training（本期仅再生成 `schema-training.d.ts`，**无 web 改动**）。
- **编排现状**：`infra/apphost/AppHost.cs` training 现 `.WithReference(mongo/messaging/recording)`；Milvus 集成已确认存在（hosting 托管 `milvusdb/milvus` + `Aspire.Milvus.Client` 客户端集成 + 可选 Attu，见 aspire.dev Milvus 文档）；内网 Ollama（`https://ollama.internal.fffdan.com/`）与 index-tts-api（`https://tts.internal.fffdan.com`）为已上线服务，**仅配置接入、不在 AppHost 托管**。
- **测试范式**：控制器直测 + `MongoDbFixture`/`RedisFixture`（Testcontainers）+ `StubHandler`/`FakeFunAsrServer` 假 HTTP 服务（不打外网）；本期新增 Testcontainers Milvus fixture；`ProjectReferenceGuardTests` 引用矩阵不变（Milvus 走包引用）；`CollectionNamesEquivalenceTests` 不动（本期零新 Mongo 集合）。

**偏差与口径收敛（6 处，均在本 Plan 收敛）**：

1. **情感向量来源（用户 2026-09-30 确认）**：提案原写「本地 Ollama 模型 + prompt」，现改为**配置的可用模型**（Models 管理的 OpenAI-like / Claude-like）+ `EmotionPromptBuilder`，与既有情绪识别（`EmotionTaskRunner`）同链路；本地 Ollama **仅**承担文本 embedding。已同步修订提案（§1.1③/§1.2/§2/§3/§5/§6/§8/§9/§10/§11）与 PRD（§5.1/§5.2），本 Plan 按修订后口径执行。
2. **需求原文「按照文本id对数据集进行向量相似匹配」歧义**：按 PRD 已明确口径实施——**将输入文本向量化**（Ollama embedding）后与条目文本向量匹配 top-1（PRD 功能点 2），不再另行解释「文本 id」。
3. **确定性口径（PRD 1.3「同一数据集 + 同一文本 → 同一参考与同一匹配结果」）**：匹配层确定——Milvus FLAT 精确检索 + **稳定 tie-break**（检索 top-k=16 后按（相似度降序，itemId 升序）取 1，杜绝同分并列不稳）；生成层最大化确定——情感向量生成请求固定 `temperature=0`（`OpenAiLikeChatClient`/`ClaudeLikeChatClient` body 增加该字段，对既有情绪识别同样生效）；命中经 `X-Speaker-Audio-Key`/`X-Emo-Audio-Key` 可观测。剩余不确定性＝模型升级/模型自身非确定性，登记风险表（PRD 该措辞列为建议修订点）。
4. **参考音频取数失败口径（PRD 3.5 与提案 §5.1 注不一致）**：收敛为**提案口径**——逐候选取数（top-1 失败取次优…），候选**全部**取数失败 → 502 `audio unavailable`；409 `no speaker reference` 仅用于「数据集内无任何可用候选（无文本向量条目）」。PRD 3.5「若导致无可用音色参考则 409」登记为建议修订。
5. **`lang`/`modelId` 校验补充（提案留白）**：`lang` 白名单 `ZH/EN/JA/ES/AR`（大小写不敏感、统一转大写传上游），非法 400 `invalid lang`；`modelId` 不存在 404 `model not found`、停用 400 `model disabled`（与情绪任务口径一致）；模型调用失败仍**降级中性**（不阻断，best-effort）。
6. **测试设施口径**：提案 §8.2 写「WebApplicationFactory」，仓库实测为控制器直测 + Testcontainers fixture——沿用既有范式，断言面不变；Milvus 新增 `MilvusFixture`（Testcontainers `milvusdb/milvus`）。

> **返工修订（针对代码审核意见）**：① 修正 `Milvus__Token` = `milvus.Resource.Token`（`root:{apiKey}`，裸密码 401）；② 落地 `MilvusFixture` + 真 Milvus 集成用例（`MilvusVectorStoreTests`/`TrainingSynthesizeApiTests` 各覆盖真链路）并补 `MilvusRestClientTests` 假 HTTP 契约测试（撤销「Milvus 容器测试暂缓」）；③ `IVectorStore` 增 `DeleteItemVectorsAsync` + `BatchDelete` 级联钩子、`UpsertItemVectorsAsync` null 侧删行（派生索引一致性）；④ `Tts:DefaultLang` 启动白名单校验；⑤ 三个 HTTP 客户端改 `IHttpClientFactory` 每次取用（handler 轮换）；⑥ 检索失败懒触发 `EnsureCollectionsAsync` 兜底重试；⑦ 检索固定 `consistencyLevel: Strong`（读己之写）。第 2 轮收口：`MilvusFixture` 镜像固定 `milvusdb/milvus:v2.5.27`（对齐 Aspire.Hosting.Milvus 13.4.6，可复现）；补真 Milvus 裸密码→401 回归用例 `Real_BarePasswordToken_Rejected_WhileRootToken_Succeeds`（锁定阻断项教训）。

### 1.1 实施范围

- **做**：
  - `contracts/`：`training.openapi.yaml` + 2 端点（`synthesize`、`vectors/rebuild`）+ `TrainingSynthesizeRequest` schema；`bilibili-crawler.openapi.yaml` + 2 镜像（`audio/wav` 二进制透传）；`npm run codegen` 再生成 `schema-training.d.ts`（勿手改）。
  - `servers/training`：`Vectors/`（`IEmbeddingClient`/`LocalEmbeddingClient`、`IVectorStore`/`MilvusVectorStore`、`VectorSyncService`、`VectorExceptions`）、`Synthesis/`（`EmotionVector`、`SynthesizeService`）、`Clients/IndexTtsClient`、`Controllers/TrainingSynthesizeController`（2 端点）、`Configuration/`（`EmbeddingOptions`/`MilvusOptions`/`TtsOptions`/`SynthesizeOptions`）、`Program.cs` 注册与 Milvus 幂等建集合、既有写路径增量同步钩子（导入/编辑/情绪写入后同步 Milvus，失败 Warn 不阻断）、两适配器 `temperature: 0`（确定性增强）。
  - `servers/api`：`TrainingProxyController` + 2 action（既有 `ForwardAsync`）。
  - `infra/apphost`：Milvus 资源（Aspire Milvus 集成）+ training `.WithReference(milvus)` + `Embedding__BaseUrl`/`Tts__BaseUrl` 注入。
  - `tests/Z.Vtuber.Tests`：清单见 Phase 1/2（含 `MilvusFixture`）。
  - 文档：`servers/training/AGENTS.md`、`servers/api/AGENTS.md` 同步（3.3）。
- **不做**：Web 合成界面、批量合成；合成结果入库/缓存/合成记录；向量入 Mongo（Milvus 为派生索引）；ANN 近似索引（本期 FLAT）；语言自动检测（`lang` 缺省 ZH）；模型训练；上游 index-tts-api 改动；Milvus 多租户/备份；既有 22 端点语义零改动。

### 1.2 现状基线（范本）

- HTTP 客户端范本：`RecordingAudioClient`（named client + 超时 + 异常映射专用异常）；`AllowAutoRedirect=false` 口径见 `ChatModelClientFactory`。
- 模型调用范本：`EmotionTaskRunner`（provider/model 读取（含 `Enabled`）→ `EmotionPromptBuilder` → `CompleteJsonAsync` → `ParseEmotion`）。
- 代理范本：`TrainingProxyController` + `TrainingProxyControllerTests`（`StubHandler`）。
- 假服务范本：`FakeFunAsrServer`（本地假 HTTP server）；假 embedding / 假 TTS 照此实现。
- 幂等初始化范本：`TrainingMongoSetup.EnsureIndexesAsync`——Milvus `EnsureCollectionsAsync`（幂等建集合/FLAT 索引）同思路。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash、分支不复用）与 Conventional Commits：

- 本需求开发全程只使用需求总分支 `feat/training-audio-synthesize`：契约、实现、测试、文档全部直接在该分支提交（沿用前几期需求既定做法）；需求总 PR #149 已存在，提交照常推送更新分支。
- 提交信息：方案/Plan/修订 `docs(training):`、契约先行 `feat(contracts):`、测试先行 `test:`、实现 `feat:`；测试与实现分开提交；scope 使用 `training`/`api`/`contracts`/`infra` 既有口径。
- 编码实现按 z-coding 流程以 subagent 按子项目并行（§4 线划分即子项目划分）。

## 3. 实施阶段

按「契约先行（0）→ TDD 测试先行（红）→ 实现（绿）→ 收口验证」推进。Phase 0 串行先行；Phase 1/2 按三条线并行（线内串行）；Phase 3 串行收口。

### Phase 0: 契约先行（串行，全部实现的联调基线）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | `training.openapi.yaml` + 2 端点：① `POST /api/training/datasets/{datasetId}/synthesize`（body `TrainingSynthesizeRequest`：`text` 必填、`lang?`、`modelId?` → 200 `audio/wav` + 响应头 `X-Speaker-Audio-Key`/`X-Emo-Audio-Key`；400 `text is required`/`invalid lang`/`model disabled`、404 `dataset not found`/`model not found`、409 `no speaker reference`、502 `embedding service unavailable`/`vector store unavailable`/`audio unavailable`/`tts service unavailable`）；② `POST /api/training/datasets/{datasetId}/vectors/rebuild`（→ 200 `{"updated": n}`；404 dataset、502 embedding/vector-store）。错误体统一 `{"error":"..."}`；`*Id` 非法 ObjectId 按不存在 → 404；`lang` 枚举 `ZH/EN/JA/ES/AR`；`X-*` 头标注命中参考（口径见 §1.0 偏差 3/5） | `contracts/training.openapi.yaml` | 契约自洽，与提案 §3.1 + 本 Plan §1.0 偏差一致 |
| 0.2 | `bilibili-crawler.openapi.yaml` + 2 镜像：`synthesize`（`audio/wav` Content-Type 与二进制体透传、`X-*` 头透传、错误码透传）、`vectors/rebuild`；上游不可达 502 `{"error":"training service unavailable"}`（与既有 22 镜像同口径） | `contracts/bilibili-crawler.openapi.yaml` | 与 0.1 语义一致 |
| 0.3 | web codegen：`npm run codegen` 再生成 `src/api/schema-training.d.ts`（勿手改）；提交契约 + codegen 作为评审基线 | `apps/web/src/api/schema-training.d.ts` | 生成物与契约一致；`npm run lint` 通过 |

### Phase 1: 测试先行（红）

**线 A：向量 / 客户端 / 编排（training 内部 + Milvus fixture）**

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | `EmotionVectorTests`：`EmotionResult` → 八维构建；`FormatEmoVector`（八值逗号分隔、顺序 `happy,angry,sad,afraid,disgusted,melancholic,surprised,calm`）；`NormalizeEmoAlpha`（`clamp(emotionControlWeight/10, 0, 1)`、缺省 `Tts:DefaultEmoAlpha`） | `tests/Z.Vtuber.Tests/EmotionVectorTests.cs` | 红 |
| 1.2 | `LocalEmbeddingClientTests`（假 embedding 服务）：`POST {BaseUrl}/api/embeddings`（body `model`+`prompt`）→ `float[]`；OpenAI 兼容 `/v1/embeddings` 分支；非 2xx/超时/响应非法 → `EmbeddingUnavailableException`；日志不打文本内容 | `tests/Z.Vtuber.Tests/LocalEmbeddingClientTests.cs` | 红 |
| 1.3 | `MilvusVectorStoreTests`（Testcontainers Milvus，新 `MilvusFixture`）：`EnsureCollectionsAsync` 幂等（两集合 + FLAT 索引）；`UpsertItemVectorsAsync` 按 `itemId` upsert；`SearchTextAsync`/`SearchEmotionAsync` 按 `datasetId` 过滤 top-1（COSINE/L2）；**稳定 tie-break**（同分并列按 `itemId` 升序稳定命中）；`DeleteDatasetVectorsAsync` 级联 | `tests/Z.Vtuber.Tests/MilvusVectorStoreTests.cs`、`MilvusFixture.cs` | 红 |
| 1.4 | `VectorSyncServiceTests`（Mongo fixture + 假 embedding + Milvus）：`RebuildAsync` 返回 `{updated}`（文本向量由 `Text` 现算、情感向量取 `Extensions.emotion` 八维、无情绪条目只写文本向量、`Text` 空跳过计数口径）；增量 `SyncItemsAsync` 单条失败 Warn 不抛不阻断 | `tests/Z.Vtuber.Tests/VectorSyncServiceTests.cs` | 红 |
| 1.5 | `IndexTtsClientTests`（假 TTS）：multipart 字段断言（`text`/`lang`/`speaker_prompt` file（必带）/`emo_prompt` file/`emo_vector`/`emo_text`/`emo_alpha`/`duration_factor`/`text_normalization`/`max_text_tokens_per_segment`/`interval_silence`/`use_emo_text=false`/`use_random=false`）；无情绪命中时省略 `emo_*`；`FormatEmoVector`/`NormalizeEmoAlpha` 生效；超时/非 2xx → `TtsUnavailableException`；`AllowAutoRedirect=false` | `tests/Z.Vtuber.Tests/IndexTtsClientTests.cs` | 红 |
| 1.6 | `SynthesizeServiceTests`（假/mocked 依赖）：编排顺序与错误映射；speaker 取数 top-1 失败取次优、候选全失败 → `AudioUnavailableException`（502）、无候选 → 无音色参考语义（409）；情感生成失败/无情绪候选 → 省略 `emo_*` 降级中性仍返回；`SynthesizeResult` 携带 `SpeakerAudioKey`/`EmoAudioKey`；Info 日志格式 | `tests/Z.Vtuber.Tests/SynthesizeServiceTests.cs` | 红 |
| 1.7 | 确定性增强：`OpenAiLikeChatClient`/`ClaudeLikeChatClient` 请求 body 含 `temperature: 0`（既有 `TrainingRecognizeTests`/`EmotionTaskRunnerTests` 相关断言扩展） | `tests/Z.Vtuber.Tests/`（扩展） | 红 |

**线 B：synthesize / rebuild 端点 API**

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.8 | `TrainingSynthesizeApiTests`（假 embedding + 假模型 + 假 recording + 假 TTS + 真 Mongo + 真 Milvus）：200 `audio/wav`（字节=假 TTS 输出、Content-Type 正确）、`X-Speaker-Audio-Key`/`X-Emo-Audio-Key` 与命中一致；400（text 空/`invalid lang`）/404（dataset/model）/400 `model disabled`/409 `no speaker reference`/502 四类；降级中性（模型失败仍 200 且 TTS 收到无 `emo_*`）；`vectors/rebuild` 返回 `{updated}` 且重建后 synthesize 命中重建向量 | `tests/Z.Vtuber.Tests/TrainingSynthesizeApiTests.cs` | 红 |
| 1.9 | 增量同步钩子：导入（`items/import`）/更新 text（PUT）/情绪写入（`SetEmotionAsync` 后）Milvus 同步且失败 Warn 不阻断主流程（扩展既有 `TrainingImportApiTests`/`EmotionTaskApiTests`/`TrainingDatasetItemsApiTests` 断言） | `tests/Z.Vtuber.Tests/`（扩展） | 红 |

**线 C：api 代理镜像**

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.10 | `TrainingProxyControllerTests` 扩展（`StubHandler`）：2 镜像路由方法/路径/body 透传；`audio/wav` 二进制 + `Content-Type` + `X-*` 头 + 状态码（200/400/404/409/502）透传；上游不可达 502 `training service unavailable` | `tests/Z.Vtuber.Tests/TrainingProxyControllerTests.cs` | 红 |

### Phase 2: 实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | **向量层（`Vectors/`）**：`IEmbeddingClient`/`LocalEmbeddingClient`（named client `EmbeddingService`，`AllowAutoRedirect=false`，`POST {Embedding:BaseUrl}/api/embeddings`（Ollama）或 `/v1/embeddings`（兼容），body `{"model": Embedding:Model, "prompt"/"input": text}`；失败抛 `EmbeddingUnavailableException`）；`IVectorStore`/`MilvusVectorStore`（`Aspire.Milvus.Client`：`EnsureCollectionsAsync` 幂等建 `item_text_embeddings`（dim=`Embedding:Dimensions`，COSINE）/`item_emotion_vectors`（dim=8，L2），字段 `datasetId`/`itemId`/`audioKey`/`vector`，FLAT；检索 top-k=16 后内存按（相似度降序，itemId 升序）取 1；失败抛 `VectorStoreUnavailableException`）；`VectorSyncService`（`RebuildAsync`/`SyncItemsAsync`）；`VectorExceptions` | `servers/training/Vectors/` | 1.2–1.4 转绿 |
| 2.2 | **匹配与编排（`Synthesis/`）**：`EmotionVector`（`EmotionResult` → 八维 + `FormatEmoVector`/`NormalizeEmoAlpha`）；`SynthesizeService` 顺序＝dataset（404）→ text（400）→ `lang`/`modelId` 校验（偏差 5）→ embed query（502）→ `SearchTextAsync`（502/409）→ speaker 取数（次优兜底、全失败 502 `audio unavailable`）→ 情感生成（`EmotionPromptBuilder` + `IChatModelClientFactory` + `ParseEmotion`，失败降级中性）→ `SearchEmotionAsync` + emo 取数（best-effort）→ `ITtsClient`（502）→ `SynthesizeResult`；Info 日志 `training synthesize: datasetId={} speakerKey={} emoKey={} lang={} elapsedMs={}`（不打文本/音频） | `servers/training/Synthesis/` | 1.1、1.6 转绿 |
| 2.3 | **TTS 客户端**：`Clients/IndexTtsClient.cs`（`MultipartFormDataContent` 按 1.5 字段；`speaker_prompt` 必带 file（源自 `AudioClip.ContentType`）；超时 `Tts:RequestTimeoutSeconds`（120s）；非 2xx/超时 → `TtsUnavailableException`） | `servers/training/Clients/IndexTtsClient.cs` | 1.5 转绿 |
| 2.4 | **控制器**：`TrainingSynthesizeController`（`[Route("api/training/datasets/{datasetId}")]`）——`POST synthesize`（`File(result.Audio, "audio/wav")` + `X-*` 头）、`POST vectors/rebuild`（`{updated}`）；非法 ObjectId → 404；错误映射按 0.1 | `servers/training/Controllers/TrainingSynthesizeController.cs` | 1.8 转绿 |
| 2.5 | **配置**：`EmbeddingOptions`（`BaseUrl` 默认 `https://ollama.internal.fffdan.com/`、`Model`、`Dimensions?`、`RequestTimeoutSeconds`）/`MilvusOptions`（Aspire 注入）/`TtsOptions`（`BaseUrl` 默认 `https://tts.internal.fffdan.com`、`RequestTimeoutSeconds=120`、`DefaultLang=ZH`、`DefaultEmoAlpha=1.0`、`DurationFactor` 等）/`SynthesizeOptions`（`DefaultEmotionModelId?` = Models 管理可用模型、`MaxReferenceDurationMs?`）；均 `ValidateOnStart` | `servers/training/Configuration/` | 启动校验通过 |
| 2.6 | **注册与启动**：`Program.cs` 注册 `IEmbeddingClient`/`IVectorStore`/`VectorSyncService`/`ITtsClient`/`SynthesizeService` + options + named clients；启动 `EnsureCollectionsAsync`（幂等）；`Milvus` 连接信息取 Aspire 注入（`ConnectionStrings:milvus`/`Milvus:Endpoint`） | `servers/training/Program.cs` | Aspire 可拉起 |
| 2.7 | **确定性增强（偏差 3）**：`OpenAiLikeChatClient`/`ClaudeLikeChatClient` body 增 `temperature: 0`（对既有识别/情绪任务同样生效） | `servers/training/Ai/` | 1.7 转绿 |
| 2.8 | **增量同步钩子（偏差/流程 2）**：导入、PUT 改 text、情绪写入后 best-effort `SyncItemsAsync`（失败 Warn，`vectors/rebuild` 兜底） | `servers/training/Controllers/`、`Emotion/EmotionTaskRunner.cs` 之后调用点 | 1.9 转绿 |
| 2.9 | **api 代理镜像**：`TrainingProxyController` + 2 action（既有 `ForwardAsync`，二进制/`X-*` 头已天然透传） | `servers/api/Controllers/TrainingProxyController.cs` | 1.10 转绿 |
| 2.10 | **编排**：`infra/apphost/AppHost.cs` 新增 Milvus 资源（Aspire Milvus 集成，可选 Attu）+ training `.WithReference(milvus)` + `Embedding__BaseUrl`/`Tts__BaseUrl`（Ollama/大模型/TTS 为内网已上线服务，仅配置接入） | `infra/apphost/AppHost.cs` | Aspire 拉起、Milvus 就绪 |
| 2.11 | `npm run codegen` 一致性复核 + `dotnet build`/`dotnet test`/`npm run lint` 全通过 | 全部 | 构建零告警 |

### Phase 3: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 3.1 | 全量测试与覆盖率：`dotnet test tests/Z.Vtuber.Tests` 全绿（含既有回归）；核心逻辑（`EmotionVector` 格式化/归一、Milvus top-1 与 tie-break、向量同步/重建、multipart 组装、错误映射）覆盖率 ≥ 80%（common-rules） | 全部通过 |
| 3.2 | 兼容性回归：`git diff` 确认 recording、recording-transcript-api/consumer、ASR 链路零改动；既有 training 22 端点与 api 代理语义零改动（仅 +2）；数据集/导入/编辑/情绪识别/波形不回退 | diff/grep 无违规 |
| 3.3 | 文档同步：`servers/training/AGENTS.md`（+2 端点、`Vectors/`/`Synthesis/`/`Clients/IndexTtsClient` 模块、4 配置节、Milvus 依赖、`vectors/rebuild` 回填前置与部署要点）、`servers/api/AGENTS.md`（镜像 22→24、`audio/wav` 二进制透传口径）；本 Plan 与提案/PRD 链接互验 | 文档与实现一致 |
| 3.4 | 手工验证（z-aspire-orchestrator，推荐）：Aspire 拉起（Milvus 就绪）→ 存量数据集 `POST vectors/rebuild`（`updated`）→ `POST synthesize`（文本 → WAV，`X-*` 头命中）→ 模型失败降级中性 → 异常路径（空 text 400 / 数据集 404 / 未回填 409 / 上游挂 502）→ 既有功能回归 | 手工验证记录 |

## 4. 并行策略

- **Phase 0 串行先行**（0.1→0.2→0.3）：契约是全部线的联调与 codegen 基线。
- **Phase 1/2 三条线并行，线内串行**（亦为 subagent 子项目划分）：
  - **线 A 向量/客户端/编排**：1.1→1.2→1.3→1.4→1.5→1.6→1.7→2.1→2.2→2.3→2.5→2.6→2.7→2.8→2.10（`Vectors/`、`Synthesis/`、`Clients/IndexTtsClient`、`Configuration/`、`Program.cs`、AppHost 全归线 A）；
  - **线 B 端点 API**：1.8→1.9→2.4（依赖线 A 组件与 0.1 契约；`Controllers/TrainingSynthesizeController` 归线 B）；
  - **线 C 代理**：1.10→2.9（与 A/B 零文件交集，可最先开工）。
- **文件冲突规避**：`servers/training/Ai/`（2.7）归线 A；`Emotion/EmotionTaskRunner.cs` 增量钩子（2.8）归线 A；`servers/api` 归线 C。
- **依赖关系**：2.4 依赖 0.1 与 2.1/2.2/2.3；2.9 依赖 0.2；2.11 与 3.x 全线完成后串行收口。
- 全部提交在需求总分支 `feat/training-audio-synthesize`。

## 5. 验收对照

| PRD 功能点 / 成功标准 | 实现任务 | 验证方式 |
|-----------|---------|---------|
| 功能点 1：`POST .../synthesize` 入参出参（text 必填、lang 缺省 ZH、modelId 缺省默认情绪模型、`audio/wav`、z-vtuber 代理同口径） | 0.1、0.2、2.4、2.5、2.9 | 1.8、1.10 |
| 功能点 2：音色参考自动匹配（文本向量化 → 数据集内向量相似 top-1 → `speaker_prompt`；稳定命中；无参考明确错误） | 2.1、2.2 | 1.3、1.6、1.8 |
| 功能点 3：情绪参考自动匹配（可用模型生成情感向量 → 最相近 1 条 → `emo_prompt`/`emo_vector`/`emo_text`/`emo_alpha`；无标注降级中性仍返回） | 2.2、2.7 | 1.1、1.6、1.8 |
| 功能点 4：上游合成与音频返回（multipart 符合契约、`speaker_prompt` 必带、`audio/wav` 透传、上游不可用 502） | 2.3、2.4、2.9 | 1.5、1.8、1.10 |
| 成功标准：可预期、可复现（同一数据集 + 同一文本 → 同一参考与同一匹配结果） | 2.1（FLAT + tie-break）、2.7（`temperature=0`） | 1.3、1.7（剩余不确定性见 §6 风险） |
| 成功标准：关键异常明确错误码、不返回损坏音频 | 2.2、2.4 | 1.8 错误码矩阵 |
| 约束：文本向量本地（Ollama）、情感向量用配置的可用模型 | 2.1、2.2 | 1.2、1.6、3.4 |
| 约束：Milvus 承载相似检索、向量可重建 | 2.1、2.6、`vectors/rebuild` 2.4 | 1.3、1.4、1.8 |
| 前置运维：存量条目 `vectors/rebuild` 回填后参与匹配 | 2.4、3.3（部署要点）、3.4 | 1.8（重建后命中）、3.4 |
| 非功能：超时（TTS 120s）、日志不打文本/音频、沿用内网访问口径 | 2.2、2.3、2.5 | 1.5、1.6、3.2 |
| 非功能：兼容（既有 22 端点/数据集/导入/情绪识别/波形零回退） | 1.1 范围约束 | 3.2 diff + 回归 |
| 契约与质量门槛（契约先行 + codegen 一致 + 覆盖率 ≥ 80% + TDD） | 0.x、2.11、3.1 | codegen diff、覆盖率报告、红-绿提交记录 |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| 情感向量生成非确定性 vs PRD 可复现口径 | 可用模型输出随模型升级/采样波动，「同一文本 → 同一匹配结果」无法绝对保证 | 匹配层 FLAT + 稳定 tie-break 确定；生成层 `temperature=0` 最大化稳定；`X-*` 头可观测命中；PRD 该措辞登记为建议修订点（改为「同模型版本下可复现」） |
| 存量数据集未回填向量 | 未 `vectors/rebuild` 的数据集 synthesize 恒 409 | 部署前置动作写入 AGENTS.md 部署要点与提案 §9.2；3.4 手工验证覆盖；增量钩子让新数据自动入索引 |
| Milvus Aspire 集成包可用性 | `Aspire.Milvus.Client` 若不可用/不满足 | 退化为 `Milvus.Client` + `AddContainer("milvus", "milvusdb/milvus")` + `Milvus:Endpoint` 注入（提案 §9.2 口径不变，`MilvusVectorStore` 接口不动） |
| embedding 模型/维度变更 | 旧向量与新查询不在同一空间，命中失真 | 向量为派生索引，换模型后 `vectors/rebuild` 全量重建；`Embedding:Dimensions` 校验与集合 dim 一致 |
| 多说话人数据集音色漂移 | 文本相似 top-1 与音色无关，混合语料会克隆出漂移音色 | 产品假设：数据集为单说话人语料（训练音色用）；维持 PRD 功能点 2 选取口径，登记产品侧注意事项 |
| 参考音频失效 | 条目 `AudioUri` 失效导致取数失败 | 逐候选跳过取次优（偏差 4）；候选全失败 502 `audio unavailable`；不返回损坏音频 |
| 上游契约字段漂移（index-tts-api） | multipart 字段变更导致合成失败 | `IndexTtsClientTests` 假 TTS 锁定字段；失败映射 502 不吞错 |
| 合成延迟 | IndexTTS-2.5 GPU 推理慢 | `Tts:RequestTimeoutSeconds=120`；接口同步等待（本期不做异步任务），并发量低（自用场景） |
| 温度固定影响既有情绪任务 | `temperature: 0` 对识别/情绪任务同样生效 | 属确定性增强（输出更稳）；既有测试断言同步更新（1.7），如有异议可仅对 synthesize 链路传参 |
| 依赖 | 内网 index-tts-api（`Tts:BaseUrl`）、内网 Ollama（`Embedding:BaseUrl`，文本向量）、配置的可用模型（情感向量）、recording 片段端点、MongoDB（元数据）、Milvus（新增 NuGet `Aspire.Milvus.Client`/`Milvus.Client`——「零新增重量级 NuGet」的显式例外，提案决策已列） | 均已核实（§1.0）；.NET 10、TDD、覆盖率 ≥ 80% 为项目既有规范 |

## 7. 相关文档

- PRD：`docs/product/draft/20260929-training-audio-synthesize-prd.md`（v1.0）
- 技术提案：`docs/engineering/proposals/20260929-proposal-training-audio-synthesize.md`（2026-09-30 修订版）
- 前序 Plan：[[20260928-plan-training-dataset-emotion]]、[[20260927-plan-training-audio-dataset]]
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`
- 模块规范：`servers/training/AGENTS.md`、`servers/api/AGENTS.md`、`servers/common/AGENTS.md`
- 既有契约：`contracts/training.openapi.yaml`、`contracts/bilibili-crawler.openapi.yaml`
- 参考：本地 Ollama `https://ollama.internal.fffdan.com/`、Aspire Milvus 集成（https://aspire.dev/integrations/databases/milvus/milvus-get-started/）、上游 [`Deliay/index-tts-api`](https://github.com/Deliay/index-tts-api)
