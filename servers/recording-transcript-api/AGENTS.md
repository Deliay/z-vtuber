# Z.Vtuber.RecordingTranscript.Api

## 技术栈说明

- 语言：C# (.NET 10)
- 框架：ASP.NET Core Web API（Controllers）
- 数据库：MongoDB（`Aspire.MongoDB.Driver` + `MongoDB.Driver`，读 `recordings`/`transcript_*`，写 `transcript_tasks`/`transcript_models`/`transcript_auto_enqueue`）
- 消息通道：Redis pub/sub（`Aspire.StackExchange.Redis`，SSE 订阅 `transcript-segment:{taskId}`）

## 项目模块示意图

```mermaid
graph LR
    Web[apps/web] -->|/api 经 api 代理| TAPI[servers/recording-transcript-api]
    TAPI --> M[(MongoDB)]
    R[(Redis pub/sub)] -->|sub transcript-segment:{taskId}| TAPI
    Common[servers/common Z.Vtuber.Common] -.被引用.-> TAPI
```

## 模块结构

无状态转写查询服务：录播场次归档查询、任务/segments 查询、手动 enqueue、retry、SSE 实时推送、自动识别设置与模型管理。web 一律经 `servers/api` 代理访问，不直连本服务。

- `Controllers/`：`RecordingsArchiveController`（`GET /api/recordings`（以 recording 磁盘数据过滤「所有文件均不存在」的空场次、files 收缩为存在子集、`size` 求和、`anchorName` 补全；recording 不可达降级返回未过滤列表 `size=null`）、`GET /api/recordings/{roomId}/transcripts`（同样过滤空场次）、`GET /api/transcripts/candidate-rooms`（`DistinctRoomsAsync` + anchorName，roomId 升序）、`POST /api/recordings/{roomId}/transcripts/settings`（`enable=true` 时强制 `modelId`，须为存在且启用的模型，否则 400）、`DELETE /api/recordings/{roomId}/transcripts/settings`（物理删除，204/404，不影响已存在任务））、`TranscriptTasksController`（`POST /api/transcripts/enqueue`、`GET /api/transcripts/task`、`GET /api/transcripts/task/{taskId}`、`POST /api/transcripts/task/{taskId}/retry`（仅 FAILED 可重试，置 RUNNING 清 error，409 否则）、`GET /api/transcripts/task/{taskId}/sse`）、`TranscriptSettingsController`（`GET /api/transcripts/settings` 全量列表，关联 `AccountBasicInfoRepository` 补 anchorName、关联 `TranscriptModelRepository` 补 modelName）、`TranscriptModelsController`（`GET/POST /api/transcripts/models`、`PUT /api/transcripts/models/{modelId}`，`model` 必填，响应剔除 apiKey、无 isDefault）
- `Sse/`：`TranscriptSegmentStream`（先 replay 全量 segments，再订阅 Redis `transcript-segment:{taskId}` 转发；以 `start` 去重；15s 心跳注释；任务终态推 `done` 事件并关闭）
- `Clients/`：`RecordingFileStatsClient`（typed HttpClient，节 `RecordingService:BaseUrl`，`IRecordingFileStatsClient.GetFileStatsAsync` 单次请求 `POST /api/recordings/file-stats` 聚合整页 (roomId, files) 的磁盘 exists/size；传输/协议异常抛出由控制器降级）
- `Contracts/`：`Dtos.cs`（请求/响应 record，字段 JSON 名小驼峰）、`TranscriptMapping.cs`（实体 → DTO 映射）
- `Configuration/`：`TranscriptApiOptions`（节 `TranscriptApi`，`MaxPageSize`(200)）
- `Program.cs`：注册 Mongo/Redis/Options/仓储/`TranscriptSegmentStream`/Controllers，启动幂等索引初始化 + 清理 `transcript_models` 存量 `IsDefault` 字段迁移（`CleanupTranscriptModelIsDefaultAsync`），`/health` + `MapControllers()`
- `MongoDbHealthCheck.cs`：与 crawler 同模式
- `Properties/launchSettings.json`：本地 8084
- `Dockerfile`：两阶段构建（纯 aspnet 运行时，`EXPOSE 8080`）
- 镜像流水线：`.github/workflows/build-recording-transcript-api-image.yml`（`zero-tools/z-vtuber-recording-transcript-api`）

## API 面

契约：`contracts/recording-transcript.openapi.yaml`。

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/health` | 进程存活 + MongoDB 连通性 |
| GET | `/api/recordings` | 录播场次归档列表（query `roomId?`/`limit?`/`before?`，StartedAt 倒序；过滤所有文件均不存在的空场次，files 收缩、补 anchorName/size） |
| GET | `/api/recordings/{roomId}/transcripts` | roomId 视角场次及各场次任务（含 `autoEnqueueEnabled`；同样过滤空场次） |
| GET | `/api/transcripts/candidate-rooms` | 候选主播列表（recordings 聚合 distinct roomId/最新 mid + anchorName，roomId 升序） |
| POST | `/api/recordings/{roomId}/transcripts/settings` | 设置自动识别开关（body `{enable, modelId?}`；`enable=true` 时 `modelId` 必填且须为已启用模型，否则 400；upsert） |
| DELETE | `/api/recordings/{roomId}/transcripts/settings` | 删除自动识别设置（物理删除，204/404；不影响已存在任务） |
| POST | `/api/transcripts/enqueue` | 手动 enqueue（body `recordingId`/`modelId`；404 录播或模型不存在/被禁用；409 返回已有 taskId） |
| GET | `/api/transcripts/task` | 任务列表（query `recordingId?`/`roomId?`/`status?`/`limit?`） |
| GET | `/api/transcripts/task/{taskId}` | 任务详情 + 全量 segments |
| POST | `/api/transcripts/task/{taskId}/retry` | 重试 FAILED 任务（409 状态不允许） |
| GET | `/api/transcripts/task/{taskId}/sse` | `text/event-stream` 实时 segments |
| GET | `/api/transcripts/settings` | 自动识别设置全量列表（补 anchorName、modelId/modelName） |
| GET | `/api/transcripts/models` | 模型列表（不回传 apiKey，含 `model` 识别模型 ID） |
| POST | `/api/transcripts/models` | 添加模型（`name/provider/type/apiKey/model` 必填） |
| PUT | `/api/transcripts/models/{modelId}` | 更新启用/禁用、识别模型、最大并发、API Key（可选） |

对外统一经 `servers/api` 代理（`/api/transcripts/**`、`/api/recordings/**`），web 不直连本服务。

## 配置

- `ConnectionStrings:mongo`：MongoDB 连接串
- `ConnectionStrings:messaging`：Redis 连接串
- `RecordingService:BaseUrl`（节 `RecordingService`，默认 `http://recording/`）：recording 服务内网地址，供磁盘权威 file-stats 查询
- `TranscriptApi` 节（`ValidateOnStart`）：`MaxPageSize`(200)
- 健康检查端口：本地 launchSettings 8084

## 部署要点

- 无状态，可多副本部署
- 需可读 `recordings`/`transcript_*` 集合、可写任务/模型/设置集合，并订阅 Redis `transcript-segment:*`
- 与 consumer 共享同一 MongoDB 与 Redis

## 开发流程

- TDD 开发（单元测试与集成测试：`tests/Z.Vtuber.Tests`，Testcontainers MongoDB + Redis）
- 契约式开发（OpenAPI）：`contracts/recording-transcript.openapi.yaml`
- 参考：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`docs/engineering/proposals/20260916-proposal-recording-transcript.md`
