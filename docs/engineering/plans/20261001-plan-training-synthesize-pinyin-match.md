---
description: AI 训练模块「音频生成 - 拼音匹配与断句拼接参考音源」实现 Plan：synthesize 音色参考升级为「输入文本按 [，。,.\s] 断句 → 每条短句与数据集候选短句计算拼音相似度（音节级归一化编辑距离）取最相似 1 条 → 命中按数据 id 去重 → recordings POST /api/recordings/audio/merge ffmpeg 合并拼接音源 → speaker_prompt」；新增 training_dataset_item_segments 候选短句反向索引集合与 segments/rebuild 重建接口（含条目写路径增量同步）、TextSplitter/PinyinConverter/PinyinSimilarity/SegmentSyncService、RecordingAudioClient.MergeClipsAsync 与 recording AudioClipMerger；三契约 + web codegen，TDD 覆盖断句/拼音/相似度/同步/编排/控制器
type: Permanent
---

# AI 训练 - 音频生成拼音匹配与断句拼接参考音源 - 实现 Plan

**功能名称**: 训练 - 音频生成（拼音匹配与断句拼接参考音源）
**关联 PRD**: `docs/product/draft/20261001-training-synthesize-pinyin-match-prd.md`（v1.1）
**关联技术提案**: `docs/engineering/proposals/20261001-proposal-training-synthesize-pinyin-match.md`（v1.1）
**Plan 版本**: v1.0
**创建日期**: 2026-10-01
**作者**: Workflow Agent（代起草）
**feat-branch**: `feat/training-synthesize-pinyin-match`

> 口径说明（2026-10-01 评审确认，已并入 PRD/提案 v1.1）：拼音匹配**要计算距离**——匹配口径为**拼音相似度（音节级归一化编辑距离）**，与原字符匹配同为编辑距离形状；**断句的目的是获得更多符合的短句**——输入文本拆短句后**每条短句分别**取拼音最相似的 1 条候选短句，汇总多条命中；命中按**数据 id 去重**后经 recordings 合并为拼接音源作为推理 `speaker_prompt`。

## 1. 概述

在既有 `synthesize`（字符相似度匹配单条音色参考 + 情绪按 `emotionMode` 取参考）之上，把音色参考升级为「断句 + 拼音相似度多命中 + 多音频拼接」。改动分五条线：① 数据层——`servers/common/training` 新增候选短句集合 `training_dataset_item_segments`（文本 + 拼音音节序列 + 所属数据 id，即反向索引载体）；② 纯函数——`Synthesis/TextSplitter.cs`（断句）、`Synthesis/PinyinConverter.cs`（文本 → 无声调拼音音节序列，新 NuGet 依赖）、`Synthesis/PinyinSimilarity.cs`（音节级归一化编辑距离）；③ 编排——`Synthesis/SegmentSyncService.cs`（rebuild + 增量同步）、`SynthesizeService` 音色链路重构（每句 top-1 → 去重 → 合并，无候选回退字符匹配）、`RecordingAudioClient.MergeClipsAsync`、`TrainingSegmentsController`（rebuild）；④ recording 合并能力——`Audio/AudioClipMerger.cs` + `POST /api/recordings/audio/merge`；⑤ 接入面——三份 OpenAPI + web codegen。实施遵循 `docs/engineering/common-rules.md`（契约先行、TDD、核心逻辑覆盖率 ≥ 80%、单用例 ≤ 5s）、`backend-rules.md`、z-coding 流程与 `servers/{training,recording,common}/AGENTS.md`。

### 1.0 现状核实结论（对照代码）

- `Synthesis/SynthesizeService.cs`：`RankSpeakerCandidatesAsync`（Mongo 全量条目 + `TextSimilarity` 降序、同分 itemId 升序、Take `ReferenceCandidateLimit`）→ `ScanCandidatesAsync`（首个可用音频为 speaker，dataset 模式同轮取首个带情绪标注）→ 情绪按 `EmotionMode` → `ITtsClient`；`ReferenceMatch(Item, Clip)` 为单条命中记录；`SynthesizeResult(Audio, SpeakerAudioKey, EmoAudioKey)` 单值命中头。
- `Synthesis/TextSimilarity.cs`：`Ratio(a,b)=1-Levenshtein/max(len)`，两行滚动数组——`PinyinSimilarity` 直接复用其算法形状，仅把单位从字符换成**音节 token**。
- `Synthesis/EmotionMode.cs`：既有 `Llm`/`Dataset`/`None`（默认 `None`），本期零改动。
- `Clients/IRecordingAudioClient`：仅 `GetClipAsync(audioUri, startMs, endMs)`（`AudioClip(Bytes, ContentType)`）；`RecordingAudioClient` 经 `TrainingMapping.ParseAudioUri` 解析 `b/live/{roomId}/recordings/{file}` 后调 recording 片段端点——合并请求项复用同一解析产物。
- `Controllers/TrainingSynthesizeController.cs`（`[Route("api/training/datasets/{datasetId}")]`）：`POST synthesize` 与 `POST vectors/rebuild`（200 `{updated}`）；`TrainingDatasetItemsController`/`TrainingDatasetsController` 以可空 `VectorSyncService?` 在写路径 best-effort 增量同步（导入 119 行、更新 215 行、批量删除 249 行、数据集删除 155 行）——`SegmentSyncService` 挂接点逐处同位平行。
- `Emotion/EmotionTaskRunner.cs` 只写条目 `Extensions.emotion`，不动 `Text` → **无需**挂 segments 增量同步。
- `servers/common/training`：实体在 `Entities/`、仓储在 `Persistence/`、集合名 `Mongo/TrainingCollectionNames.cs`、索引 `Mongo/TrainingMongoSetup.EnsureIndexesAsync`——新增 segments 实体/仓储/集合名/索引沿同结构落位。
- `servers/recording`：`Audio/AudioClipExtractor.cs`（ffmpeg 输入 seek + `-vn -ac 1 -ar 44100 -b:a 96k -f mp3 pipe:1`、取消 kill 回收、`AudioClipFailedException`）与 `Mp3EncoderSupport`（libmp3lame 探测降级 wav）即合并实现的范本；`RecordingController`（`[Route("api")]`）`ResolveRecordingFileAsync`（平文件名 → 前缀校验 → 录制中 400 → 不存在 404）供 merge 端点逐 item 复用；`RecordingOptions` 节 `Recording`（`AudioClipMaxDurationMs` 既有护栏口径）。
- `servers/api/Controllers/TrainingProxyController.cs`：`/api/training/**` 为**逐端点显式路由** + `ForwardAsync` 透传（共 24 个，含 `vectors/rebuild`）——新增 training 端点需同构补一条薄代理路由（见 1.2 偏差 1）。
- 契约：`contracts/training.openapi.yaml`（synthesize 在 617 行、`vectors/rebuild` 在 679 行，`X-Speaker-Audio-Key` 现为单值）、`contracts/recording.openapi.yaml`（片段端点在 163 行）、`contracts/bilibili-crawler.openapi.yaml`（`/api/training/**` 网关镜像）；web `npm run codegen` 由 7 份契约生成 `schema*.d.ts`（勿手改）。
- 测试范式：`SynthesizeServiceTests`（service 直测 + `MongoDbFixture`）、`TrainingSynthesizeApiTests`（控制器直测 + fakes）、`TrainingSynthesizeFakes`、`RecordingAudioClientTests`（假 `HttpMessageHandler` 契约测试）、`VectorSyncServiceTests`/`TrainingDatasetVectorCascadeTests`（增量同步与级联范本）。

### 1.1 实施范围

- **做**：
  - `contracts/`：training（`segments/rebuild` 新增、synthesize 描述 + 命中头多值）、recording（`POST /api/recordings/audio/merge`）、bilibili-crawler（网关镜像同步 training 侧）+ `apps/web` codegen 再生成；
  - `servers/common/training`：`TrainingDatasetItemSegmentRecord`、`TrainingDatasetItemSegmentRepository`、`TrainingCollectionNames`、`TrainingMongoSetup` 索引；
  - `servers/training`：`Synthesis/TextSplitter.cs`、`Synthesis/PinyinConverter.cs`（新 NuGet 拼音库）、`Synthesis/PinyinSimilarity.cs`、`Synthesis/SegmentSyncService.cs`、`Synthesis/SynthesizeService.cs` 重构、`Clients/IRecordingAudioClient`/`RecordingAudioClient` 增 `MergeClipsAsync`、`Controllers/TrainingSegmentsController.cs`、条目/数据集写路径挂增量同步、`Configuration/SynthesizeOptions.cs` 增 `MergedReferenceLimit`、`Program.cs` 注册；
  - `servers/recording`：`Audio/AudioClipMerger.cs`、`Controllers/RecordingController.cs` 增 merge 端点、`RecordingOptions`/`RecordingContracts` 增合并护栏与 DTO；
  - `servers/api`：`TrainingProxyController` 增 `segments/rebuild` 薄代理路由（偏差 1）；
  - `tests/Z.Vtuber.Tests`：新增 `TextSplitterTests`/`PinyinConverterTests`/`PinyinSimilarityTests`/`SegmentSyncServiceTests`/`TrainingSegmentsApiTests`/`RecordingAudioMergeApiTests`/`AudioClipMergerTests`，扩展 `SynthesizeServiceTests`/`TrainingSynthesizeApiTests`/代理测试；
  - 文档：`servers/training/AGENTS.md`、`servers/recording/AGENTS.md`、`servers/common/AGENTS.md`、`servers/api/AGENTS.md` 同步 + PRD/提案/Plan 互链。
- **不做**：情绪参考三模式语义、Milvus/Ollama 向量链路与 `vectors/rebuild`、`IndexTtsClient` 上游契约、`servers/api` `ForwardAsync` 透传机制、数据模型既有字段、Web 界面/批量合成；不做句级音频时间轴切分（拼接粒度 = 整条数据音频）、多音字消歧、相似度阈值/top-k 调优、拼音倒排召回结构、跨数据集全量 rebuild。

### 1.2 Plan 对提案的补充与偏差（供 review 确认）

| # | 项 | 说明 |
|---|----|------|
| 偏差 1 | `servers/api` 代理路由 | 提案写「网关镜像同步 `segments/rebuild` 端点；`servers/api` 代理透传零改动」。核实：`TrainingProxyController` 是逐端点显式路由而非通配转发，网关镜像若收录该端点而无代理路由会契约/行为不一致。按「与既有 `vectors/rebuild` 同构」补一条薄代理路由（`ForwardAsync` 透传机制本身零改动），并在 `bilibili-crawler.openapi.yaml` 镜像同步 |
| 补充 1 | 增量同步挂接面 | 提案列导入/更新/批量删除/数据集删除四处；核实 `EmotionTaskRunner` 仅写 `Extensions.emotion` 不涉文本，**不挂** segments 同步（在 Plan 中显式排除，避免实现时误挂） |
| 补充 2 | codegen 影响面 | 网关镜像变更会同时再生成 `apps/web/src/api/schema.d.ts`（bilibili-crawler 源），与 `schema-training.d.ts`/`schema-recording.d.ts` 一并 0 diff 复核 |
| 补充 3 | 命中头兼容 | `X-Speaker-Audio-Key` 多值后 `SynthesizeResult.SpeakerAudioKey` 改为按拼接顺序的 audioKey 序列；回退路径单值语义不变（单元素序列 = 原值），控制器 join 输出逗号分隔 |

## 2. 分支与提交流程

- feat-branch `feat/training-synthesize-pinyin-match`（需求总分支，基于 `origin/main`），本 Plan 直接提交至该分支。
- 提交顺序：`docs(training):` 本 Plan → `feat(contracts):` 三契约 + codegen → `feat(common):` segments 数据层 → `feat(training):` 纯函数与编排 → `feat(recording):` 合并能力 → `test(training):`/`test(recording):` 测试 → `docs:` AGENTS.md 同步。Conventional Commits，测试与实现分开提交。

## 3. 实施阶段

### Phase 0: 技术文档（先行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | 实现 Plan（本文档） | `docs/engineering/plans/20261001-plan-training-synthesize-pinyin-match.md` | 任务/文件/验收明确，与 PRD/提案 v1.1 口径一致 |
| 0.2 | PRD/提案/Plan 互链复核 | PRD、提案 §11 | 链接指向正确、口径无漂移 |

### Phase 1: 契约先行

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | `training.openapi.yaml`：新增 `POST /api/training/datasets/{datasetId}/segments/rebuild`（200 `{"rebuilt": int}` 新 schema、404 `dataset not found`）；synthesize `description` 改「断句 + 拼音相似度 + 拼接音源、无候选回退字符相似」，`X-Speaker-Audio-Key` 描述改逗号分隔多值（按拼接顺序），502 口径不变（合并取数失败归入 `audio unavailable`） | `contracts/training.openapi.yaml` | 契约自洽，与提案 §3.1 一致 |
| 1.2 | `recording.openapi.yaml`：新增 `POST /api/recordings/audio/merge`（`AudioMergeRequest{items:[{roomId,fileName,startMs,endMs}]}`；200 `audio/mpeg` + `Cache-Control: no-store`；400 `items is required`/`too many items`/`invalid time range`/`total duration too long`/`recording in progress`；404 `file not found`；500 `audio merge failed`） | `contracts/recording.openapi.yaml` | 护栏与错误码齐备 |
| 1.3 | `bilibili-crawler.openapi.yaml`：`/api/training/**` 镜像同步 1.1 两个端点变更（含 segments/rebuild 路由） | `contracts/bilibili-crawler.openapi.yaml` | 与 1.1 一致 |
| 1.4 | `npm run codegen` 再生成 `schema.d.ts`/`schema-training.d.ts`/`schema-recording.d.ts`（勿手改） | `apps/web/src/api/*` | 生成物与契约一致，复核 0 diff |

### Phase 2: 数据层（servers/common/training）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | `TrainingCollectionNames` 增 `TrainingDatasetItemSegments = "training_dataset_item_segments"` | `Mongo/TrainingCollectionNames.cs` | 常量命名与既有域一致 |
| 2.2 | `TrainingDatasetItemSegmentRecord`：`Id`/`DatasetId`/`ItemId`/`AudioKey`/`SegmentIndex`/`Text`/`Pinyin`（音节单空格 join）/`CreatedAt` | `Entities/TrainingDatasetItemSegmentRecord.cs` | 字段与提案 §4.1 一致 |
| 2.3 | `TrainingDatasetItemSegmentRepository`：`ListByDatasetAsync`/`ReplaceForItemAsync`（删旧插新）/`DeleteByDatasetAsync`/`InsertManyAsync`/`CountByDatasetAsync` | `Persistence/TrainingDatasetItemSegmentRepository.cs` | API 面与提案 §3.2 一致 |
| 2.4 | `TrainingMongoSetup.EnsureIndexesAsync` 幂等挂 `(DatasetId)`、`(DatasetId, ItemId)` 索引（不建 Pinyin 等值索引——相似度为内存计算） | `Mongo/TrainingMongoSetup.cs` | 幂等可重入，`TrainingMongoSetupTests` 转绿 |

### Phase 3: 纯函数（servers/training/Synthesis）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 3.1 | `TextSplitter.Split(text)`：正则 `[，。,.\s]+` 拆分 → Trim → 丢空段 → 保序；纯函数确定性 | `Synthesis/TextSplitter.cs` | 单测转绿（中英文逗号/句号/空白混合、空段、纯标点） |
| 3.2 | `PinyinConverter.ToSyllables(text)`：逐字符——汉字转**无声调小写**拼音（1 字 = 1 音节）、ASCII 字母/数字连续段小写为 1 token、其余丢弃；落库单空格 join；新增 NuGet `TinyPinyin.Net`（本地词典无网络依赖，以 NuGet 可用性与多音字口径验证为准，不达标回退同类库，决策点隔离在本类） | `Synthesis/PinyinConverter.cs`、`Z.Vtuber.Training.csproj` | 同音字同音节、同一文本同一序列；单测转绿 |
| 3.3 | `PinyinSimilarity.Ratio(a, b)`：音节 token 序列归一化 Levenshtein `1 - dist/max(len)`，两行滚动数组；均空 → 1、一方空 → 0；确定、对称 | `Synthesis/PinyinSimilarity.cs` | 同音句距离 0、近音高分、长度惩罚；单测转绿 |

### Phase 4: training 编排与接入面

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 4.1 | `SegmentSyncService`：`RebuildAsync(datasetId)`（DeleteMany → 全量条目（过滤空 Text）→ 逐条 Split + ToSyllables（SegmentIndex 保序）→ InsertManyAsync，返回总数）；增量 `SyncItemAsync`/`DeleteItemsAsync`/`DeleteDatasetAsync`，best-effort 失败 Warn，rebuild 兜底对齐 | `Synthesis/SegmentSyncService.cs` | 仓储/同步测试转绿 |
| 4.2 | `SynthesizeService` 音色链路重构：`MatchSpeakerSegmentsAsync`（断句 → 各句拼音 → `ListByDatasetAsync` 加载候选 → 每句对全部候选算 `PinyinSimilarity.Ratio` 取最相似 1 条，同分 `itemId` 升序 + `SegmentIndex` 升序 tie-break）→ 按 `ItemId` 去重（输入句序优先）→ 回读条目 → 过滤音频不可用 → 截断 `MergedReferenceLimit` → `MergeClipsAsync` 合并为 `speaker_prompt`；合并整体失败逐条单片段兜底（按序取首个可用），全失败 `AudioUnavailableException`；**无候选短句回退既有字符相似路径**（`RankSpeakerCandidatesAsync` + 单片段扫描，语义不变）；dataset 情绪沿去重后条目取首个带 `Extensions.emotion`；命中头多值（`SpeakerAudioKey` 序列）；Info 日志增 `speakerKeys=N`，不打文本/音频 | `Synthesis/SynthesizeService.cs` | 编译通过；每句 top-1/去重/回退/兜底用例转绿 |
| 4.3 | `IRecordingAudioClient.MergeClipsAsync(IReadOnlyList<AudioClipRequest>)`（`AudioClipRequest(roomId, fileName, startMs, endMs)` 由 `TrainingMapping.ParseAudioUri` 产物组装）：POST `api/recordings/audio/merge` 返回 `AudioClip`；404/非 2xx/超时 → `AudioUnavailableException`（编排层按候选失败口径降级） | `Clients/IRecordingAudioClient.cs`、`Clients/RecordingAudioClient.cs` | 假 handler 契约测试转绿 |
| 4.4 | `TrainingSegmentsController`（`POST segments/rebuild`，与 `vectors/rebuild` 同构）：dataset 404 → `SegmentSyncService.RebuildAsync` → 200 `{rebuilt}` | `Controllers/TrainingSegmentsController.cs` | 控制器测试转绿 |
| 4.5 | 写路径挂增量同步（与 `VectorSyncService` 挂接点逐处同位，可空注入 best-effort）：`TrainingDatasetItemsController` 导入/更新/批量删除、`TrainingDatasetsController` 删除；**不挂** `EmotionTaskRunner`（见补充 1） | `Controllers/TrainingDatasetItemsController.cs`、`Controllers/TrainingDatasetsController.cs` | 级联测试转绿（删除后 segments 清空） |
| 4.6 | `SynthesizeOptions` 增 `MergedReferenceLimit`（默认 8）；`Program.cs` 注册仓储/`SegmentSyncService`，`EnsureIndexesAsync` 覆盖新集合 | `Configuration/SynthesizeOptions.cs`、`Program.cs` | `ValidateOnStart` 通过 |
| 4.7 | 偏差 1：`TrainingProxyController` 增 `POST training/datasets/{datasetId}/segments/rebuild` 薄代理（`ForwardAsync` 机制零改动） | `servers/api/Controllers/TrainingProxyController.cs` | 代理测试转绿，网关可达 |

### Phase 5: recording 合并能力

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 5.1 | `AudioClipMerger.MergeAsync(items, output, ct)`：ffmpeg 单进程——每输入 `-ss {startMs}ms -t {durMs}ms -i {file}`（输入 seek 前置）、`concat=n=N:v=0:a=1` filter、`-map "[out]" -vn -ac 1 -ar 44100 -b:a 96k -f mp3 pipe:1` 直写响应流不落盘；`Mp3EncoderSupport` 缺失降级 `-f wav`；取消 kill + 回收；`exitCode != 0` 且零字节抛 `AudioMergeFailedException` 映 500 | `Audio/AudioClipMerger.cs` | 参数纯函数测试转绿 |
| 5.2 | `RecordingController` 增 `POST recordings/audio/merge`：逐 item 复用 `ResolveRecordingFileAsync`（平文件名/前缀校验/录制中 400/不存在 404）；items `1~AudioMergeMaxItems`、`startMs>=0`、`endMs>startMs`、单段 ≤ `AudioClipMaxDurationMs`、总时长 ≤ `AudioMergeMaxTotalDurationMs`；200 `audio/mpeg` + `Cache-Control: no-store` | `Controllers/RecordingController.cs` | 控制器测试转绿（200/400×4/404/500） |
| 5.3 | `RecordingOptions` 增 `AudioMergeMaxItems`(16)/`AudioMergeMaxTotalDurationMs`(600000)；合并请求/响应 DTO | `Configuration/RecordingOptions.cs`、`Contracts/` | `ValidateOnStart` 通过 |

### Phase 6: 测试与收口

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 6.1 | `TextSplitterTests`/`PinyinConverterTests`/`PinyinSimilarityTests`：断句（混合标点/空段/纯标点）；拼音（同音同音节、无声调、大小写归一、非中文保留、确定性）；相似度（同音距离 0、近音高分、长度惩罚、对称、空序列口径） | `tests/Z.Vtuber.Tests/*` | 全绿 |
| 6.2 | `SegmentSyncServiceTests`（`MongoDbFixture`）：rebuild 清空重建、增量同步（import/update/batch-delete/数据集删除级联）、`ListByDatasetAsync` 全量加载 | 同上 | 全绿 |
| 6.3 | `SynthesizeServiceTests` 扩展：每条输入短句取拼音最相似候选、断句多命中拼接、ItemId 去重、确定性排序、截断 `MergedReferenceLimit`、无候选回退字符相似（与现网一致）、合并失败单片段兜底、全失败 502、情绪三模式零回退 | 同上 | 全绿 |
| 6.4 | `TrainingSegmentsApiTests`（rebuild 200/404）、`RecordingAudioMergeApiTests`（200/400 空 items/超上限/区间非法/总时长超限/404/500）、`AudioClipMergerTests`（ffmpeg 参数纯函数）、`TrainingSynthesizeApiTests`（命中头多值）、代理测试（偏差 1 路由） | 同上 | 全绿 |
| 6.5 | 集成：rebuild 后 synthesize 拼音命中端到端（`MongoDbFixture` + 假 recording/假 TTS） | 同上 | 全绿 |
| 6.6 | `dotnet build` + podman 容器 UT（Mongo/Milvus/Redis）全绿；跑测口径走 `dotnet test --logger trx` + `tests/Z.Vtuber.TestReport --check`（单用例 ≤ 5s，慢用例登记 `TimeoutExceptions.json`）；`npm run codegen` 复核 0 diff | 全部 | 通过 |
| 6.7 | 文档同步：`servers/training/AGENTS.md`（segments 集合/API 面/配置/部署 rebuild 步骤）、`servers/recording/AGENTS.md`（merge 端点/配置）、`servers/common/AGENTS.md`（训练域实体仓储清单）、`servers/api/AGENTS.md`（代理端点数）；PRD/提案/Plan 互链 | 文档 | 与实现一致 |

关键用例清单（提案 §8.2）：

- **同音命中**：输入「今天得天气」与候选「今天的天气」拼音相同（`de` 同音节）→ 距离 0 命中；
- **近音高分**：「我喜欢吃苹果」对「我喜欢吃萍果」相似度高于无关句；
- **断句多命中**：`你好，世界。` 拆 2 句，每句各取最相似 1 条、命中 2 条不同数据 → 合并 2 段、`X-Speaker-Audio-Key` 两值；
- **去重**：同一数据被 2 个短句命中 → 仅 1 段参与合并；
- **长度惩罚**：单字短句对长候选相似度受长度比约束（ratio 分母取较长序列）；
- **回退**：segments 为空（未 rebuild）→ 走字符相似单条，结果与现网一致；
- **稳定复现**：同一数据集 + 同一文本 → 同一命中集合与拼接顺序（同分 tie-break 确定）；
- **合并降级**：拼接失败 → 首个可用单片段；全失败 → 502 `audio unavailable`。

## 4. 并行策略

- Phase 0 串行先行；Phase 1（契约）与 Phase 3（纯函数）可并行（文件无冲突）；Phase 2 依赖 Phase 3 的拼音格式口径（2.2 `Pinyin` 字段由 3.2 定义），可与 Phase 4.1 并行起步。
- Phase 4.2 依赖 2.3/3.x/4.3（编排引用仓储、纯函数与合并客户端），4.2/4.5/4.6 同服务文件相邻建议串行；Phase 5 与 Phase 4 并行（不同服务）；4.7 与 Phase 5 并行。
- Phase 6.1–6.5 可与对应实现交错 TDD（先红后绿），6.6/6.7 收口串行。

## 5. 验收对照

| 提案目标 | 实现任务 | 验证 |
|-----------|---------|------|
| 候选短句拆分 + 拼音反向索引（可按数据集加载） | 2.1–2.4、3.1、3.2、4.1 | 6.1、6.2 |
| rebuild 清空并全量重建、返回重建数 | 4.1、4.4 | 6.2、6.4 |
| 条目增删改/数据集删除增量同步（best-effort） | 4.5 | 6.2 |
| 每条输入短句拼音相似度匹配取最相似 1 条（音节级归一化编辑距离） | 3.3、4.2 | 6.1、6.3 |
| 命中按数据 id 去重 + recordings 合并拼接音源作 `speaker_prompt` | 4.2、4.3、5.1、5.2 | 6.3、6.4、6.5 |
| 无候选回退字符相似单条；情绪三模式零回退 | 4.2 | 6.3 |
| `segments/rebuild` 接口 + 网关镜像可达 | 1.1、1.3、4.4、4.7 | 6.4 |
| merge 护栏与错误码齐备、按序合并单 mp3 | 1.2、5.1–5.3 | 6.4 |
| 三契约与 codegen 一致；AGENTS.md 同步 | 1.1–1.4、6.7 | 6.6 |
| 核心逻辑覆盖率 ≥ 80%，TDD 全绿 | 6.1–6.5 | 6.6 |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| 拼音库可用性/多音字 | `TinyPinyin.Net` 口径不达标或多音字默认读音不符预期 | 决策点隔离在 `PinyinConverter`（3.2），不达标回退同类库；多音字取库默认读音（本期约束，不做消歧） |
| 行为变更 | 音色参考由字符相似单条 → 拼音相似多条拼接，命中头变多值 | 提案/PRD/AGENTS/契约明确；回退路径行为不劣化；`X-Speaker-Audio-Key` 仅观测用途 |
| 存量数据集 | 未 rebuild 时无候选短句 → 走回退路径 | 上线后对每个存量数据集执行 `POST /api/training/datasets/{id}/segments/rebuild`；未重建自动回退，不阻断 |
| 性能 | 每句 × 全候选音节 Levenshtein 内存计算 | 千级短句 × 十级输入句成本可忽略；`MergedReferenceLimit`(8) 约束拼接规模；数据量增长后再评估倒排召回（本期外） |
| 合并护栏 | 过大合并请求拖垮 recording | `AudioMergeMaxItems`(16)/`AudioMergeMaxTotalDurationMs`(600000) + 单段上限既有口径 |
| 依赖新增 | training 新 NuGet 拼音库；recording ffmpeg/libmp3lame（既有，镜像流水线已断言） | 进程内依赖无外部服务；镜像流水线 paths 已覆盖 `servers/common/training/**` |
| 偏差 1 | 网关代理路由需显式新增（提案表述为零改动） | 已列入 4.7；review 确认口径，如需收回可改为仅内网直连 training 触发 rebuild |

## 7. 相关文档

- PRD：`docs/product/draft/20261001-training-synthesize-pinyin-match-prd.md`
- 技术提案：`docs/engineering/proposals/20261001-proposal-training-synthesize-pinyin-match.md`
- 前序提案/Plan：`20260929-proposal-training-audio-synthesize.md`、`20260930-proposal-training-synthesize-emotion-mode.md`、`20261001-proposal-training-synthesize-char-match.md`、`20261001-plan-training-synthesize-char-match.md`
- 模块规范：`servers/training/AGENTS.md`、`servers/recording/AGENTS.md`、`servers/common/AGENTS.md`、`servers/api/AGENTS.md`、`apps/web/AGENTS.md`
- 规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`
