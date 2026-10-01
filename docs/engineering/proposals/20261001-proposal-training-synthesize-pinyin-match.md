---
description: AI 训练模块「音频生成 - 拼音匹配与断句拼接参考音源」技术提案：synthesize 音色参考由整段文本字符相似匹配单条，升级为「数据集条目按 [，。,.\s] 断句为候选短句 + 拼音反向索引（新增 training_dataset_item_segments 集合与 rebuild 接口）；输入文本同规则断句后逐短句拼音匹配 → 命中按数据 id 去重 → recordings 新增 POST /api/recordings/audio/merge（ffmpeg concat 合并单 mp3）→ 拼接音源作为 speaker_prompt 推理」；零命中回退原字符相似匹配，情绪参考语义零改动
type: Permanent
---

# AI 训练 - 音频生成拼音匹配与断句拼接参考音源 - 技术提案

- 日期：2026-10-01
- 状态：待评审
- feat-branch：`feat/training-synthesize-pinyin-match`
- 关联 PRD：[20261001-training-synthesize-pinyin-match-prd.md](../../product/draft/20261001-training-synthesize-pinyin-match-prd.md)
- 关联提案：[20260929-proposal-training-audio-synthesize.md](20260929-proposal-training-audio-synthesize.md)、[20261001-proposal-training-synthesize-char-match.md](20261001-proposal-training-synthesize-char-match.md)

## 1. 概述

### 1.1 背景

`POST /api/training/datasets/{datasetId}/synthesize` 的音色参考当前为「整段输入文本与数据集条目 `Text` 的归一化编辑距离匹配，取最相似 1 条」（见 [20261001-proposal-training-synthesize-char-match.md](20261001-proposal-training-synthesize-char-match.md)）。该口径存在两个短板：

1. **字符相似 ≠ 发音相似**：字面不同但同音/近音的内容无法互相命中，而克隆参考的关键是发音内容覆盖；
2. **单条参考对长文本覆盖不足**：整段长文本只命中 1 条参考，大部分句子没有对应发音的参考内容。

本期将匹配方式升级为**断句 + 拼音匹配 + 多音频拼接**：数据集条目与输入文本统一按 `[，。,.\s]` 断句；候选短句以**拼音**（无声调归一化）建**反向索引**（新 MongoDB 集合）；输入各短句按拼音精确命中多条候选，按**数据 id 去重**后调用 recordings 新增的**多片段合并接口**得到一段拼接 mp3，作为上游合成的 `speaker_prompt`。

### 1.2 目标

1. 新增候选短句集合 `training_dataset_item_segments`（文本断句 + 拼音 + 数据 id 反向索引），`(DatasetId, Pinyin)` 索引即反向索引；
2. 新增 rebuild 接口 `POST /api/training/datasets/{datasetId}/segments/rebuild`：清空该数据集反向索引并全量重建；条目增删改/数据集删除时**增量同步**候选短句；
3. synthesize 音色链路改为：输入断句 → 逐短句拼音匹配 → 数据 id 去重（确定性排序）→ recordings 合并 → 拼接音源作 `speaker_prompt`；**零拼音命中回退原字符相似匹配单条**（行为不劣化）；
4. recordings 新增 `POST /api/recordings/audio/merge`：多片段请求（文件 + 起止）经 ffmpeg 单进程 concat 合并输出单个 mp3；
5. 契约先行：training / recording / bilibili-crawler 网关镜像三份 OpenAPI 同步，web codegen 再生成；
6. 情绪参考（`llm`/`dataset`/`none`）、上游 TTS 契约、返回 `audio/wav` 语义零改动。

### 1.3 范围

- **内**：`servers/common/training`（新增 segment 实体/仓储/索引）；`servers/training`（`Synthesis/TextSplitter.cs`、`Synthesis/PinyinConverter.cs`、`Synthesis/SegmentSyncService.cs`（重建+增量）、`SynthesizeService` 音色链路重构、`RecordingAudioClient` 增合并方法、`TrainingSegmentsController`（rebuild）、条目/数据集写路径挂增量同步、配置）；`servers/recording`（`Audio/AudioClipMerger.cs`、`RecordingController` 增 merge 端点、配置）；`contracts/training.openapi.yaml`、`contracts/recording.openapi.yaml`、`contracts/bilibili-crawler.openapi.yaml`；`apps/web` codegen 产物；`tests/Z.Vtuber.Tests`；`servers/training/AGENTS.md`、`servers/recording/AGENTS.md`、`servers/common/AGENTS.md`、PRD。
- **外**：情绪参考三模式语义、Milvus/Ollama 向量链路、`vectors/rebuild`、`IndexTtsClient` 上游契约、`servers/api` 代理实现（`ForwardAsync` 透传零改动）、数据模型既有字段、Web 界面；不做句级音频时间轴切分（拼接粒度 = 整条数据音频）、不做多音字消歧调优。

## 2. 技术架构

### 2.1 系统架构图（改动后音色链路）

```mermaid
flowchart TD
    A[synthesize: text] --> B[TextSplitter 按 断句规则 拆分]
    B --> C[PinyinConverter 逐短句转拼音键]
    C --> D[(MongoDB<br/>training_dataset_item_segments<br/>反向索引 DatasetId+Pinyin)]
    D --> E{有拼音命中?}
    E -->|是| F[按数据 id 去重<br/>首句命中顺序 + itemId 升序<br/>截断 MergedReferenceLimit]
    E -->|否| G[回退: TextSimilarity 字符相似<br/>匹配单条参考（既有逻辑）]
    F --> H[RecordingAudioClient.MergeClipsAsync<br/>POST /api/recordings/audio/merge]
    G --> K[单条片段音频]
    H --> I[拼接 mp3 = speaker_prompt]
    K --> I
    I --> J[情绪参考按 EmotionMode 既有逻辑]
    J --> L[IndexTtsClient /synthesize]
    L --> M[200 audio/wav + X-Speaker-Audio-Key 多值]
```

### 2.2 技术栈

| 层级 | 技术选型 | 说明 |
|------|---------|------|
| 后端 | C# (.NET 10) ASP.NET Core Controllers（training / recording 内改造） | 既有栈 |
| 断句 | 正则 `[，。,.\s]+` 拆分 + Trim + 去空段 | 纯函数，需求固定口径 |
| 拼音 | NuGet 拼音库（本地词典，无运行时外部依赖） | 选型见 §6.1，逐字无声调小写 |
| 反向索引 | MongoDB 新集合 `training_dataset_item_segments` + `(DatasetId, Pinyin)` 索引 | 与既有仓储同模式（驱动 + POCO，无 ORM） |
| 音频合并 | ffmpeg 单进程多输入 `-ss/-t` + `concat` filter → mp3 pipe 输出 | 复用 recording 既有 `AudioClipExtractor` 模式 |
| 契约 | OpenAPI（training / recording / 网关镜像）+ codegen | 契约先行 |

### 2.3 模块划分

| 模块 | 职责 | 关键技术点 |
|------|------|-----------|
| `servers/common/training`：`TrainingDatasetItemSegmentRecord` + `TrainingDatasetItemSegmentRepository` | 候选短句存取与反向索引 | `FindByPinyinAsync(datasetId, pinyins)`、`ReplaceForItemAsync`、`DeleteByDatasetAsync`、`Rebuild` 用 `DeleteMany + BulkWrite` |
| `servers/training` `Synthesis/TextSplitter.cs`（新增） | 断句 | 正则拆分、去空段、保序去重不做（保留重复短句无意义但无害，匹配侧去重） |
| `servers/training` `Synthesis/PinyinConverter.cs`（新增） | 文本 → 拼音键 | 逐字转拼音（无声调小写），非中文字符（字母/数字）转小写保留，空格连接；同一文本 → 同一键 |
| `servers/training` `Synthesis/SegmentSyncService.cs`（新增） | 重建 + 增量同步 | `RebuildAsync(datasetId)` 清空重建；`SyncItemAsync` / `DeleteItemsAsync` / `DeleteDatasetAsync` 挂条目/数据集写路径 |
| `servers/training` `Synthesis/SynthesizeService.cs`（变更） | 音色链路编排 | 拼音命中 → 去重排序 → 合并取音；零命中回退字符相似；`ReferenceMatch` 支持多数据 |
| `servers/training` `Clients/RecordingAudioClient.cs`（变更） | 新增 `MergeClipsAsync` | POST 合并端点，404 → null 跳过语义由编排层处理 |
| `servers/training` `Controllers/TrainingSegmentsController.cs`（新增） | rebuild 接口 | 与 `vectors/rebuild` 同构 |
| `servers/recording` `Audio/AudioClipMerger.cs`（新增） | ffmpeg 合并 | 单进程多输入 concat filter，流式 pipe 输出不落盘 |
| `servers/recording` `Controllers/RecordingController.cs`（变更） | merge 端点 | 复用 `ResolveRecordingFileAsync` 路径穿越防护与录制中判定 |

## 3. API 设计

### 3.1 API 契约

#### 接口 1: 重建候选短句拼音反向索引（training，新增）

`contracts/training.openapi.yaml` 新增，与既有 `vectors/rebuild` 同构：

- **请求**
  - Method: `POST`
  - Path: `/api/training/datasets/{datasetId}/segments/rebuild`
  - Body: 无
- **响应**
  - `200`：`{"rebuilt": <int>}`（重建的候选短句总数）
  - `404`：`{"error": "dataset not found"}`
- 语义：**清空该数据集的反向索引并全量重建**（先 `DeleteMany(datasetId)` 再逐条目断句 + 拼音 + 批量插入）。
- 网关镜像 `contracts/bilibili-crawler.openapi.yaml` 的 `/api/training/**` 同步该端点；`servers/api` 代理透传零改动。

#### 接口 2: 多片段音频合并（recording，新增）

`contracts/recording.openapi.yaml` 新增：

- **请求**
  - Method: `POST`
  - Path: `/api/recordings/audio/merge`
  - Body:
    ```json
    {
      "items": [
        { "roomId": 123, "fileName": "xxx.flv", "startMs": 1000, "endMs": 5000 }
      ]
    }
    ```
  - 约束：`items` 1 ~ `Recording:AudioMergeMaxItems`（默认 16）；每项 `startMs >= 0`、`endMs > startMs`、`endMs - startMs <= AudioClipMaxDurationMs`；各段总时长 `<= Recording:AudioMergeMaxTotalDurationMs`（默认 600000）。
- **响应**
  - `200`：`audio/mpeg`（mp3 单声道 44.1kHz 96kbps，与既有片段端点同规格；无 libmp3lame 时降级 `audio/wav` 同既有口径）+ `Cache-Control: no-store`
  - `400`：`{"error": "items is required" | "too many items" | "invalid time range" | "total duration too long" | "recording in progress"}`
  - `404`：`{"error": "file not found"}`（任一文件不存在/非法文件名）
  - `500`：`{"error": "audio merge failed"}`
- 实现：ffmpeg 单进程——每个 item 一组 `-ss {start} -t {dur} -i {file}` 输入，`-filter_complex "[0:a][1:a]...concat=n=N:v=0:a=1[out]" -map "[out]" -vn -ac 1 -ar 44100 -b:a 96k -f mp3 pipe:1`，直写响应流不落盘；`exitCode != 0` 且零字节抛异常映 500；取消时 kill 回收（同 `AudioClipExtractor`）。
- 说明：该端点为服务间内部能力（training 调用），不挂网关镜像；录制中文件按既有口径 400。

#### 接口 3: synthesize（training，既有，描述与命中头更新）

路径/方法/请求体/状态码**结构性不变**，契约更新点：

| 项 | 改动 |
|----|------|
| `description` | 音色参考口径改为「输入文本断句后按拼音反向索引匹配数据集候选短句，命中数据去重后经 recordings 合并为拼接音源；零拼音命中回退字符相似匹配单条」 |
| `X-Speaker-Audio-Key` | 由单值改为**逗号分隔的多值**（参与拼接的各条目 `AudioKey`，按拼接顺序）；回退路径仍为单值，格式自洽 |
| `502` | 保持 `audio unavailable` / `tts service unavailable`（合并取数失败归入 `audio unavailable`） |

### 3.2 内部接口

| 调用 | 说明 |
|------|------|
| training → MongoDB：`TrainingDatasetItemSegmentRepository` | `FindByPinyinAsync(datasetId, IReadOnlyCollection<string> pinyins)`（`$in` 查询反向索引）、`ReplaceForItemAsync`（删旧插新）、`DeleteByDatasetAsync`、`InsertManyAsync`、`CountByDatasetAsync` |
| training → recording：`IRecordingAudioClient.MergeClipsAsync(IReadOnlyList<AudioClipRequest>)` | 调 `POST /api/recordings/audio/merge`；`AudioClipRequest(roomId, fileName, startMs, endMs)` 复用 `TrainingMapping.ParseAudioUri` 解析产物；404/非 2xx/超时 → `AudioUnavailableException`（编排层按候选失败口径降级） |

## 4. 数据模型

### 4.1 数据库设计

#### 集合: `training_dataset_item_segments`（新增，MongoDB）

| 字段 | 类型 | 说明 |
|------|------|------|
| `Id` | ObjectId | 主键 |
| `DatasetId` | string | 所属数据集 |
| `ItemId` | string | 所属数据 id（去重单位；回查条目取音频/情绪） |
| `AudioKey` | string | 冗余条目 AudioKey（命中头/日志直读，免回查） |
| `SegmentIndex` | int | 短句在条目文本中的序号（确定性排序用） |
| `Text` | string | 候选短句原文（Trim 后非空） |
| `Pinyin` | string | 归一化拼音键（逐字无声调小写、空格连接，反向索引键） |
| `CreatedAt` | DateTime | 写入时间 |

索引（`TrainingMongoSetup.EnsureIndexesAsync` 幂等建立）：

- `(DatasetId, Pinyin)` —— **反向索引**（拼音 → 候选短句），非唯一；
- `(DatasetId, ItemId)` —— 条目级替换/删除；
- `(DatasetId)` —— rebuild 清空与数据集级联删除。

派生关系：`training_dataset_items` 为事实源，segments 为其派生索引，可随时由 rebuild 重建，无迁移风险。

### 4.2 缓存设计

无新增缓存。拼音匹配走 Mongo `$in` 索引检索，单数据集千级短句规模延迟可忽略；不再对全量条目逐一计算编辑距离（回退路径除外）。

## 5. 技术实现方案

### 5.1 核心流程

```mermaid
sequenceDiagram
    participant C as 调用方
    participant T as training(SynthesizeService)
    participant M as MongoDB
    participant R as recording
    participant S as index-tts-api

    C->>T: POST .../synthesize {text}
    T->>T: TextSplitter.Split(text) → 短句列表
    T->>T: PinyinConverter.ToKey(短句)
    T->>M: segments.FindByPinyinAsync(datasetId, pinyins)
    M-->>T: 命中候选短句（含 ItemId/AudioKey）
    alt 有拼音命中
        T->>T: 按 ItemId 去重（首句命中顺序+itemId 升序）<br/>截断 MergedReferenceLimit
        T->>M: 回读去重条目（音频地址/情绪）
        T->>R: POST /api/recordings/audio/merge（多片段）
        R-->>T: 拼接 mp3（失败逐条降级单片段兜底，全失败 502）
    else 零拼音命中
        T->>M: 全量条目 → TextSimilarity 字符相似 top-1（既有）
        T->>R: 单片段取音（既有）
    end
    T->>T: 情绪参考按 EmotionMode（既有）
    T->>S: multipart /synthesize（speaker_prompt=拼接/单条音源）
    S-->>T: WAV（失败 502）
    T-->>C: 200 audio/wav + X-Speaker-Audio-Key（逗号分隔多值）
```

rebuild 流程：

```mermaid
sequenceDiagram
    participant U as 数据维护者
    participant T as training(SegmentSyncService)
    participant M as MongoDB

    U->>T: POST /datasets/{id}/segments/rebuild
    T->>M: segments.DeleteMany(datasetId)（清空反向索引）
    T->>M: items.ListAsync(datasetId) 全量条目
    loop 逐条目
        T->>T: TextSplitter.Split + PinyinConverter.ToKey
    end
    T->>M: segments.InsertManyAsync（批量重建）
    T-->>U: 200 {rebuilt}
```

### 5.2 关键实现点

#### 实现点 1: 断句（`Synthesis/TextSplitter.cs`）

- `Split(text)`：正则 `[，。,.\s]+` 拆分 → `Trim` → 丢弃空段 → 保序返回；
- 纯函数、确定性；输入为空白/纯标点返回空列表（控制器按既有空文本口径 400）。

#### 实现点 2: 拼音键（`Synthesis/PinyinConverter.cs`）

- `ToKey(text)`：逐字符处理——中文字符转拼音（**无声调、小写**），ASCII 字母/数字转小写原样保留，其余字符（标点等）丢弃；结果以单空格连接为索引键；
- 同一文本 → 同一键；同音不同字 → 同一键（如「的/得/地」同归 `de`）；
- 多音字取库默认读音（首选项），不做消歧（本期约束）；
- 新增 NuGet 依赖（选型见 §6.1），本地词典加载、无网络调用。

#### 实现点 3: 候选短句同步（`Synthesis/SegmentSyncService.cs`）

- `RebuildAsync(datasetId)`：`DeleteMany(datasetId)` → 全量条目（过滤空 `Text`）→ 逐条 `Split + ToKey` 生成 segments（`SegmentIndex` 保序）→ `InsertManyAsync`，返回总数；
- 增量：`SyncItemAsync(item)`（条目 import / update 后 `ReplaceForItemAsync` = 删旧插新）、`DeleteItemsAsync(datasetId, itemIds)`（batch-delete 级联）、`DeleteDatasetAsync(datasetId)`（数据集删除级联）；
- 挂接点：`TrainingDatasetItemsController` 导入/更新/批量删除、`TrainingDatasetsController` 删除（与既有 `VectorSyncService` 挂接点一致，best-effort 失败记 Warn，最终以 rebuild 对齐）。

#### 实现点 4: 音色链路重构（`Synthesis/SynthesizeService.cs`）

- 新增 `MatchSpeakerSegmentsAsync`：输入断句 → 拼音键列表 → `FindByPinyinAsync` → 按 **ItemId 去重**（保留首次命中序：输入短句顺序优先，同短句内按 `itemId` 升序）→ 回读条目 → 过滤音频不可用 → 截断 `Synthesize:MergedReferenceLimit`（默认 8）；
- 命中 ≥1 → `MergeClipsAsync` 合并为 `speaker_prompt`；合并整体失败时**逐条单片段兜底**（按序取首个可用单片段），全失败 `AudioUnavailableException`（502）；
- 命中 = 0 → **回退既有字符相似路径**（`RankSpeakerCandidatesAsync` + 单片段扫描），语义不变；
- `dataset` 情绪模式：沿去重后条目顺序取首个带 `Extensions.emotion` 的条目（与既有「沿候选取首个带情绪标注」同口径）；`llm`/`none` 零改动；
- 命中头 `X-Speaker-Audio-Key` 输出逗号分隔多值；Info 日志增 `speakerKeys=N` 计数，不打文本/音频。

#### 实现点 5: 音频合并（`servers/recording/Audio/AudioClipMerger.cs`）

- `MergeAsync(items, output, ct)`：逐 item 复用 `ResolveRecordingFileAsync` 路径穿越防护与「录制中」判定（控制器层完成），构造 ffmpeg 单进程：每输入 `-ss {startMs}ms -t {durMs}ms -i {file}`（输入 seek 前置，小时级文件秒开），`concat=n=N:v=0:a=1` filter，统一 `-ac 1 -ar 44100 -b:a 96k -f mp3 pipe:1`；
- mp3 编码缺失降级 `-f wav`（`Mp3EncoderSupport` 既有探测，接口语义不变）；
- 取消时 kill + 回收；`exitCode != 0` 且零字节抛 `AudioMergeFailedException` 映 500。

### 5.3 不在本期的事项

- 句级音频时间轴切分（按短句估算起止时间）；多音字消歧与拼音相似度（模糊）匹配；
- 全量（跨数据集）rebuild 端点；segments 的 Milvus/embedding 化；
- Web 合成界面、批量合成、合成结果入库/缓存。

## 6. 技术决策

### 6.1 决策列表

| 决策 | 选项 A | 选项 B | 最终选择 | 原因 |
|------|--------|--------|---------|------|
| 反向索引载体 | MongoDB 新集合 + `(DatasetId,Pinyin)` 索引 | Milvus / Redis / 内存字典 | **A** | 精确等值检索无需向量库；与既有仓储/索引模式一致；可持久化、可 rebuild |
| 拼音库 | `TinyPinyin.Net`（本地词典、零运行时依赖） | `NPinyin` / 自研字典 | **A** | 本地词典无网络依赖、API 简洁；实现时以 NuGet 可用性与多音字口径验证为准，不达标则回退 B 中同类库（决策点已隔离在 `PinyinConverter`） |
| 匹配口径 | 拼音键精确匹配（`$in`） | 拼音编辑距离模糊匹配 | **A** | 反向索引天然支撑精确检索、确定性强；模糊匹配留待后续 |
| 零命中行为 | 回退原字符相似单条 | 直接 409 | **A** | 未重建索引/稀疏数据集下行为不劣化 |
| 拼接粒度 | 整条数据音频去重拼接 | 句级时间轴切分拼接 | **A** | 条目无句级时间戳；需求口径即「数据 id 去重后拼合」 |
| 合并实现 | recording 单进程 ffmpeg concat filter | 各片段分别抽取后服务端拼字节 / 临时文件 concat demuxer | **A** | mp3/wav 压缩流不能直接拼字节；单进程不落盘、复用既有抽取参数体系 |
| 合并端点归属 | recordings 新增 POST 接口 | training 侧自行合并 | **A** | 需求指定；音频文件与 ffmpeg 能力均在 recording，避免大文件出网传输 |

### 6.2 依赖与约束

| 类型 | 内容 | 说明 |
|------|------|------|
| 依赖 | 新增 NuGet 拼音库（training） | 本地词典，无外部服务依赖 |
| 依赖 | recording ffmpeg（既有）+ libmp3lame（镜像流水线已断言） | 合并复用 |
| 约束 | 契约先行（三份 OpenAPI + codegen 一致） | 仓库规范 |
| 约束 | TDD、核心逻辑覆盖率 ≥ 80%、单用例 ≤ 5s | `docs/engineering/common-rules.md` |
| 约束 | 断句规则固定 `[，。,.\s]`；拼音键无声调小写 | 需求口径 |
| 影响 | 升级后需对每个存量数据集执行一次 `segments/rebuild` 方启用拼音匹配；未重建自动回退字符相似，行为不劣化 | 运维步骤 |
| 影响 | `X-Speaker-Audio-Key` 变多值（逗号分隔） | 契约说明，调用方仅日志/观测用途 |

## 7. 项目结构

```
contracts/
├── training.openapi.yaml                          # segments/rebuild 新增；synthesize 描述+命中头更新
├── recording.openapi.yaml                         # POST /api/recordings/audio/merge 新增
└── bilibili-crawler.openapi.yaml                  # 网关镜像同步 training 侧端点

servers/common/training/
├── Entities/TrainingDatasetItemSegmentRecord.cs   # 新增：候选短句实体
└── Persistence/TrainingDatasetItemSegmentRepository.cs  # 新增：反向索引仓储（EnsureIndexes 挂载）

servers/training/
├── Synthesis/TextSplitter.cs                      # 新增：断句
├── Synthesis/PinyinConverter.cs                   # 新增：拼音键（新 NuGet 依赖）
├── Synthesis/SegmentSyncService.cs                # 新增：rebuild + 增量同步
├── Synthesis/SynthesizeService.cs                 # 变更：拼音匹配→去重→合并；零命中回退
├── Clients/RecordingAudioClient.cs                # 变更：MergeClipsAsync
├── Controllers/TrainingSegmentsController.cs      # 新增：POST segments/rebuild
├── Controllers/TrainingDatasetItemsController.cs  # 变更：写路径挂增量同步
├── Controllers/TrainingDatasetsController.cs      # 变更：删除级联清 segments
└── Configuration/SynthesizeOptions.cs             # 变更：MergedReferenceLimit（默认 8）

servers/recording/
├── Audio/AudioClipMerger.cs                       # 新增：ffmpeg 单进程 concat 合并
├── Controllers/RecordingController.cs             # 变更：POST /api/recordings/audio/merge
└── Configuration/RecordingOptions.cs              # 变更：AudioMergeMaxItems / AudioMergeMaxTotalDurationMs

apps/web/src/api/                                  # codegen 再生成（勿手改）

tests/Z.Vtuber.Tests/
├── TextSplitterTests.cs                           # 新增
├── PinyinConverterTests.cs                        # 新增
├── SegmentSyncServiceTests.cs                     # 新增
├── SynthesizeServiceTests.cs                      # 拼音命中/去重/回退/合并兜底
├── TrainingSegmentsApiTests.cs                    # 新增：rebuild 控制器
├── RecordingAudioMergeApiTests.cs                 # 新增：merge 控制器
└── AudioClipMergerTests.cs                        # 新增：ffmpeg 参数纯函数
```

## 8. 测试策略

### 8.1 测试覆盖要求

- 核心逻辑（断句、拼音键、segments 仓储/同步、音色链路去重排序与回退、合并参数构造）覆盖率 ≥ 80%；
- TDD：先红后绿；沿用 `MongoDbFixture` + fakes，不打外网、单用例 ≤ 5s。

### 8.2 测试类型

| 类型 | 工具 | 覆盖范围 |
|------|------|---------|
| 纯函数单测 | xUnit | 断句（中英文逗号/句号/空白混合、空段、纯标点）；拼音键（同音字同键、无声调、大小写归一、非中文保留、确定性） |
| 仓储/同步测试 | xUnit + MongoDbFixture（Testcontainers） | rebuild 清空重建、增量同步（import/update/batch-delete/数据集删除级联）、`FindByPinyinAsync` `$in` 检索 |
| 编排测试 | xUnit + 假 recording/假 TTS/假模型 | 多短句多命中拼接、ItemId 去重、确定性排序、截断上限、零命中回退字符相似、合并失败单片段兜底、全失败 502、情绪三模式零回退 |
| 控制器测试 | xUnit（控制器直测） | rebuild 200/404；merge 200/400（空 items/超上限/区间非法/总时长超限）/404/500；synthesize 命中头多值 |
| 集成测试 | Testcontainers MongoDB | rebuild 后 synthesize 拼音命中端到端（假 recording/假 TTS） |

关键用例清单：

- **同音命中**：输入「今天得天气」命中条目短句「今天的天气」（`de` 同键）；
- **断句多命中**：`你好，世界。` 拆 2 句分别命中 2 条数据 → 合并 2 段、`X-Speaker-Audio-Key` 两值；
- **去重**：同一数据被 2 个短句命中 → 仅 1 段参与合并；
- **回退**：segments 为空（未 rebuild）→ 走字符相似单条，结果与现网一致；
- **稳定复现**：同一数据集 + 同一文本 → 同一命中集合与拼接顺序；
- **合并降级**：拼接失败 → 首个可用单片段；全失败 → 502 `audio unavailable`。

## 9. 部署方案

### 9.1 环境规划

| 环境 | 用途 | 部署方式 |
|------|------|---------|
| 内网测试 | 联调/验收 | 常规镜像发布（training / recording 两服务） |
| 生产 | 正式上线 | 常规镜像发布，无新增基础设施 |

### 9.2 配置管理

- `Synthesize:MergedReferenceLimit`（默认 8）：参与拼接的数据条数上限；
- `Recording:AudioMergeMaxItems`（默认 16）、`Recording:AudioMergeMaxTotalDurationMs`（默认 600000）：合并请求护栏；
- 无新增外部服务/连接串；拼音库为进程内依赖。

### 9.3 上线与回滚

- 发布后对每个存量数据集执行 `POST /api/training/datasets/{id}/segments/rebuild` 启用拼音匹配；未重建期间自动回退字符相似匹配，行为不劣化；
- 回滚：还原 training / recording 代码与契约即可；segments 集合为派生数据，可整体丢弃无影响。

## 10. 验收标准

- [ ] 技术方案/契约评审通过
- [ ] 数据集条目按 `[，。,.\s]` 断句生成候选短句并以拼音建反向索引；rebuild 清空并全量重建、返回重建数
- [ ] 条目增删改与数据集删除增量同步 segments（best-effort，rebuild 兜底对齐）
- [ ] synthesize 输入断句 → 拼音匹配 → 数据 id 去重 → recordings 合并拼接音源作为 `speaker_prompt`；命中头多值
- [ ] 零拼音命中回退原字符相似匹配单条；情绪参考三模式零回退
- [ ] recordings `POST /api/recordings/audio/merge` 按请求顺序合并输出单 mp3，护栏与错误码齐备
- [ ] 三份契约与 codegen 一致；`servers/training`、`servers/recording`、`servers/common` AGENTS.md 同步更新
- [ ] 核心逻辑覆盖率 ≥ 80%，TDD 用例与容器 UT 全绿

## 11. 相关文档

- PRD：`docs/product/draft/20261001-training-synthesize-pinyin-match-prd.md`
- 前序提案：`20260929-proposal-training-audio-synthesize.md`、`20261001-proposal-training-synthesize-char-match.md`
- 模块规范：`servers/training/AGENTS.md`、`servers/recording/AGENTS.md`、`servers/common/AGENTS.md`
- 规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`
