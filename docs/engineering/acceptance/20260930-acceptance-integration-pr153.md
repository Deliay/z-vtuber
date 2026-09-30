---
description: integration 分支验收报告——integration 合入 release 前的代码 Review 结果（PR #153，差距 release..integration）：训练-数据集工具优化（上轮已通过）+ 音频生成（数据集语音合成）+ 测试运行性能优化 + 模型服务会话头；发现 2 项 P2（跨数据集误删派生向量、代理超时预算小于阶段预算），结论不通过、打回返工
type: Permanent
---

# Integration 验收报告：integration → release（PR #153）

- **验收日期**: 2026-09-30
- **PR**: https://github.com/Deliay/z-vtuber/pull/153 （integration → release，OPEN / MERGEABLE）
- **Review 范围**: `release..integration` 代码差距（52 commits / 40 non-merge，167 files，+22700/-417）
- **基线**: `release` = `7ee216ecc63a48c86ebdbaf72fbc9cfdd9f00f81`；`integration` = `dc6d73c84afc79b34b9da897faeeaefab7fcac22`（与最新 `main` 一致，`release` 为 `integration` 祖先，故差距即 integration 相对 release 的全部新增）
- **上游说明**: 「修复Review意见」节点无人类打回（reworkCount=0、无 rejectInfo），黑板无 Review 意见，PR #153 无 review 记录与评论；本节点为该差距的首次 AI Review
- **验收结论**: **不通过（打回）**——发现 2 项 P2：①`items/batch-delete` 的 Milvus 向量级联删除不带 `datasetId` 作用域，可跨数据集误删派生向量（数据完整性）；②`servers/api` 代理 `TrainingService` 客户端未设超时（默认 100s）小于各阶段 120s 预算，长调用/批量回填在成功路径上被误报 502。P1 为零，测试运行性能优化与模型服务会话头两部分**零问题**，音频生成主体功能完整、测试覆盖良好，修复 2 项 P2 后可复审

## 一、变更内容概述

本次 `release..integration` 增量由四个特性叠加而成：

| # | 特性 | 关联 PR | 上轮验收状态 |
|---|---|---|---|
| 1 | 训练 - 数据集工具优化（模型设置 / 重新识别 / 情绪识别 / 列表展示优化） | #144/#145/#146 | **已验收通过**（[[20260929-acceptance-integration-pr146]] → [[20260929-acceptance-integration-pr146-r2]]，4 项 P2 全部修复） |
| 2 | 模型服务调用固定携带 `x-opencode-session` 请求头 | #147 | 本报告首次验收 → **零问题** |
| 3 | 训练 - 音频生成（数据集语音合成） | #149/#150 | 本报告首次验收 → **2 项 P2** |
| 4 | 测试运行性能优化 | #151/#152 | 本报告首次验收 → **零问题** |

### 特性 3：音频生成（数据集语音合成）— 本次重点

新增 `servers/training/Vectors/`（`LocalEmbeddingClient` 调内网 Ollama `/api/embeddings` 生成文本向量；`MilvusVectorStore` 两集合 `item_text_embeddings`（dim=`Embedding:Dimensions`，COSINE）/`item_emotion_vectors`（dim=8，L2），FLAT + 稳定 tie-break top-1；`VectorSyncService` Mongo→Milvus 重建/增量同步）、`Synthesis/`（`EmotionVector` 八维 + `SynthesizeService` 编排）、`Clients/IndexTtsClient`（手写 multipart 调 index-tts-api `/synthesize` 返回 `audio/wav`）、`Controllers/TrainingSynthesizeController`（`synthesize` + `vectors/rebuild`）、4 个 `Configuration/` 选项类、`Program.cs` 注册与 Milvus 幂等建集合、既有写路径增量同步钩子、`temperature: 0` 确定性增强、契约 +2 端点与 api 代理镜像 +2 路由、`infra/apphost` 新增 Milvus 资源。

### 特性 4：测试运行性能优化

`tests/Z.Vtuber.Tests/TestWait.cs` 轮询断言工具（替代硬编码 `Task.Delay`）、`tests/Z.Vtuber.TestReport/` trx 报告工具（`--check` 对 >5s 未登记用例非零退出拦截）、`MongoDbFixture` 索引一次化 + `DeleteMany` 清数据、`xunit.runner.json` 并行度 `maxParallelThreads=8`、`TimeoutExceptions.json` 慢用例例外登记、约 40 个测试文件硬等待改轮询断言。硬约束「**不改变任何业务代码的对外行为**」。

### 特性 2：模型服务会话头

`servers/training/Ai/ModelSessionHeaderHandler.cs`（DelegatingHandler，固定 `x-opencode-session: z-vtuber-agent/1.0` + 每请求随机 guid，挂在 named client `AiModel`，覆盖情绪识别/重新识别/模型发现全部模型调用）。

## 二、验证结果（AI Review 独立复跑）

- **构建**: `dotnet build servers/Z.Vtuber.slnx` **0 错误**（32 warnings，其中 1 项为本次新增代码引入的 `CS9113`，见 P4-2；其余为 xUnit/nullable 分析器既有告警与 NuGet `NU1903` 既有漏洞告警）。
- **测试（dotnet，本次差距相关）**: `dotnet test --filter "Training|Emotion|Model|Synthesize|Milvus|Vector|Embedding|IndexTts|RecordingAudio|CollectionNames|TestWait|TestReport"` **541/541 全部通过**（14s，含 Testcontainers Mongo/Redis/Milvus 集成测试；真 Milvus 裸密码 401 回归、tie-break 稳定命中、并发提交原子性、锁续期 fencing 等均覆盖）。
- **测试（web）**: `npm test -- --run` **325/325 全部通过**（19 个文件）。
- **契约一致性**: `npm run codegen` 后 `git diff` 为 **0 diff**，提交的 `schema*.d.ts` 与契约完全一致；双契约（`training.openapi.yaml` / `bilibili-crawler.openapi.yaml` 网关镜像）synthesize + vectors/rebuild 两端点路径/方法/状态码/`X-Speaker-Audio-Key`/`X-Emo-Audio-Key`/`audio/wav`/`TrainingSynthesizeRequest`/`TrainingErrorResponse` 逐条一致。
- **lint**: `npm run lint`（oxlint）**0 warnings 0 errors**（51 files）。
- **全量 dotnet test**: 沿用既往登记的本机 Testcontainers 环境问题与 6 项预先存在失败（`RecordingFileNameTests`×2、`FfmpegProcessBuilderTests`×2、`WeiboTimelineJobTests`×2，已独立复现并登记于 [[20260930-test-perf-optimization-known-failures]]，与本差距无关），本轮以聚焦用例集复跑确认，不阻塞验收。

## 三、发现的问题

### P2-1 - `items/batch-delete` 的 Milvus 向量级联删除不带 `datasetId` 作用域，可跨数据集误删派生向量（数据完整性）

- **位置**：`servers/training/Controllers/TrainingDatasetItemsController.cs:234-240`、`servers/training/Vectors/MilvusVectorStore.cs`（`DeleteItemAsync`，filter 仅 `id == "{itemId}"`）、`servers/training/Vectors/IVectorStore.cs:37`
- **问题**：Mongo 删除按数据集作用域（`items.DeleteManyAsync(id, itemIds)` 只删本数据集的行），但向量级联删除对**解析出的全部 `itemIds`** 无条件执行，且 Milvus 的删除 filter **完全忽略 `datasetId` 形参**（`DeleteItemVectorsAsync(ObjectId datasetId, ObjectId itemId)` 的 `datasetId` 是死参数，接口注释却声明「删除某个条目在两个集合中的向量」并暗示数据集范围）。二者叠加构成跨数据集数据破坏路径：对数据集 A 调 `POST /api/training/datasets/{A}/items/batch-delete`，body 传入属于数据集 B 的条目 id，Mongo 侧删除 0 行，Milvus 侧却把 B 的文本/情感向量行删掉。B 的 `synthesize` 随即**静默丢失** speaker/emo 候选（降级到次优或 409 `no speaker reference`），直到有人对从未被调用方碰过的数据集 B 执行 `vectors/rebuild` 才能恢复。
- **证据**：
  ```csharp
  // TrainingDatasetItemsController.cs:234-240 —— Mongo 按数据集删，向量按 id 全删
  var deleted = itemIds.Count == 0 ? 0 : await items.DeleteManyAsync(id, itemIds, ct);
  if (itemIds.Count > 0 && vectors is not null)
      await vectors.DeleteItemsAsync(id, itemIds, ct);   // 未与 deleted 对齐，id 可属其他数据集
  // MilvusVectorStore.DeleteItemAsync —— datasetId 未参与过滤
  filter = $"id == \"{itemId}\"",
  ```
- **建议**：删除 filter 改为 `datasetId == "{datasetId}" && id == "{itemId}"`（或在 `DeleteItemAsync` 内联 `datasetId` 条件），并把级联范围收敛为**实际被 Mongo 删除的条目**（`DeleteManyAsync` 返回被删 id 集合后据此级联）；补一条「跨数据集 id 传入 batch-delete 不得删除他数据集向量」的回归测试。接口 `datasetId` 形参若保留则必须生效，否则删除该形参避免误导。

### P2-2 - api 代理 `TrainingService` 客户端未设超时（默认 100s）小于各阶段 120s 预算，长调用在成功路径上被误报 502

- **位置**：`servers/api/Program.cs:61-64`（`TrainingClientName` named client 未设 `client.Timeout`）；对照 `servers/training/Configuration/TtsOptions.cs:24`、`TrainingOptions.cs:21`、`EmbeddingOptions.cs:30`（三者 `RequestTimeoutSeconds` 均为 **120**）与 `servers/training/Vectors/VectorSyncService.cs`（`RebuildAsync` 逐条串行调 Ollama embedding，**在 HTTP 请求内**）、`TrainingDatasetItemsController.cs:105-112`（导入后同步同理）
- **问题**：项目规定的访问路径是「web 一律经 `servers/api` 代理」，而 `TrainingProxyController.ForwardAsync` 把上游超时映射为 `502 {"error":"training service unavailable"}`。当前 `TrainingService` 客户端未设 `Timeout`，落到 `HttpClient` 默认 **100 秒**，小于 training 侧任一阶段的 120 秒预算。结果是：一次慢 TTS 合成（TTS 明确允许 120s）或一次条目数上百的 `vectors/rebuild`（逐条串行 embedding）会**在 training 侧正常完成工作的同时**让代理先超时，调用方收到 502 并可能反复重试/重复 rebuild，既误导排障又放大上游负载。`vectors/rebuild` 与 `items/import` 这类新长耗时端点尤其突出。
- **证据**：
  ```csharp
  // servers/api/Program.cs:61-64 —— 无 Timeout 设置 → 默认 100s
  builder.Services.AddHttpClient(TrainingProxyController.TrainingClientName, client =>
  {
      client.BaseAddress = new Uri(builder.Configuration["TrainingService:BaseUrl"] ?? "http://training");
  });
  // TtsOptions.cs:24 / TrainingOptions.cs:21 / EmbeddingOptions.cs:30
  public int RequestTimeoutSeconds { get; set; } = 120;
  ```
- **建议**：为 `TrainingService` named client 显式设置高于阶段预算的超时（如 180s，或至少 > 120s），并在契约/AGENTS.md 注明代理超时口径；`vectors/rebuild` 与 `items/import` 的向量同步更稳妥的做法是改为异步（202 + 任务查询）或请求外执行，避免任何一层超时预算与数据量线性相关的端点共存。补一条「代理超时 ≥ 各阶段超时」的一致性断言（配置或测试）。

### P3 - 建议（不阻塞，可随后续小 PR 跟进）

1. **`TryBuildEmotionReferenceAsync` 的 `catch (Exception)` 吞掉取消**（`servers/training/Synthesis/SynthesizeService.cs:120-126`）：客户端断连/`ct` 取消时 `OperationCanceledException` 被吞，流程继续进入 `tts.SynthesizeAsync(..., ct)`；应 `catch (OperationCanceledException) when (ct.IsCancellationRequested) throw;`。
2. **导入增量同步重同步整个 source 批次**（`TrainingDatasetItemsController.cs:105-112`）：`items.ListAsync(id, source: taskId)` 返回该 source 的**全部**条目，部分失败重导会把已导入条目逐条重新 embedding（串行、请求内），既是浪费也直接放大 P2-2 的超时风险；应只同步本次新增记录。
3. **`DeleteItemVectorsAsync(datasetId, itemId)` 接口/实现漂移**（`IVectorStore.cs:37` vs `MilvusVectorStore.cs` `DeleteItemAsync`）：`datasetId` 声明了作用域语义却未参与过滤，签名即误导；修复 P2-1 后应同步校正注释或强制作用域。

### P4 - 提醒

- **`MilvusOptions.SearchLimit` 是死配置**（`Configuration/MilvusOptions.cs`）：AGENTS.md 记为「文本向量检索的候选数（top-k）」，但实际 top-k 走 `SynthesizeOptions.ReferenceCandidateLimit`（`SynthesizeService.cs:50,105`），调 `Milvus:SearchLimit` 无效；应二选一。
- **`SynthesizeService` 构造参数 `ttsOptions` 未被读取**（`Synthesis/SynthesizeService.cs:35`），构建告警 `CS9113`；要么使用（如日志/超时展示）要么移除。
- **`RecordingAudioClient` 先全量缓冲再做 `MaxAudioBytes` 检查**（`Clients/RecordingAudioClient.cs:86-101`）：应 Content-Length 预检 + 流式限长（上轮 P3-9 已登记，本次差距未处理，维持登记）。
- **`ParseAudioUri` 接受 `.` / `..` 文件段**（`Contracts/TrainingMapping.cs:92-99`）：仅拒绝 `/`，`Uri.EscapeDataString("..")` 原样返回，相对路径会被 `Uri` 归一化出非预期路由（Base 地址固定，无跨主机 SSRF）；建议一并拒绝 `.`/`..` 段。
- **命中头 `X-Speaker-Audio-Key`/`X-Emo-Audio-Key` 回显 Milvus 输出字段而非条目记录**（`SynthesizeService.cs:77` → `TrainingSynthesizeController.cs:78,83`）：Milvus 中出现畸形值时响应头赋值抛 `ArgumentException`，会在 TTS 已成功后落成 500；建议改用 `speaker.Item.AudioKey`。
- **确定性 tie-break 仅在 top-k=16 窗口内稳定**（`MilvusVectorStore.cs:126,172-175`）：超过 16 条同分时进入窗口的候选由 Milvus 决定，属唯一残留确定性缺口（另有 Plan 已登记的模型自身非确定性）。
- **畸形 JSON body 返回框架 ProblemDetails 而非 `{"error":"..."}`**（`TrainingSynthesizeController.cs:43-45`）：与既有 22 端点同款既有口径，严格契约读者可能异议。
- **`TestWait.cs:39` 用 `Assert.True(false, msg)` 失败**（触发 xUnit2020 告警）：建议改 `Assert.Fail(msg)`。
- **`TimeoutExceptions.json` 共 10 条例外**：逐条理由与 `since` 齐备，均为「真实时间流逝」类（锁 TTL 到期点 / 空闲关闭观察窗 / 心跳累计），口径合理；后续新增需继续随评审最小化。

## 四、零问题部分（本次重点核验结论）

- **特性 4 测试运行性能优化 —— 干净**：硬约束「不改变任何业务代码的对外行为」**成立**——该工作流的提交（`520c4b0..08abaeb`）在 `servers/`/`apps/`/`infra/`/`contracts/` 下仅触及各 `AGENTS.md` 与 `servers/Z.Vtuber.slnx`（新增 TestReport 工程），**零业务行为改动**；测试完整性**未被削弱**——`git diff` 中 `Fact(Skip` 新增数为 **0**，无删除/放宽断言，`TestWait.UntilAsync` 到点即失败且失败信息带条件描述，`NeverAsync` 逐次断言条件不成立（验证定时器/锁不提前触发），未发现「大超时轮询掩盖竞态」；`MongoDbFixture` 索引一次化 + `DeleteMany` 清数据的隔离性可接受并有 `MongoDbFixtureTests` 覆盖；`TestReport` 的 `--check` 护栏与例外表匹配口径正确；并行度 8 未发现共享静态状态/端口/集合导致的不安全并行。
- **特性 2 模型服务会话头 —— 干净**：`ModelSessionHeaderHandler` 为无状态 DelegatingHandler，固定 agent 标识 + 每请求随机 guid，挂 `AiModel` named client 覆盖全部模型调用，无日志泄露、无副作用。
- **特性 1 训练-数据集工具优化 —— 维持上轮通过结论**：4 项 P2 已于 `0e2115b` 修复并经 [[20260929-acceptance-integration-pr146-r2]] 复核通过；残留 2 项 P3（嵌套文件名条目解析、音频全量读入）与少量 P4 维持登记，不阻塞。本次未发现新增问题。
- **音频生成其余部分 —— 完整且符合 Plan**：错误矩阵（400 `text is required`/`invalid lang`/`model disabled`、404 `dataset not found`/`model not found`、409 `no speaker reference`、502 ×4）与 `{"error":"..."}` 错误体、校验顺序、非法 ObjectId→404 均逐条落实；Plan §1.0 的 7 项偏差与返工修订 ①–⑦ 及第 2 轮收口（`milvus.Resource.Token = root:{apiKey}`、`MilvusFixture` 固定 `milvusdb/milvus:v2.5.27`、真 Milvus 裸密码 401 回归、`DeleteItemVectorsAsync` 级联钩子 + null 侧删行、`Tts:DefaultLang` 启动白名单校验、三客户端 `IHttpClientFactory` 每次取用、检索失败懒触发 `EnsureCollectionsAsync`、`consistencyLevel: Strong`）**全部落地**；确定性口径 FLAT + 稳定 tie-break（相似度降序、itemId 升序）与 `temperature: 0`（`OpenAiLikeChatClient`/`ClaudeLikeChatClient` 均已加）成立；`AllowAutoRedirect=false` 落在两个持 token 的客户端上；新日志仅打 id/key/lang/elapsed，不打文本/音频/apiKey；recording、recording-transcript-api/consumer 零引用新增类型。

## 五、结论

本次 `release..integration` 差距四个特性中，**测试运行性能优化**与**模型服务会话头**零问题，**训练-数据集工具优化**维持上轮通过结论，**音频生成（数据集语音合成）**功能主体完整、契约三方一致、codegen 0 diff、测试 541/541 全绿、Plan 偏差与返工修订逐条落地。但存在 **2 项 P2 必须修复**：

1. **跨数据集误删派生向量**（数据完整性）——`items/batch-delete` 向量级联删除不带 `datasetId` 作用域，可静默删除他数据集的 Milvus 向量；
2. **代理超时预算倒挂**——`servers/api` 代理默认 100s 小于 training 各阶段 120s 预算，长调用/批量回填在成功路径上被误报 502。

按节点规则**判定不通过、打回返工**，要求逐项修复并补对应测试（跨数据集删除回归、代理超时一致性断言）后重新提交验收。P3/P4 列表建议随返工一并处理或排期独立跟进。
