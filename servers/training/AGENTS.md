# Z.Vtuber.Training

## 技术栈说明

- 语言：C# (.NET 10)
- 框架：ASP.NET Core Web API（Controllers）
- 数据库：MongoDB（`Aspire.MongoDB.Driver` + `MongoDB.Driver`，读写 `training_datasets`/`training_dataset_items`）
- 上游依赖：recording-transcript-api（导入数据源，named HttpClient `TranscriptService`）

## 项目模块示意图

```mermaid
graph LR
    Web[apps/web] -->|/api 经 api 代理| A[servers/api TrainingProxyController]
    A -->|/api/training/**| T[servers/training]
    T --> M[(MongoDB training_datasets / training_dataset_items)]
    T -->|GET api/transcripts/task/{id}| X[servers/recording-transcript-api]
    Common[servers/common Z.Vtuber.Common] -.被引用.-> T
```

## 模块结构

AI 训练模块第一期「音频数据集」的独立服务：数据集管理（新建/重命名/删除，重名 409、删除级联清条目）、
条目管理（七字段列表、四字段编辑、批量删除）、从识别结果（Transcript）导入（仅 DONE 任务、尊重任务播放偏移、
按 `(DatasetId, AudioKey)` 唯一索引去重）。web 一律经 `servers/api` 代理访问，不直连本服务。

- `Controllers/`：
  - `TrainingDatasetsController`（`[Route("api/training/datasets")]`，接口 1-4）：列表（`type` 必填且本期固定 `audio`，
    否则 400 `unsupported dataset type`，按 `CreatedAt` 升序）、新建（201；名字 trim 后非空 ≤128，否则 400
    `name is required`/`name too long`；同 `(Type,Name)` 重名 409 `dataset name exists`）、重命名（400/404/409/200）、
    删除（先 `DeleteByDatasetAsync` 级联清条目再删数据集，204/404）
  - `TrainingDatasetItemsController`（`[Route("api/training/datasets/{datasetId}/items")]`，接口 5-8）：
    列表（`audioType?`/`source?` 过滤，`CreatedAt`、`AudioKey` 升序，本期全量不分页）、导入、更新、批量删除；
    错误体统一 `{"error": "..."}`，`datasetId`/`itemId`/`taskId` 非法 ObjectId 一律按「不存在」→ 404
  - 导入（`POST .../items/import`）：数据集 404 → 任务不存在 404 → `Status != DONE` 400 `task not done` →
    `TrainingMapping.ToImportItem` 逐 segment 映射 → `InsertManyIgnoreDuplicatesAsync` → 200 `{imported, skipped}`；
    上游传输/超时异常 → 502 `transcript service unavailable`；Info 日志
    `training dataset import: datasetId={} taskId={} imported={} skipped={}`
  - 更新（`PUT .../items/{itemId}`）：请求 DTO 用非可空 `JsonElement` 区分「缺失（`ValueKind.Undefined`，不改）/
    显式 `null`（`endMs` 置空；`audioUri`/`text`/`startMs` 显式 null → 400 `field cannot be null`）/ 取值」；
    时间校验 `TrainingMapping.IsValidTimeRange`（`startMs >= 0` 且非空 `endMs > startMs`，否则 400 `invalid time range`）
  - 批量删除（`POST .../items/batch-delete`）：部分 id 不存在照常删其余，返回实际 `{deleted}`
- `Clients/`：`ITranscriptApiClient`/`TranscriptApiClient`（named client `TranscriptService`，`GET api/transcripts/task/{taskId}`；
  404 → 返回 null 表示任务不存在，其余非 20xx 抛 `HttpRequestException` 供控制器映 502；
  DTO `TranscriptTaskDetail`/`TranscriptTaskBrief`（含 `Status`/`RoomId`/`File`/`OffsetMs`）与 `TranscriptSegmentBrief`）
- `Contracts/`：`Dtos.cs`（请求/响应 record，JSON 名小驼峰）、`TrainingMapping.cs`
  （`GenerateAudioKey(taskId, rawStart)` → **`transcript:{taskId}:{原始startMs}`**，原始 start = 返回 start − 任务 `OffsetMs`
  （**不含播放偏移**，保证任务偏移变更后重复导入仍命中唯一索引去重）；`BuildAudioUri(roomId, file)` →
  `b/live/{roomId}/recordings/{file}`（file 空 → 空串）；`IsValidTimeRange(startMs, endMs)` 时间校验纯函数；
  `ToImportItem(...)` 落库条目，`StartMs`/`EndMs` 仍为**偏移后展示口径**）
- `Configuration/`：`TrainingOptions`（节 `TranscriptService`，默认 `http://recording-transcript-api`）
- `Program.cs`：Mongo/两个仓储/`ITranscriptApiClient` 注册、启动 `EnsureIndexesAsync()`、`/health` + `MapControllers()`
- `MongoDbHealthCheck.cs`：与 crawler 同模式
- `Properties/launchSettings.json`：本地 8086
- `Dockerfile`：两阶段构建（纯 aspnet 运行时，`EXPOSE 8080`）
- 镜像流水线：`.github/workflows/build-training-image.yml`（`zero-tools/z-vtuber-training`，paths `servers/training/**` + `servers/common/**`）

## API 面

契约：`contracts/training.openapi.yaml`（api 侧镜像见 `contracts/bilibili-crawler.openapi.yaml` 的 `/api/training/**`）。

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/health` | 进程存活 + MongoDB 连通性 |
| GET | `/api/training/datasets` | 数据集列表（`type` 必填，本期固定 `audio`，否则 400 `unsupported dataset type`；`CreatedAt` 升序） |
| POST | `/api/training/datasets` | 新建（201；400 `name is required`/`name too long`/`unsupported dataset type`；409 `dataset name exists`） |
| PUT | `/api/training/datasets/{datasetId}` | 重命名（200/400/404 `dataset not found`/409） |
| DELETE | `/api/training/datasets/{datasetId}` | 删除并**级联清空该数据集全部条目**（204/404） |
| GET | `/api/training/datasets/{datasetId}/items` | 条目列表（query `audioType?`/`source?`；`CreatedAt`、`AudioKey` 升序；200/404） |
| POST | `/api/training/datasets/{datasetId}/items/import` | 从识别结果导入（body `{taskId}` → `{imported, skipped}`；400 `task not done`；404 `dataset/task not found`；502 `transcript service unavailable`） |
| PUT | `/api/training/datasets/{datasetId}/items/{itemId}` | 更新 `audioUri`/`text`/`startMs`/`endMs`（缺失不变、`endMs` 显式 null 置空；400 `invalid time range`/`field cannot be null`；404） |
| POST | `/api/training/datasets/{datasetId}/items/batch-delete` | 批量删除（body `{itemIds}` → `{deleted}`；部分 id 不存在照常删其余；404 dataset） |

对外统一经 `servers/api` 代理（`/api/training/**`，named client `TrainingService`），web 不直连本服务。

## 数据模型

- `training_datasets`：`Name`/`Type`/`CreatedAt`/`UpdatedAt`；索引 `(Type, Name)` **唯一**（重名天然 409）。
- `training_dataset_items`：`DatasetId`/`AudioType`/`Source`/`AudioUri`/`AudioKey`/`StartMs`/`EndMs`(long?) /
  `Text`/`Extensions`(BsonDocument?，**预留**情感识别、二次 ASR 等加工结果)/时间戳；
  索引 `(DatasetId, AudioKey)` **唯一**（导入去重与标识唯一性基础）、`(DatasetId, AudioType)`、`(DatasetId, Source)`。
- 实体与仓储位于 `servers/common/Persistence`（`TrainingDatasetRecord`/`TrainingDatasetItemRecord`、
  `TrainingDatasetRepository`/`TrainingDatasetItemRepository`），索引由 `MongoDbInitializer.EnsureTrainingIndexesAsync` 幂等建立。
- 无存量数据，无迁移。

## 配置

- `ConnectionStrings:mongo`：MongoDB 连接串
- `TranscriptService:BaseUrl`（节 `TranscriptService`，默认 `http://recording-transcript-api`）：转写服务内网地址
- 健康检查端口：本地 launchSettings 8086

## 部署要点

- 无状态（状态在 MongoDB），可多副本部署
- 需可读写 `training_datasets`/`training_dataset_items`，并可访问 recording-transcript-api 内网地址
- 镜像：`zero-tools/z-vtuber-training`（`.github/workflows/build-training-image.yml`）

## 开发流程

- TDD 开发（单元与接口测试：`tests/Z.Vtuber.Tests` 的 `Training*Tests`，Testcontainers MongoDB）
- 契约式开发（OpenAPI）：`contracts/training.openapi.yaml`
- 参考：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、
  `docs/engineering/proposals/20260927-proposal-training-audio-dataset.md`、`docs/engineering/plans/20260927-plan-training-audio-dataset.md`
