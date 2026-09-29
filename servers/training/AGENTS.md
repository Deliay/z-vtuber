# Z.Vtuber.Training

## 技术栈说明

- 语言：C# (.NET 10)
- 框架：ASP.NET Core Web API（Controllers）
- 数据库：MongoDB（`Aspire.MongoDB.Driver` + `MongoDB.Driver`，读写 `training_datasets`/`training_dataset_items`/`training_providers`/`training_models`/`training_emotion_tasks`）
- 分布式锁：Redis（`Aspire.StackExchange.Redis`，情绪识别任务锁 SET NX PX + 续期 + Lua 释放 + fencing）
- 上游依赖：recording-transcript-api（导入数据源，named HttpClient `TranscriptService`）、recording 服务（音频片段，named HttpClient `RecordingAudioClient`，节 `RecordingService`）、外部大模型服务（named HttpClient `AiModel`，OpenAI-like `/chat/completions` 与 Claude-like `/v1/messages`）

## 项目模块示意图

```mermaid
graph LR
    Web[apps/web] -->|/api 经 api 代理| A[servers/api TrainingProxyController]
    A -->|/api/training/**| T[servers/training]
    T --> M[(MongoDB<br/>training_datasets/items/providers/models/emotion_tasks)]
    T -->|"EmotionTaskScanner 抢锁<br/>training:emotion-task-lock:{taskId}"| R[(Redis)]
    T -->|GET api/transcripts/task/{id}| X[servers/recording-transcript-api]
    T -->|"GET api/{roomId}/recordings/{file}/audio"| REC[servers/recording]
    T -->|"chat/completions 或 /v1/messages"| LLM["大模型服务（OpenAI-like / Claude-like）"]
    Common[servers/common Core+Training+Transcript] -.被引用.-> T
```

## 模块结构

AI 训练模块「数据集工具优化」：数据集管理、条目管理、从识别结果导入、Provider/Model 模型设置、
单条「重新识别」与批量「情绪识别」后台任务（周期扫描 + Redis 抢锁消费，写入条目 `Extensions.emotion`）。web 一律经 `servers/api` 代理访问，不直连本服务。

- `Controllers/`：
  - `TrainingDatasetsController`（`[Route("api/training/datasets")]`，接口 1-4）：列表（`type` 必填且本期固定 `audio`，否则 400 `unsupported dataset type`，按 `CreatedAt` 升序）、新建（201；名字 trim 后非空 ≤128，否则 400 `name is required`/`name too long`；同 `(Type,Name)` 重名 409 `dataset name exists`）、重命名（400/404/409/200）、删除（先 `DeleteByDatasetAsync` 级联清条目再删数据集，204/404）
  - `TrainingDatasetItemsController`（`[Route("api/training/datasets/{datasetId}/items")]`，接口 5-8）：列表（`audioType?`/`source?` 过滤，`CreatedAt`、`AudioKey` 升序）、导入、更新、批量删除；错误体统一 `{"error": "..."}`，非法 ObjectId 一律按「不存在」→ 404；存在性判定与前值回读走仓储 `GetByIdAsync` 单读
  - `TrainingProvidersController`（`[Route("api/training/providers")]`，接口 9-14，新增）：Provider 列表/新建/更新/删除（删除**级联删其下 Models**）、`GET {id}/models/discover`（按 `type` 选协议并去重排序；不可达 502 `provider service unavailable`、响应非法 502 `invalid provider response`）、`POST {id}/models/import`（`{imported, skipped}`；模型名 trim 后 > 128 → 400 `model too long`，与手动添加同口径）；name 全局唯一（409 `provider name exists`）、type 白名单 `openai|claude`、endpoint 仅 http/https 且去尾 `/`；**响应永不含 apiKey，仅 `apiKeySet`**
  - `TrainingModelsController`（`[Route("api/training/models")]`，接口 15-18，新增）：列表（`enabled?` 过滤，每项含服务端拼好的 `slug = {providerName}/{model}`）、手动添加（Provider 下拉口径，provider 404/重复 409 `model exists`）、更新（启停/改名 409）、删除
  - `TrainingItemToolsController`（`[Route("api/training/datasets/{datasetId}/items")]`，接口 19-20，新增）：`POST {itemId}/recognize`（单条重新识别：dataset/item/model（含 `Enabled`）校验 → 取音频转 base64 → 识别 prompt → JSON 解析 → 200 `{text}`，**不写库**；400 `model disabled`/`audio uri is empty`/`audio too large`/`model does not support audio input`/`unsupported provider type`；404；502 `audio unavailable`/`model service unavailable`/`invalid model output`）、`POST emotion-tasks`（批量提交 `{itemIds, modelId}` → `{created, skipped}`；model 停用 400 `model disabled`；skipped = 条目已有 PENDING/RUNNING 任务、条目 `text` 为空或 id 非法；**并发重复提交由 `ItemId_1_Active` 唯一索引兜底**，`HasActive` 预检 + `TryInsertActiveAsync` 捕获唯一键冲突计 skipped）
  - `TrainingEmotionTasksController`（`[Route("api/training/emotion-tasks")]`，接口 21-22，新增）：队列查询（`datasetId?`/`status?`/`limit?`（默认 100 最大 500，`CreatedAt` 降序）+ `counts` 分组计数）、`POST {taskId}/retry`（**原子 CAS**：`RetryFailedAsync` filter 含 `Id + Status==FAILED`，仅 FAILED → PENDING，`attempts` 保留、`error` 清空；非 FAILED 400 `task not failed`；同条目已有其他活跃任务 409 `active task exists`；404）
- `Ai/`（新增）：
  - `IChatModelClient`/`IChatModelClientFactory`（`ChatAudio`/`ChatPrompt`；按 `provider.Type` 派发，未知类型抛 `ProviderTypeException`）、`ChatModelClientFactory`（named client `AiModel`；`SocketsHttpHandler.AllowAutoRedirect=false`，避免携带 API key 跟随重定向到其他主机）
  - `OpenAiLikeChatClient`（`POST {endpoint}/chat/completions`、Bearer、`text` + 可选 `input_audio`、`response_format: json_object` 且 400 降级重试一次）
  - `ClaudeLikeChatClient`（`POST {endpoint}/v1/messages`、`x-api-key` + `anthropic-version: 2023-06-01`、仅 `text`；音频直接抛 `ModelAudioNotSupportedException`）
  - `ModelSessionHeaderHandler`（挂在 named client `AiModel` 上的 message handler：**所有**模型服务调用固定携带 `x-opencode-session` 两个同名值 = agent 标识 `z-vtuber-agent/1.0` + 每次请求新生成的随机 guid，与调用场景无关（情绪识别 / 重新识别 / 模型发现一致）；同名多值在线上折叠为 `x-opencode-session: z-vtuber-agent/1.0, <guid>`）
  - `ModelJsonParser`（剥 ```json 围栏 → 首个平衡 `{...}` → 大小写不敏感反序列化 → 数值钳制；`ParseRecognizeText`/`ParseEmotion`；失败抛 `ModelOutputException`）
  - `ModelDiscoveryClient`（`IModelDiscoveryClient`：OpenAI-like `GET {endpoint}/models`、Claude-like `GET {endpoint}/v1/models`）
  - `Prompts/RecognizePromptBuilder`、`Prompts/EmotionPromptBuilder`（硬约束 prompt）
  - `ModelExceptions`（`ProviderTypeException`/`ModelAudioNotSupportedException`/`ModelOutputException`/`InvalidProviderResponseException`/`AudioUnavailableException`/`AudioTooLargeException`）；日志不打 apiKey 与音频内容
- `Emotion/`（新增，语义逐条仿 `recording-transcript-consumer/Tasks/TranscriptTaskLock`）：
  - `EmotionTaskLock`（`IEmotionTaskLockProvider`/`RedisEmotionTaskLockProvider`，key `training:emotion-task-lock:{taskId}`，SET NX PX + 周期续期 + Lua token 比对释放 + `CheckOwnershipAsync` fencing；Redis 抖动 `IsHeldAsync` 保守按持有、`RenewAsync` 捕获异常置 `Lost` 返回 false（不抛出冲垮续期循环），续期循环单轮异常记日志后继续下一轮）
  - `EmotionTaskScanner`（HostedService：`ScanIntervalSeconds` 周期 + 启动抖动、`ListClaimableAsync(ScanBatchSize)`、`MaxConcurrentTasks` 门控、僵尸 RUNNING 无锁接管、**僵尸 RUNNING** `MaxAttempts` 超限置 FAILED（PENDING 含人工 retry 保留 attempts 的任务放行执行）、异常不中断循环；置 RUNNING（Attempts 累加）后把**领取后的记录**交 Runner，使 Runner fencing 的 `Attempts` 与库一致）
  - `EmotionTaskRunner`（单任务编排：置 RUNNING → 读模型（含 `Enabled`，停用 FAILED `model disabled`）/Provider/条目 → 空文本 FAILED → 拼情绪 prompt → 调模型 → 解析 → **CAS 到 DONE**（`UpdateRunningAsync` filter `Id + Status==RUNNING + Attempts`，闭合 TOCTOU，非持有者放弃）→ `SetEmotionAsync` 写 `Extensions.emotion`（条目被删除时改标 FAILED `item not found`）；逐阶段 fencing、失败原因白名单映射不透传 `ex.Message`；`finally` 释放锁）
  - 状态机 `PENDING → RUNNING → DONE | FAILED`，retry 仅 `FAILED → PENDING`
- `Clients/`：`ITranscriptApiClient`/`TranscriptApiClient`（named client `TranscriptService`，`GET api/transcripts/task/{taskId}`；404 → null 表示任务不存在，其余非 20xx 抛 `HttpRequestException` 供控制器映 502）；`IRecordingAudioClient`/`RecordingAudioClient`（取音频片段；`endMs` 空按 `Ai:AudioClipDefaultDurationMs`（默认 300000）补齐——recording 片段端点要求 `endMs` 必填）
- `Contracts/`：`Dtos.cs`（请求/响应 record，JSON 名小驼峰；含 `TrainingProvider`/`TrainingModel`/`EmotionResult`/`TrainingEmotionTask` 等新增）、`TrainingMapping.cs`（`GenerateAudioKey`/`BuildAudioUri`/`IsValidTimeRange`/`ToImportItem` + 新增 `ParseAudioUri`/`BuildModelSlug`/`ToEmotionDocument`/`FromEmotionDocument` 与 `To*Dto` 映射）
- `Configuration/`：`TrainingOptions`（节 `TranscriptService`）、`AiOptions`（节 `Ai`）、`EmotionTaskOptions`（节 `EmotionTask`）、`RecordingServiceOptions`（节 `RecordingService`）
- `Program.cs`：Mongo/Redis（`ConnectionStrings:messaging`）/Options（`ValidateOnStart`）/五仓储/`ITranscriptApiClient`/`IRecordingAudioClient`/`IChatModelClientFactory`/`IModelDiscoveryClient`/`ModelSessionHeaderHandler`（`AiModel` client 的 message handler）/`IEmotionTaskLockProvider`/`EmotionTaskRunner`/`EmotionTaskScanner`(HostedService) 注册，启动 `EnsureIndexesAsync()`、`/health` + `MapControllers()`
- `MongoDbHealthCheck.cs`：与 crawler 同模式
- `Properties/launchSettings.json`：本地 8086
- `Dockerfile`：两阶段构建（纯 aspnet 运行时，`EXPOSE 8080`）
- 镜像流水线：`.github/workflows/build-training-image.yml`（`zero-tools/z-vtuber-training`，paths `servers/training/**` + `servers/common/core/**` + `servers/common/training/**` + `servers/common/transcript/**`）

## API 面

契约：`contracts/training.openapi.yaml`（api 侧镜像见 `contracts/bilibili-crawler.openapi.yaml` 的 `/api/training/**`）。

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/health` | 进程存活 + MongoDB 连通性 |
| GET | `/api/training/datasets` | 数据集列表（`type` 必填，本期固定 `audio`，否则 400；`CreatedAt` 升序） |
| POST | `/api/training/datasets` | 新建（201；400/409 `dataset name exists`） |
| PUT | `/api/training/datasets/{datasetId}` | 重命名（200/400/404/409） |
| DELETE | `/api/training/datasets/{datasetId}` | 删除并**级联清空该数据集全部条目**（204/404） |
| GET | `/api/training/datasets/{datasetId}/items` | 条目列表（query `audioType?`/`source?`；200/404） |
| POST | `/api/training/datasets/{datasetId}/items/import` | 从识别结果导入（`{imported, skipped}`；400/404/502 `transcript service unavailable`） |
| PUT | `/api/training/datasets/{datasetId}/items/{itemId}` | 更新 `audioUri`/`text`/`startMs`/`endMs`（400/404） |
| POST | `/api/training/datasets/{datasetId}/items/batch-delete` | 批量删除（`{deleted}`；404） |
| GET | `/api/training/providers` | Provider 列表（`CreatedAt` 升序；`apiKeySet`，永不含 apiKey） |
| POST | `/api/training/providers` | 新建 Provider（201；400；409 `provider name exists`） |
| PUT | `/api/training/providers/{providerId}` | 更新 Provider（200/400/404/409） |
| DELETE | `/api/training/providers/{providerId}` | 删除 Provider 并**级联删除其下 Models**（204/404） |
| GET | `/api/training/providers/{providerId}/models/discover` | 从 Endpoint 同步模型列表（200/404/502 `provider service unavailable`\|`invalid provider response`） |
| POST | `/api/training/providers/{providerId}/models/import` | 批量导入模型（`{imported, skipped}`；400/404） |
| GET | `/api/training/models` | 模型列表（query `enabled?`；每项含 `slug`） |
| POST | `/api/training/models` | 手动添加模型（201；400/404 `provider not found`/409 `model exists`） |
| PUT | `/api/training/models/{modelId}` | 更新模型（启停/改名；200/400/404/409） |
| DELETE | `/api/training/models/{modelId}` | 删除模型（204/404） |
| POST | `/api/training/datasets/{datasetId}/items/{itemId}/recognize` | 单条重新识别（200 `{text}` **不写库**；400/404/502） |
| POST | `/api/training/datasets/{datasetId}/items/emotion-tasks` | 提交情绪识别任务（200 `{created, skipped}`；400/404 `model disabled`） |
| GET | `/api/training/emotion-tasks` | 情绪任务队列（query `datasetId?`/`status?`/`limit?` → `{items, counts}`） |
| POST | `/api/training/emotion-tasks/{taskId}/retry` | 重试失败任务（200；400 `task not failed`；404；409 `active task exists`） |

对外统一经 `servers/api` 代理（`/api/training/**`，named client `TrainingService`），web 不直连本服务。

## 数据模型

- `training_datasets`：`Name`/`Type`/`CreatedAt`/`UpdatedAt`；索引 `(Type, Name)` **唯一**（重名天然 409）。
- `training_dataset_items`：`DatasetId`/`AudioType`/`Source`/`AudioUri`/`AudioKey`/`StartMs`/`EndMs`(long?) /
  `Text`/`Extensions`(BsonDocument?，**预留**情感识别等加工结果)/时间戳；
  索引 `(DatasetId, AudioKey)` **唯一**、`(DatasetId, AudioType)`、`(DatasetId, Source)`。
- `training_providers`：`Name`(唯一)/`Type`/`Endpoint`/`ApiKey`(明文入库、永不回传)/时间戳；索引 `Name` **唯一**。
- `training_models`：`ProviderId`/`Model`/`Enabled`(默认 true)/时间戳；索引 `(ProviderId, Model)` **唯一**、`ProviderId`；`slug` 为派生字段不落库。
- `training_emotion_tasks`：`DatasetId`/`ItemId`/`AudioKey`/`ModelId`/`ModelSlug`(快照)/`Status`/`Attempts`/`Error`/`Emotion`(BsonDocument?)/时间戳；索引 `(Status, CreatedAt)`、`(DatasetId, CreatedAt)`、`ItemId`、**`ItemId_1_Active`（部分唯一：仅 PENDING/RUNNING，重复提交并发幂等兜底）**；状态迁移带 `Id + Status + Attempts` 条件（fencing 落到写操作），retry 为 `Id + Status==FAILED` 原子 CAS。
- 情绪结果写入条目 `Extensions.emotion`（`SetEmotionAsync` 仅 `$set` 该键、同步刷新条目 `UpdatedAt`；重复识别以最新覆盖），零表结构变更；读取映射到 DTO `emotion`，缺失/格式不符 → null。情绪口径：八维强度 `0~1`、`emotionControlWeight` `0~10`、描述截断 ≤ 200 字，越界/超长均钳制/截断并记 Warn（`ModelJsonParser.ParseEmotion` 与 `TrainingMapping.To/FromEmotionDocument`）。
- 实体与仓储位于 `servers/common/training`（`Z.Vtuber.Common.Training`），索引由 `TrainingMongoSetup.EnsureIndexesAsync` 幂等建立。

## 配置

- `ConnectionStrings:mongo`：MongoDB 连接串
- `ConnectionStrings:messaging`：Redis 连接串（情绪任务锁；**本服务新增依赖**）
- `TranscriptService:BaseUrl`（默认 `http://recording-transcript-api`）：转写服务内网地址
- `RecordingService:BaseUrl`（默认 `http://recording`）：recording 服务内网地址（取音频片段）
- `Ai` 节（`ValidateOnStart`）：`RequestTimeoutSeconds`(120)、`MaxAudioBytes`(8388608)、`AudioClipDefaultDurationMs`(300000)
- `EmotionTask` 节（`ValidateOnStart`）：`ScanIntervalSeconds`(10)、`ScanBatchSize`(10)、`LockTtlSeconds`(30)、`LockRenewIntervalSeconds`(10)、`MaxConcurrentTasks`(2)、`MaxAttempts`(3)
- 健康检查端口：本地 launchSettings 8086

## 部署要点

- 状态在 MongoDB，可多副本部署；情绪任务由 Redis 锁保证同一任务单节点执行，宕机后锁 TTL 过期由他节点接管
- **新增 Redis 依赖**（`ConnectionStrings:messaging`）：裸部署缺连接串则 `ValidateOnStart` 启动校验失败
- 新增 3 个集合与 3 组索引，启动幂等建立，无迁移；回滚时残留集合无害、条目 `Extensions.emotion` 旧版本读取忽略
- 需可访 recording（音频片段）、recording-transcript-api（导入）与外部大模型服务（OpenAI-like / Claude-like；重新识别另需音频输入）
- 镜像：`zero-tools/z-vtuber-training`（`.github/workflows/build-training-image.yml`）

## 开发流程

- TDD 开发（单元与接口测试：`tests/Z.Vtuber.Tests` 的 `Training*`/`EmotionTask*`/`Model*` Tests，Testcontainers MongoDB + Redis；LLM/音频以假 `HttpMessageHandler` 契约测试，不打外网）
- 契约式开发（OpenAPI）：`contracts/training.openapi.yaml`
- 参考：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、
  `docs/engineering/proposals/20260928-proposal-training-dataset-emotion.md`、`docs/engineering/plans/20260928-plan-training-dataset-emotion.md`
