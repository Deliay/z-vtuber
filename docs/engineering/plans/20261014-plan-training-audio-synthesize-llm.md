---
description: AI 训练「音频生成 - 语音大模型推理改造」实现 Plan：synthesize 上游整体替换为「语音+文本输入、语音输出」大模型（ISpeechModelClient/SpeechModelClientFactory/OpenAiLikeSpeechClient 走 chat/completions + modalities=[text,audio] + input_audio 参考语音 → message.audio.data base64 WAV），模型解析走新功能点 speech-synthesize（输入 audio,text / 输出 audio，无可用 409 no speech model available），参考语音挑选链路零改动并随音频传条目 Text 转写，emotionMode 保留（EmotionMatcher 内存 L2 top-1 + SpeechPromptBuilder 情绪指令注入，无命中降级中性）；同时彻底移除 Milvus 与原 TTS：Vectors/ 全目录、vectors/rebuild 端点（含 api 镜像与 web codegen 类型）、导入/编辑/情绪写入向量同步钩子、Ollama embedding 依赖、MilvusOptions/EmbeddingOptions/TtsOptions/IndexTtsClient、Aspire Milvus 资源与 Milvus__*/Embedding__BaseUrl/Tts__BaseUrl 注入全部删除，SupportedLangs/DefaultLang 迁入 SpeechOptions；契约先行 + TDD（假 HttpMessageHandler，不再使用 Testcontainers Milvus），覆盖率 ≥ 80%
type: Permanent
---

# AI 训练 - 音频生成（语音大模型推理改造） - 实现 Plan

**功能名称**: 训练 - 音频生成（语音大模型推理改造：语音+文本输入 → 语音输出）
**关联 PRD**: `docs/product/draft/20261014-training-audio-synthesize-llm-prd.md`（v1.1，返工修订：彻底移除 Milvus）
**关联技术提案**: `docs/engineering/proposals/20261014-proposal-training-audio-synthesize-llm.md`（v1.1 返工修订）
**Plan 版本**: v1.0
**创建日期**: 2026-10-14
**作者**: Workflow Agent（代起草）
**需求总分支**: `prd/training-audio-synthesize-llm`（需求总 PR #186，含返工修订 `fc117f7`）
**feat-branch**: `feat/training-audio-synthesize-llm`（实现分支，基于需求总分支切出）

## 1. 概述

依据 PRD 与技术提案落地六件事：① `POST /api/training/datasets/{datasetId}/synthesize` 的推理上游由内网 index-tts-api（`ITtsClient`/`IndexTtsClient`）整体替换为「语音+文本输入、语音输出」大模型（`ISpeechModelClient` 族：`chat/completions` + `modalities:["text","audio"]` + `input_audio` 参考语音 → `message.audio.data` base64 WAV），对外端点/参数/响应形态不变；② 语音模型解析走**新功能点 `speech-synthesize`**（输入 `audio,text` / 输出 `audio`，指定优先 → 默认，无可用 409 `no speech model available`，provider 无音频输出能力 400 `model does not support audio output`）；③ 参考语音挑选链路**零改动**，产出物增补**参考转写**（命中条目 `Text` 按序拼接）随参考音频经 `input_audio`+`text` 提交；④ `emotionMode` 三模式契约保留，情绪参考由 emo_* multipart 字段改为**情绪指令文本**注入（`SpeechPromptBuilder`），`llm` 模式情绪检索由 Milvus 改为 **Mongo 情绪标注 + 内存 L2 top-1（`EmotionMatcher` 纯函数）**，无命中/失败降级中性；⑤ **彻底移除 Milvus**（`Vectors/` 全目录、`vectors/rebuild` 端点含 api 镜像与 web codegen 类型、导入/编辑/情绪写入向量同步钩子、Ollama embedding 依赖、Aspire Milvus 资源与配置注入）与**原 TTS 下线**（`ITtsClient`/`IndexTtsClient`/`TtsOptions`/`Tts__BaseUrl`，`SupportedLangs`/`DefaultLang` 迁入新 `SpeechOptions`）；⑥ 契约先行（502 文案 `tts service unavailable` → `model service unavailable`、新增 `invalid model output`、废止 embedding/vector-store 错误码）、TDD 补齐测试（假 `HttpMessageHandler` 契约测试，不打外网；不再使用 Testcontainers Milvus）。实施遵循 `docs/engineering/common-rules.md`（契约先行、TDD、覆盖率 ≥ 80%、单用例 ≤ 5s）、`backend-rules.md`、z-coding 流程与相关模块 `AGENTS.md`（`servers/training`、`servers/api`、`apps/web`）。

### 1.0 现状核实结论（读码确认，均在需求总分支 @ `fc117f7` 复核）

- **TTS 耦合面 = 2 文件 + 1 配置 + 注入**：`Clients/ITtsClient.cs`（`TtsSpeakerReference(Bytes, ContentType)`/`TtsEmotionReference(Bytes, ContentType, EmotionResult)`/`TtsSynthesizeRequest(Text, Lang, Speaker, Emotion?)`/`TtsUnavailableException`）、`Clients/IndexTtsClient.cs`（typed client `TtsService`，手写 multipart `POST {Tts:BaseUrl}/synthesize`：`text`/`lang`/`speaker_prompt`(file)/`emo_prompt`(file)/`emo_vector`/`emo_text`/`emo_alpha`/`duration_factor`/…；超时 `Tts:RequestTimeoutSeconds` 120s）、`Configuration/TtsOptions.cs`（节 `Tts`；`static SupportedLangs = ["ZH","EN","JA","ES","AR"]`、`DefaultLang=ZH` 被控制器/启动校验引用）；`Program.cs`（`TtsOptions` 绑定 + `Tts:DefaultLang` 白名单校验 + `IndexTtsClient.ClientName` HttpClient + `AddSingleton<ITtsClient, IndexTtsClient>`）；AppHost `Tts__BaseUrl` 注入（`infra/apphost/AppHost.cs:102`）。
- **Milvus/Embedding 移除面全量清单**：`Vectors/` 7 文件（`IEmbeddingClient`/`LocalEmbeddingClient`/`IVectorStore`/`MilvusVectorStore`/`MilvusRestClient`/`VectorSyncService`/`VectorExceptions`）；`Configuration/MilvusOptions.cs`/`EmbeddingOptions.cs`；`Program.cs`（`EmbeddingService`/`MilvusStore` named clients、`IEmbeddingClient`/`IMilvusRestClient`/`IVectorStore`/`VectorSyncService` 注册、启动 `EnsureCollectionsAsync()` best-effort）；向量同步钩子 **5 处**（`TrainingDatasetItemsController`：导入 `SyncItemAsync`、编辑 text `SyncItemAsync`、批量删除 `DeleteItemsAsync`；`TrainingDatasetsController`：删除数据集 `DeleteDatasetAsync`；`EmotionTaskRunner:134-136`：情绪写入后 `SyncItemAsync`）；`TrainingSynthesizeController.RebuildVectors`（`POST vectors/rebuild`）+ `Contracts/Dtos.cs` `TrainingVectorsRebuildResultDto`；AppHost `milvus` 资源（`AddMilvus("milvus", milvusApiKey)` + `WaitFor(milvus)`）与 `Milvus__Endpoint`/`Milvus__Token`/`Embedding__BaseUrl` 注入（`AppHost.cs:95-101`）。
- **synthesize 控制器现状**（`Controllers/TrainingSynthesizeController.cs`，216 行）：校验顺序 dataset 404 → text 400 `text is required` → lang 400 `invalid lang`（缺省 `ttsOptions.DefaultLang`，白名单 `TtsOptions.SupportedLangs`）→ emotionMode 400 `invalid emotion mode` →（`llm`）`ResolveEmotionModelAsync`（显式 `modelId` 非法/不存在 404 `model not found`、停用 400 `model disabled`；缺省走功能点 `emotion-from-text` 解析，无可用 → null 降级中性）→ `SynthesizeService.SynthesizeAsync(datasetId, text, lang, model, mode, ct)` → 200 `File(result.Audio, "audio/wav")` + `X-Speaker-Audio-Key`（逗号分隔多值）/`X-Emo-Audio-Key`；catch 映射 `NoSpeakerReferenceException` 409、`EmbeddingUnavailableException`/`VectorStoreUnavailableException`/`AudioUnavailableException`/`TtsUnavailableException` 各 502。
- **编排现状**（`Synthesis/SynthesizeService.cs`，515 行）：`SynthesizeAsync` → `ResolveSpeakerAsync`（拼音断句多命中 → 按 ItemId 去重 → 条数/总时长截断 → `MergeClipsAsync` 合并；无候选回退 `TextSimilarity` 字符相似单条；内部类型 `SpeakerOutcome(TtsSpeakerReference Reference, …, EmotionMatch? DatasetEmotion)`、`ReferenceMatch(Item, Clip)`、`EmotionMatch(TtsEmotionReference Reference, string AudioKey)`）→ 情绪三模式 → `tts.SynthesizeAsync(new TtsSynthesizeRequest(text, lang, speaker.Reference, emotion?.Reference))` → `SynthesizeResult(byte[] Audio, IReadOnlyList<string> SpeakerAudioKeys, string? EmoAudioKey)`（对外形态不变）。`llm` 情绪路径 `TryBuildEmotionReferenceAsync`：`EmotionPromptBuilder.Build` + `IChatModelClient.CompleteJsonAsync` → `ModelJsonParser.ParseEmotion` → `EmotionVector.FromEmotion` → **`IVectorStore.SearchEmotionAsync`（Milvus top-k=16）** → 逐候选回读 Mongo → `ScanCandidatesAsync`（逐候选取音频兜底）→ `TtsEmotionReference`。`dataset` 路径本就直读 Mongo（`Extensions.emotion`）。
- **EmotionVector 现状**（`Synthesis/EmotionVector.cs`）：`Dimensions`（八维顺序 happy,angry,sad,afraid,disgusted,melancholic,surprised,calm）/`FromEmotion`/`Format`（emo_vector 串）/`NormalizeEmoAlpha`（emo_alpha）——后两者**仅服务 emo_\***，随 TTS 移除；前两者为内存情绪匹配的查询/候选向量化所依赖，**保留**。
- **能力/功能点基建可直接复用**：`TrainingModelCapabilities` 四值 `audio`/`video`/`text`/`image`（输入/输出同词表，「输入 `audio,text` / 输出 `audio`」零 schema 变更可表达）；`FeaturePointIds` 现 4 个定义（`dataset-clean`/`emotion-from-audio`/`emotion-from-text`/`image-generate`），+1 `speech-synthesize` 即可；`ITrainingModelResolver.ResolveAsync(slug, name, input, output)`（登记 upsert → 指定优先 → 默认首候选 → none）；Ai 客户端派发先例 `IImageModelClient.GenerateImageAsync(TrainingProviderRecord, TrainingModelRecord, prompt, IReadOnlyList<GenerateImageInput>, ct)` + `ImageModelClientFactory`（openai → OpenAI-like；claude 及未知 → 抛不支持）+ named client `AiModel`（`ModelSessionHeaderHandler` 挂 `x-opencode-session`、`AllowAutoRedirect=false`、超时 `Ai:RequestTimeoutSeconds` 120s）。
- **契约现状**：`contracts/training.openapi.yaml` `synthesize`（operationId `synthesizeTrainingDataset`；200 `audio/wav` 描述「mono 16-bit PCM WAV @22.05kHz，上游透传」+ 两个 X-* 头；400/404/409/502 文案含 `tts service unavailable`、`embedding service unavailable`/`vector store unavailable`）+ `vectors/rebuild`（operationId `rebuildTrainingDatasetVectors` + `TrainingVectorsRebuildResult` schema）；`contracts/bilibili-crawler.openapi.yaml` 同路径镜像（`vectors/rebuild` @1733、schema @3408）；`apps/web` **无手写调用方**（`vectors/rebuild` 仅存在于 `npm run codegen` 产物 `schema.d.ts`/`schema-training.d.ts`）。
- **代理现状**（`servers/api/Controllers/TrainingProxyController.cs`）：`vectors/rebuild` 镜像 action @140-142（本期删除）；`synthesize` 走通用 `ForwardAsync`（POST `StreamContent(Request.Body)` 原样透传 + 响应状态码/`Content-Type`/二进制流/`X-*` 头透传）——**零代码改动**；`TrainingService:RequestTimeoutSeconds` 180s > 各上游 120s 由 `TrainingProxyTimeoutConsistencyTests` 守护（现含 Tts/Embedding/Milvus/Ai 四行，本期收敛为 Ai）。
- **测试现状**：`IndexTtsClientTests`（假 `HttpMessageHandler`，本期改写为 `OpenAiLikeSpeechClientTests`）；Milvus 系 `MilvusFixture`（`milvusdb/milvus` 容器）+ `MilvusRestClientTests`/`MilvusVectorStoreTests`/`VectorSyncServiceTests`/`LocalEmbeddingClientTests`/`TrainingDatasetVectorCascadeTests`（**整体删除**）；`TrainingSynthesizeApiTests`（`IClassFixture<MongoDbFixture> + IClassFixture<MilvusFixture>`，`TrainingSynthesizeFakes` 含 `StubMilvusRestClient`/`FakeEmbeddingClient`/`InMemoryVectorStore`/`StubAudioClient`/`StubTtsClient`——本期去 Milvus fixture、`StubTtsClient` → `StubSpeechClient`）；`TrainingSynthesizeProxyTests`（错误文案断言 + `vectors/rebuild` 代理用例）；`EmotionVectorTests`（删 `Format`/`NormalizeEmoAlpha` 用例）。
- **残留配置先例**：`Program.cs:162-169` 对已移除的 `Synthesize:DefaultEmotionModelId` 做「非空 → 启动 Warn 一次并忽略」，本期对残留 `Tts`/`Milvus`/`Embedding` 节照此口径。

### 1.1 口径澄清（规划期裁定，随实现评审）

1. **情绪参考只传「倾向」不传音频**：`SpeechSynthesizeRequest` 不含情绪音频字段（emo_prompt 随 TTS 废止）；`EmotionResult` → 情绪指令文本（**描述优先**；描述为空按八维最强维度映射情绪名，同分按 `EmotionVector.Dimensions` 顺序取先）。`X-Emo-Audio-Key` 命中头保留，但命中口径改为「带情绪标注的命中条目」——**不再要求该条目音频可取**（现状 emo_prompt 需取音频，改造后无此需求）。
2. **情绪命中确定性**：`llm` 模式 `EmotionMatcher.SelectTop1` 直取 top-1（不再逐候选取音频兜底），同分 itemId 升序；`dataset` 模式沿音色命中顺序取首个带 `Extensions.emotion` 者（口径零改动）；`none` 不参与。
3. **参考转写拼接口径**：参与合并的条目 `Text` 按合并顺序以「，」拼接（空文本条目跳过；全空 → `ReferenceTranscript = null` 不传）；单条回退路径取该条目 `Text`（空则 null）。
4. **控制器校验/解析顺序（错误优先级）**：dataset 404 → text 400 → lang 400（缺省与白名单改读 `Speech` 节）→ emotionMode 400 →（`llm`）情绪模型 404/400（既有口径零回退）→ **语音模型解析**（无可用 409 `no speech model available`；provider 无音频输出能力 400 `model does not support audio output`）→ 参考挑选 409 `no speaker reference` → 参考取数 502 `audio unavailable` → 模型调用 502。即「无语音模型」优先于「无参考语音」返回（与提案时序一致）。
5. **超长文本不做本地长度闸门与截断**（PRD §3.5「不做额外截断策略（本期）」）：超长输入由模型侧拒绝，映射 502 `model service unavailable`；契约描述显式说明，后续有需要再单独立项。
6. **输出格式**：请求固定 `audio: {"format": "wav"}`，响应 `audio.data` base64 解码后 `audio/wav` 透传；200 契约描述由「mono 16-bit PCM WAV @22.05kHz」修订为「WAV（模型输出透传，采样率/编码以模型输出为准）」。
7. **`input_audio.format` 映射**：`ReferenceContentType` 分号参数归一后 `audio/mpeg`→`mp3`、`audio/wav`/`audio/x-wav`/`audio/wave`→`wav`，未知按 `mp3`（参考音频来自 recording 合并结果，多为 `audio/mpeg`）。
8. **异常语义**：`SpeechModelUnavailableException`（不可达/超时/非 2xx/空响应）→ 502 `model service unavailable`；响应缺 `audio.data`/空/非法 base64 → `ModelOutputException` → 502 `invalid model output`（与 image 先例同口径）；`ModelAudioOutputNotSupportedException` → 400 `model does not support audio output`；客户端取消（`OperationCanceledException` when `ct.IsCancellationRequested`）**原样上抛**不降级（既有口径）。
9. **残留配置处理**：`Tts`/`Milvus`/`Embedding` 节非空 → 启动 Warn 一次并忽略（先例 `Synthesize:DefaultEmotionModelId`）；新 `Speech` 节 `ValidateOnStart`，`DefaultLang` 白名单校验迁移（文案 `Speech:DefaultLang must be one of ZH/EN/JA/ES/AR`），`SupportedLangs`/`DefaultLang` 自 `TtsOptions` 静态迁移。
10. **EmotionMatcher 纯函数口径**：L2 距离 top-1（比较用平方欧氏距离，排序与 L2 等价），同分 itemId（`ObjectId`）升序 tie-break；候选空/查询向量长度不符 → null（调用方降级中性）；候选 = 该数据集 `Extensions.emotion` 非空条目（`EmotionVector.FromEmotion` 向量化），数据集千级 × 八维内存精确计算。

### 1.2 实施范围

- **做**：`contracts/`（`training.openapi.yaml` synthesize 语义/错误文案/200 描述修订 + 移除 `vectors/rebuild` 与 `TrainingVectorsRebuildResult`；`bilibili-crawler.openapi.yaml` 镜像同步；`apps/web` codegen 再生成）；`servers/training`（`Ai/` 新增 `ISpeechModelClient`/`ISpeechModelClientFactory`/`SpeechModelClientFactory`/`OpenAiLikeSpeechClient` + 异常；`Synthesis/` 新增 `EmotionMatcher`/`SpeechPromptBuilder`、`SynthesizeService` 上游替换 + 转写 + 内存情绪匹配、`EmotionVector` 瘦身；`FeaturePoints/FeaturePointIds` +`speech-synthesize`；`Configuration/SpeechOptions` 新增、`TtsOptions`/`MilvusOptions`/`EmbeddingOptions` 删除；`Clients/ITtsClient.cs`/`IndexTtsClient.cs` 删除、**`Vectors/` 整目录删除**；`Controllers/TrainingSynthesizeController.cs` 语音模型解析 + 新错误映射 + 移除 `vectors/rebuild`；`Controllers/TrainingDatasetItemsController.cs`/`TrainingDatasetsController.cs`/`Emotion/EmotionTaskRunner.cs` 移除向量同步钩子；`Contracts/Dtos.cs` 移除 rebuild DTO；`Program.cs` 注册调整）；`infra/apphost/AppHost.cs`（移除 Milvus 资源与三组注入）；`servers/api`（`TrainingProxyController` 移除 `vectors/rebuild` 镜像 action，其余零改动）；`tests/Z.Vtuber.Tests`（新增语音客户端/纯函数/编排/接口用例，删除 Milvus/Embedding/TTS 相关测试与 fake）；文档同步（`servers/training/AGENTS.md`、`servers/api/AGENTS.md`、PRD 验收勾选、文档互链）。
- **不做**：参考语音挑选口径（拼音/字符匹配、合并拼接、截断上限）、`segments/rebuild` 与 Mongo 段存储、情绪标注结构与识别任务、数据集/导入/编辑/波形试听/绘图生成语义、`emotionMode`/`lang`/`modelId` 契约语义、模型管理与功能点机制本身、流式输出、合成结果入库/缓存、批量合成、Web 合成界面、文本长度闸门/截断、参考转写 ASR 兜底、Claude-like 音频输出、多音色混合/语速语调参数化、向量检索替代选型、生产 Milvus/Ollama 实例下线（运维侧另行安排）、除 `Speech` 节外的新增配置/依赖/流水线。

## 2. 分支与提交流程

- 需求总分支 `prd/training-audio-synthesize-llm`（基于 origin/main 切出，PRD 与技术提案已随 `3446483`/`fc117f7` 提交，需求总 PR #186）：**本 Plan 提交到该分支随 PR 评审**。
- 实现分支 `feat/training-audio-synthesize-llm`（基于需求总分支切出，提案 §1.3 口径）：实现按 z-coding「编写实现」流程推进（子项目为单位委托 subagent、TDD 红-绿、测试与实现分开提交），实现 PR 回需求总分支评审。
- 提交顺序（Conventional Commits）：
  1. `docs(training):` 本实现 Plan（需求总分支）；
  2. `feat(contracts):` 契约修订（synthesize 语义/错误文案 + 移除 `vectors/rebuild`）+ api 镜像同步 + `npm run codegen`；
  3. `feat(training):` 语音客户端族 + 纯函数（`EmotionMatcher`/`SpeechPromptBuilder`/`EmotionVector` 瘦身）（含 `test(training):` 配套）；
  4. `feat(training):` 编排替换 + 功能点 + `SpeechOptions`（`SynthesizeService`/`TrainingSynthesizeController`/`FeaturePointIds`）（含 `test(training):` 配套）；
  5. `refactor(training):` TTS 与 Milvus 移除（`Vectors/`、`Clients/ITtsClient.cs`、`TtsOptions`/`MilvusOptions`/`EmbeddingOptions`、同步钩子、`Program.cs`、AppHost）+ `test(training):` 删除 Milvus 系测试、合成测试去 fixture 改造；
  6. `test(api):` 代理测试同步（`vectors/rebuild` 用例移除、超时口径收敛）；
  7. `docs(training):` `AGENTS.md`（training/api）/ PRD 验收勾选 / 文档互链同步。

## 3. 实施阶段

### Phase 0: 技术文档（先行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | PRD（上游节点产出） | `docs/product/draft/20261014-training-audio-synthesize-llm-prd.md` | 已完成（`3446483`/`fc117f7`） |
| 0.2 | 技术提案（上游节点产出） | `docs/engineering/proposals/20261014-proposal-training-audio-synthesize-llm.md` | 已完成（`3446483`/`fc117f7`） |
| 0.3 | 实现 Plan（本文档） | `docs/engineering/plans/20261014-plan-training-audio-synthesize-llm.md` | 任务/文件/验收明确，与提案口径一致 |

### Phase 1: 契约先行

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | `synthesize` 语义修订：description 改「语音大模型推理（`input_audio` 参考语音 + 文本 → WAV 输出）」；`TrainingSynthesizeRequest.lang`/`modelId` 描述同步（lang 作为语种提示注入、`modelId` 仍仅情绪推断）；200 描述改「WAV（模型输出透传）」；400 描述 +`model does not support audio output`、409 描述 +`no speech model available`、502 描述 `tts service unavailable` → `model service unavailable` + `invalid model output`、废止 `embedding service unavailable`/`vector store unavailable`；**移除 `vectors/rebuild` 端点与 `TrainingVectorsRebuildResult` schema**；端点路径/operationId/请求 schema/200 形态/其余错误码全部不变 | `contracts/training.openapi.yaml` | 契约自洽，错误口径与提案 §3.1 逐字一致 |
| 1.2 | 镜像同步（synthesize 描述/错误文案修订 + `vectors/rebuild` 端点与 schema 移除；透传语义不变） | `contracts/bilibili-crawler.openapi.yaml` | 与 1.1 一致 |
| 1.3 | `npm run codegen` 再生成 `schema.d.ts`/`schema-training.d.ts`（勿手改；`vectors/rebuild` 类型随端点消失） | `apps/web/src/api/` | 生成物与契约一致，web 无手工代码改动 |

### Phase 2: 语音模型客户端族（TDD：每任务测试先行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | `Ai/ISpeechModelClient.cs`（新增）：`SpeechSynthesizeRequest(ReferenceAudio, ReferenceContentType, ReferenceTranscript?, Text, Lang, EmotionInstruction?)`；`ISpeechModelClient.SynthesizeSpeechAsync(TrainingProviderRecord, TrainingModelRecord, SpeechSynthesizeRequest, ct) → Task<byte[]>`（WAV 字节；不可达/超时/非 2xx/空响应 → `SpeechModelUnavailableException`；响应无音频段 → `ModelOutputException`）；`ISpeechModelClientFactory.Create(providerType)`；异常 `SpeechModelUnavailableException`/`ModelAudioOutputNotSupportedException`（→400） | `servers/training/Ai/ISpeechModelClient.cs` | 签名对齐 `IImageModelClient` 先例；与提案 §3.2 一致 |
| 2.2 | `Ai/OpenAiLikeSpeechClient.cs`（新增）：复用 named client `AiModel`；`POST {provider.BaseUrl}/chat/completions` + `Authorization: Bearer {apiKey}`，body `model`/`modalities:["text","audio"]`/`audio:{"format":"wav"}`/messages 单 user 两段（`input_audio`（base64 参考语音 + format 映射）+ `text`（`SpeechPromptBuilder` 产物））；解析 `choices[0].message.audio.data` base64 → 字节；缺/空/非法 base64 → `ModelOutputException`；非 2xx/超时/传输失败 → `SpeechModelUnavailableException`；客户端取消原样上抛；日志不打 apiKey/文本/音频 | `servers/training/Ai/OpenAiLikeSpeechClient.cs` | `OpenAiLikeSpeechClientTests`（假 `HttpMessageHandler`）转绿：请求体/鉴权头/format 映射/解析/异常矩阵 |
| 2.3 | `Ai/SpeechModelClientFactory.cs`（新增）：`openai` → `OpenAiLikeSpeechClient`；`claude` 及未知 → `ModelAudioOutputNotSupportedException`/`ProviderTypeException`（先例 `ImageModelClientFactory`） | `servers/training/Ai/SpeechModelClientFactory.cs` | 工厂派发用例转绿（含 claude 400 路径） |

### Phase 3: 纯函数（TDD：每任务测试先行，互相独立可并行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 3.1 | `Synthesis/EmotionMatcher.cs`（新增纯函数）：`SelectTop1(IReadOnlyList<EmotionCandidate> candidates, float[] query) → EmotionMatch?`（候选 itemId/audioKey + 八维；平方欧氏距离 top-1，同分 itemId 升序；空候选/长度不符 → null） | `servers/training/Synthesis/EmotionMatcher.cs` | `EmotionMatcherTests` 转绿（top-1/tie-break/空候选/维度不符/确定性） |
| 3.2 | `Synthesis/SpeechPromptBuilder.cs`（新增纯函数）：`BuildEmotionInstruction(EmotionResult?)`（描述优先；空则八维最强维度映射情绪名，同分按 `Dimensions` 序）+ `Build(SpeechSynthesizeRequest)`（角色指令 → 参考转写（可空）→ 语种提示（非默认才附）→ 情绪指令（可空）→ 目标文本） | `servers/training/Synthesis/SpeechPromptBuilder.cs` | `SpeechPromptBuilderTests` 转绿（转写有无 × 语种 × 情绪有无全组合，纯字符串断言） |
| 3.3 | `Synthesis/EmotionVector.cs` 瘦身：删 `Format`/`NormalizeEmoAlpha`（仅服务 emo_\*）；`FromEmotion`/`Dimensions` 保留并补注释「内存情绪匹配向量化」 | `servers/training/Synthesis/EmotionVector.cs` | `EmotionVectorTests` 改为仅覆盖 `FromEmotion`/`Dimensions` 转绿 |

### Phase 4: 编排替换 + 功能点 + 配置迁移（TDD）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 4.1 | `FeaturePointIds` +`speech-synthesize`（`SpeechSynthesizeDefinition`：输入 `audio,text` / 输出 `audio`，中文名「语音合成」） | `servers/training/FeaturePoints/FeaturePointIds.cs` | 与 `image-generate` 定义同构；Resolver 登记 upsert 无需改动 |
| 4.2 | `Configuration/SpeechOptions.cs`（新增，节 `Speech`，`ValidateOnStart`）：`DefaultLang`（默认 `ZH`，白名单校验迁移）/`SupportedLangs`（`ZH/EN/JA/ES/AR`，自 `TtsOptions` 迁入）；lang 校验与缺省改读此节 | `servers/training/Configuration/SpeechOptions.cs`、`TrainingSynthesizeController.cs` | 既有 lang 用例口径零回退（`invalid lang` 文案/时机不变） |
| 4.3 | `SynthesizeService` 改造：① 参考产物增补**转写**（参与条目 `Text` 按序「，」拼接，全空 → null；单条回退取该条目 `Text`）；② `llm` 情绪路径改 `EmotionMatcher` 内存 top-1（候选 = 该数据集 `Extensions.emotion` 非空条目经 `FromEmotion` 向量化；不再逐候选取音频，`EmotionMatch` 瘦身为 `EmotionResult + AudioKey`）；③ 上游调用 `ISpeechModelClient.SynthesizeSpeechAsync`（`SpeechSynthesizeRequest`：参考音频 + 转写 + 目标文本 + lang + 情绪指令（`SpeechPromptBuilder.BuildEmotionInstruction`））；④ `SynthesizeResult` 与命中头产出不变；Info 日志口径不变（不打文本/音频） | `servers/training/Synthesis/SynthesizeService.cs` | `SynthesizeServiceTests` 转绿：转写传递/情绪三模式（llm 内存匹配确定性、dataset、none）/降级中性/取消不降级/既有挑选用例零回退 |
| 4.4 | `TrainingSynthesizeController`：校验链插入**语音模型解析**（`ResolveAsync(SpeechSynthesizeDefinition)`，无 → 409 `no speech model available`；工厂派发 `ModelAudioOutputNotSupportedException` → 400 `model does not support audio output`）并传入 `SynthesizeService`；异常映射：`SpeechModelUnavailableException` 502 `model service unavailable`、`ModelOutputException` 502 `invalid model output`（`AudioUnavailableException` 502 不变） | `servers/training/Controllers/TrainingSynthesizeController.cs` | `TrainingSynthesizeApiTests` 新增用例转绿（409/400/502 新口径 + 既有 400/404/409/502 audio 不回退） |

### Phase 5: TTS 与 Milvus 彻底移除（依赖 Phase 4 完成：先替换后删除）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 5.1 | 删除 `Clients/ITtsClient.cs`/`Clients/IndexTtsClient.cs`（`TtsSpeakerReference`/`TtsEmotionReference`/`TtsSynthesizeRequest`/`TtsUnavailableException` 随之消失） | `servers/training/Clients/` | 全仓无 `ITtsClient` 引用（编译即证） |
| 5.2 | **删除 `Vectors/` 整目录** + `Configuration/MilvusOptions.cs`/`EmbeddingOptions.cs`；`SynthesizeService`/控制器移除 `IVectorStore`/`Embedding*`/`VectorStore*` 残留引用与 catch | `servers/training/Vectors/`、`Configuration/` | 全仓无 Milvus/Embedding 引用（编译即证） |
| 5.3 | 移除 `TrainingSynthesizeController.RebuildVectors` action 与 `Dtos.cs` `TrainingVectorsRebuildResultDto` | `Controllers/TrainingSynthesizeController.cs`、`Contracts/Dtos.cs` | 与契约 1.1 一致；`segments/rebuild` 不动 |
| 5.4 | 移除向量同步钩子 5 处：`TrainingDatasetItemsController`（导入/编辑/批量删除）、`TrainingDatasetsController`（删除数据集）、`EmotionTaskRunner`（情绪写入）——`VectorSyncService` 注入与调用整体摘除，`SegmentSyncService` 同步保留 | `Controllers/TrainingDatasetItemsController.cs`、`TrainingDatasetsController.cs`、`Emotion/EmotionTaskRunner.cs` | 导入/编辑/情绪写入/删除无向量副作用（回归用例）；段同步行为不变 |
| 5.5 | `Program.cs`：注销 `TtsService`/`EmbeddingService`/`MilvusStore` named clients、`ITtsClient`/`IEmbeddingClient`/`IMilvusRestClient`/`IVectorStore`/`VectorSyncService` 与启动 `EnsureCollectionsAsync`；删 `TtsOptions`/`MilvusOptions`/`EmbeddingOptions` 绑定与 `Tts:DefaultLang` 校验；注册 `SpeechOptions` + `ISpeechModelClientFactory`；残留 `Tts`/`Milvus`/`Embedding` 节非空 → 启动 Warn 一次并忽略（先例 `Synthesize:DefaultEmotionModelId`） | `servers/training/Program.cs`、`Configuration/TtsOptions.cs`（删） | 启动无 Milvus/TTS/Embedding 配置不报错；残留配置 Warn 不阻断 |
| 5.6 | `infra/apphost/AppHost.cs`：移除 `milvus` 资源（`AddMilvus` + `milvus-api-key` 参数）与 training 侧 `WaitFor(milvus)`、`Milvus__Endpoint`/`Milvus__Token`/`Embedding__BaseUrl`/`Tts__BaseUrl` 注入 | `infra/apphost/AppHost.cs` | 编排无 Milvus/Ollama/TTS 依赖 |
| 5.7 | 测试删除/改造：删 `MilvusFixture`/`MilvusRestClientTests`/`MilvusVectorStoreTests`/`VectorSyncServiceTests`/`LocalEmbeddingClientTests`/`TrainingDatasetVectorCascadeTests`/`IndexTtsClientTests`；`TrainingSynthesizeFakes` 删 `StubMilvusRestClient`/`FakeEmbeddingClient`/`InMemoryVectorStore`、`StubTtsClient` → `StubSpeechClient`；`TrainingSynthesizeApiTests` 去 `MilvusFixture` | `tests/Z.Vtuber.Tests/` | 编译通过、剩余用例全绿 |

### Phase 6: api 代理镜像与 web codegen（零代码改动为主）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 6.1 | `TrainingProxyController` 移除 `vectors/rebuild` 镜像 action；`synthesize` 透传零改动复核 | `servers/api/Controllers/TrainingProxyController.cs` | 与契约 1.2 一致；`TrainingProxyController.cs` 除删除行外零 diff |
| 6.2 | 代理测试同步：`TrainingSynthesizeProxyTests` 错误文案断言（`model service unavailable`/`invalid model output`）+ 删 `vectors/rebuild` 用例 + 补「端点移除后 404」；`TrainingProxyTimeoutConsistencyTests` 超时口径收敛（180s > `Ai:RequestTimeoutSeconds` 120s，删 Tts/Embedding/Milvus 行） | `tests/Z.Vtuber.Tests/` | 全绿；`servers/api/Program.cs` 注释同步（不再引用 Tts/Embedding 预算） |
| 6.3 | `npm run codegen` 复核 0 diff（1.3 已生成，此处收口断言） | `apps/web/src/api/` | 生成物与契约一致 |

### Phase 7: 测试收口与文档同步

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 7.1 | 全量回归：`dotnet build` + podman 容器 UT（仅 Mongo/Redis，无 Milvus）；既有 `Training*`/`Model*`/`Emotion*`/`TrainingProxy*`/绘图/数据集用例不回退 | 全部 | 全绿 |
| 7.2 | 跑测口径：trx + `tests/Z.Vtuber.TestReport --check`（单用例 ≤ 5s，新增慢用例须登记 `TimeoutExceptions.json`） | 全部 | 通过 |
| 7.3 | 覆盖率：核心逻辑（`OpenAiLikeSpeechClient` 请求/解析、`EmotionMatcher`、`SpeechPromptBuilder`、功能点解析、编排替换点）≥ 80% | 全部 | 达标 |
| 7.4 | 文档同步：`servers/training/AGENTS.md`（删 Milvus/TTS/embedding 全部条目与 `vectors/rebuild` API 行；补 `Ai/` 语音客户端族、`Synthesis/EmotionMatcher`/`SpeechPromptBuilder`、`Speech` 配置节、功能点 `speech-synthesize`、部署要点改「需 Models 管理配置语音模型」）、`servers/api/AGENTS.md`（`vectors/rebuild` 镜像移除、超时口径）、PRD 验收项勾选、提案/Plan 互链 | 文档 | 与实现一致 |

## 4. 并行策略

- **Phase 1 契约先行**（1.1 → 1.2 → 1.3）与 Phase 2/3 并行（契约文件与 Ai/纯函数代码无冲突）。
- **Phase 2** 内部顺序 2.1 → 2.2/2.3（2.2、2.3 可并行）；**Phase 3** 三个任务互相独立，可全并行。
- **Phase 4** 依赖 2.1（接口定稿）与 3.1/3.2（纯函数）；4.1/4.2 可先行，4.3 依赖 2.1/3.1/3.2/4.1，4.4 依赖 4.2/4.3。
- **Phase 5** 严格在 Phase 4 后（先替换后删除，保证每步可编译）；5.1–5.4 可并行，5.5/5.6 随后，5.7 收口。
- **Phase 6** 依赖 1.2 契约定稿与 5.3（端点移除）；6.2 可与 5.x 并行准备。
- **Phase 7** 最后收口；7.4 文档同步在实现定稿后。
- **subagent 编排（z-coding「编写实现」）**：`servers/training`（Phase 2–5 主体，1 个 subagent 承担，避免跨任务冲突）；`contracts/` + `apps/web` codegen（Phase 1/6.3，1 个 subagent）；`servers/api` + `infra/apphost`（Phase 5.6/6，1 个 subagent）；各自 TDD 红-绿、测试与实现分开提交、按第 2 节提交顺序落 PR。

## 5. 验收对照

| PRD/提案目标 | 实现任务 | 验证 |
|--------------|---------|------|
| 功能点 1：传数据集+文本返回语音大模型生成的 `audio/wav`；经代理同口径可访问；无可用语音模型明确错误 | 2.x、4.3、4.4、6.1 | `TrainingSynthesizeApiTests`（200 WAV 透传 / 409 `no speech model available` / 400 `model does not support audio output`）；`TrainingSynthesizeProxyTests` 透传；PRD 功能点 1 |
| 功能点 2：参考挑选口径零回退（同数据集同文本稳定命中）；无可用参考 409；参考文本随音频提交 | 4.3 | `SynthesizeServiceTests` 既有挑选用例零回退 + 转写拼接断言；`OpenAiLikeSpeechClientTests` `input_audio`+`text` 两段断言；PRD 功能点 2 |
| 功能点 3：情绪倾向保持（命中→指令注入；无命中/失败→中性仍 200；开关口径不变） | 3.2、3.3、4.3 | `EmotionMatcherTests`/`SpeechPromptBuilderTests`/`SynthesizeServiceTests` 三模式用例；PRD 功能点 3 |
| 功能点 4：原 TTS 调用与配置完全移除 | 5.1、5.5、5.6 | 编译即证无引用；启动无 `Tts` 配置不报错；PRD 功能点 4 |
| Milvus 彻底移除（含 `vectors/rebuild` 下线、无索引维护、残留配置 Warn 忽略） | 5.2–5.7、1.1/1.2、6.2 | `vectors/rebuild` 404 用例；导入/编辑/情绪写入无向量副作用回归；启动 Warn 用例；PRD §4.3 已知破坏性变更 |
| 错误码矩阵：400 text/lang/emotionMode/不支持音频输出；404 dataset/model；409 无参考/无语音模型；502 audio/`model service unavailable`/`invalid model output` | 4.4、1.1 | `TrainingSynthesizeApiTests` 全矩阵 + 契约描述逐字一致 |
| 契约先行、api 镜像一致、codegen 0 diff | 1.1–1.3、6.3 | 契约评审；6.3 复核 |
| 回归不回退、覆盖率 ≥ 80%、单用例 ≤ 5s | 7.1–7.3 | 7.1–7.3 |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|----|------|------|
| 上游模型音频输出形态差异 | OpenAI-like 实现差异（`audio.data` 缺失/`url` 形态/base64 非法） | 假 `HttpMessageHandler` 契约测试锁定解析与异常矩阵；缺音频段 → 502 `invalid model output` 可诊断（提案 §5.2 实现点 1） |
| 情绪命中口径漂移（Milvus top-k → 内存 top-1） | 现状 top-16 后逐候选取首个可取音频者；改造为精确 top-1 | 内存 L2 与 FLAT 等价、结果确定（同分 itemId 升序）；`EmotionMatcherTests` 锁定确定性；X-Emo-Audio-Key 口径变化记入 1.1 澄清 1（不再要求命中条目音频可取） |
| 超长文本无本地闸门 | 模型侧拒绝 → 502 | PRD 明确本期不做截断策略；契约描述显式说明（1.1 澄清 5） |
| 校验/解析顺序改变错误优先级 | 「无语音模型」与「无参考语音」同时命中时的返回码 | 固定顺序（1.1 澄清 4）并以用例断言；既有 modelId 404/400 用例零回退 |
| 移除面大（Milvus + TTS 双系统） | 编译断裂/漏删引用/测试假体残留 | 严格「Phase 4 先替换、Phase 5 后删除」；每步 `dotnet build` + 相关 UT 转绿再进下一步；5.7 集中清理假体 |
| 上线前置：功能点 `speech-synthesize` 无可用模型 | 线上合成 409 | 部署检查清单项（提案 §9.2）：Models 管理配置「输入 `audio,text` / 输出 `audio`」可用模型并指定/默认；运维侧另行下线 Milvus/Ollama/TTS 实例 |
| 回滚 | 移除面为删除式改造 | 直接回退 training/api/apphost 镜像即恢复原 TTS + Milvus 链路（无数据迁移、Mongo schema 零变更；Milvus 向量为派生索引直接弃置） |
| 无新增依赖 | 零新增 NuGet/环境变量/流水线/项目引用（`ProjectReferenceGuardTests`/`WorkflowPathsGuardTests` 口径不变）；配置面净减（`Tts`/`Milvus`/`Embedding` 移除、`Speech` 新增） | - |

## 7. 相关文档

- PRD：`docs/product/draft/20261014-training-audio-synthesize-llm-prd.md`
- 技术提案：`docs/engineering/proposals/20261014-proposal-training-audio-synthesize-llm.md`
- 被改造的既有文档：`20260929-training-audio-synthesize-prd.md`、`20260929-proposal-training-audio-synthesize.md`、`20260929-plan-training-audio-synthesize.md`、`20260930-plan-training-synthesize-emotion-mode.md`
- 同构先例（模态客户端 + 功能点 + 能力白名单）：`20261001-proposal-training-image-generate.md`、`20261002-plan-training-image-multi-input.md`、`20261001-proposal-dataset-model-capability.md`
- 情绪链路：`20260928-proposal-training-dataset-emotion.md`、`20260930-proposal-training-synthesize-emotion-mode.md`
- 模块规范：`servers/training/AGENTS.md`、`servers/api/AGENTS.md`、`servers/common/AGENTS.md`、`apps/web/AGENTS.md`
- 规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`
- 契约：`contracts/training.openapi.yaml`、`contracts/bilibili-crawler.openapi.yaml`
