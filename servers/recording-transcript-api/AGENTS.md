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
    Common[servers/common Core+Live+Recording+Transcript] -.被引用.-> TAPI
```

## 模块结构

无状态转写查询服务：录播场次归档查询、任务/segments 查询、手动 enqueue、retry、SSE 实时推送、自动识别设置与模型管理。web 一律经 `servers/api` 代理访问，不直连本服务。

- `Controllers/`：`RecordingsArchiveController`（`GET /api/recordings`（以 recording 磁盘数据过滤「所有文件均不存在」的空场次、files 收缩为存在子集、`size` 求和、`anchorName` 补全；recording 不可达降级返回未过滤列表 `size=null`）、`GET /api/recordings/{roomId}/transcripts`（同样过滤空场次）、`GET /api/transcripts/candidate-rooms`（`DistinctRoomsAsync` + anchorName，roomId 升序）、`POST /api/recordings/{roomId}/transcripts/settings`（`enable=true` 时强制 `modelId`，须为存在且启用的模型，否则 400）、`DELETE /api/recordings/{roomId}/transcripts/settings`（物理删除，204/404，不影响已存在任务））、`TranscriptTasksController`（`POST /api/transcripts/enqueue`（建任务成功记 Info 提交信息日志：taskId/recordingId/roomId/file/modelId/mode/source=manual，409 幂等命中记 Debug）、`GET /api/transcripts/task`、`GET /api/transcripts/task/{taskId}`（segments 按任务 `OffsetMs` 叠加后返回）、`PUT /api/transcripts/task/{taskId}/offset`（任务播放偏移：body `{offsetMs}`，缺失或超出 ±86400000 返回 400 `offsetMs out of range`，taskId 非法或任务不存在 404，成功重读任务返回含 `offsetMs` 的任务并记 Info `transcript task offset updated`）、`POST /api/transcripts/task/{taskId}/retry`（仅 FAILED 可重试，置 RUNNING 清 error，409 否则）、`GET /api/transcripts/task/{taskId}/sse`）、`TranscriptSubtitleController`（`GET /api/transcripts/task/{taskId}/subtitle`：`format` 枚举校验默认 srt、非 srt/ass 400、taskId 非法或任务不存在 404；`ListByTaskAsync` 升序取片段 → 按任务 `OffsetMs` 叠加展示时间（负值由 `SubtitleRenderer` 钳 0）→ `SubtitleRenderer` 渲染 → UTF-8 带 BOM 下载，框架生成 `Content-Disposition` 含 `filename*`；任意状态任务可下载、无片段为合法空字幕）、`TranscriptSettingsController`（`GET /api/transcripts/settings` 全量列表，关联 `AccountBasicInfoRepository` 补 anchorName、关联 `TranscriptModelRepository` 补 modelName）、`TranscriptModelsController`（`GET/POST /api/transcripts/models`、`PUT /api/transcripts/models/{modelId}`，`model` 必填，响应剔除 apiKey、无 isDefault）
- `Subtitles/`：`SubtitleRenderer`（SRT/ASS 纯函数渲染，格式即契约：SRT 序号从 1 起 + `HH:MM:SS,mmm` 小时两位起自然进位、文本换行原样保留、`end<=start` 异常片段按原值渲染；ASS v4+ 固定文件头（ScriptType v4.00+ / PlayResX 1920 / PlayResY 1080 / WrapStyle 2 / ScaledBorderAndShadow yes）+ `Style: Default`（Microsoft YaHei 64、白字黑边、Alignment 2、MarginV 60）+ `Dialogue` 时间 `H:MM:SS.cc` 厘秒向下取整，`{`/`}` 转义 `\{`/`\}`、换行转 `\N`；统一 UTF-8 with BOM 防播放器 GBK 判定乱码）、`SubtitleFileName`（`Build(title, modelName, taskId, ext)` → `{sanitize(标题)}-{sanitize(模型名)}.{ext}`，仅保留中英文/数字/`-`/`_` 其余折叠 `_`、无 CR/LF 注入，空段兜底 `transcript-{taskId 前 8 位}`）
- `Sse/`：`TranscriptSegmentStream`（先 replay 全量 segments，再订阅 Redis `transcript-segment:{taskId}` 转发；以 `start` 去重；15s 心跳注释；任务终态推 `done` 事件并关闭；建立流时读一次任务 `OffsetMs`，replay 与实时事件写出时统一 +offset，去重键保持原始 `StartTime`）
- `Clients/`：`RecordingFileStatsClient`（typed HttpClient，节 `RecordingService:BaseUrl`，`IRecordingFileStatsClient.GetFileStatsAsync` 单次请求 `POST /api/recordings/file-stats` 聚合整页 (roomId, files) 的磁盘 exists/size；传输/协议异常抛出由控制器降级）
- `Contracts/`：`Dtos.cs`（请求/响应 record，字段 JSON 名小驼峰；`TranscriptTaskDto.offsetMs` 与 `UpdateTranscriptTaskOffsetRequest`）、`TranscriptMapping.cs`（实体 → DTO 映射；`ToSegmentDto` 叠加任务播放偏移且负值不钳制以保持 `start` 单射、`ToTaskDto` 输出 `offsetMs`）
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
| GET | `/api/transcripts/task/{taskId}` | 任务详情 + 全量 segments（segments = 落库时间 + 任务 `offsetMs`） |
| PUT | `/api/transcripts/task/{taskId}/offset` | 设置任务播放偏移（body `{offsetMs}` 毫秒可正可负，范围 ±86400000 超出 400 `offsetMs out of range`、404 任务不存在或 Id 非法；200 返回含 `offsetMs` 的任务；仅写 `transcript_tasks.OffsetMs`，详情/SSE/字幕三出口即时生效） |
| POST | `/api/transcripts/task/{taskId}/retry` | 重试 FAILED 任务（409 状态不允许） |
| GET | `/api/transcripts/task/{taskId}/sse` | `text/event-stream` 实时 segments（replay 与实时事件均叠加任务 `offsetMs`） |
| GET | `/api/transcripts/task/{taskId}/subtitle` | 下载字幕文件（query `format` = `srt` 默认 \| `ass`，其他 400 `unsupported format`；200 `application/x-subrip` / `text/x-ass`（均 `charset=utf-8`）+ `Content-Disposition`（`attachment` + `filename`/`filename*`）+ UTF-8 BOM 正文；404 任务不存在/Id 非法；任意状态可下载，无片段为合法空字幕；条目时间 = 落库时间 + 任务 `offsetMs`，负值钳 0） |
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

- 跑测口径：遵循 `docs/engineering/common-rules.md`「测试性能基线与超时护栏」小节（单用例 ≤5s；报告工具 `tests/Z.Vtuber.TestReport --check` 护栏）
- TDD 开发（单元测试与集成测试：`tests/Z.Vtuber.Tests`，Testcontainers MongoDB + Redis）
- 契约式开发（OpenAPI）：`contracts/recording-transcript.openapi.yaml`
- 参考：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`docs/engineering/proposals/20260916-proposal-recording-transcript.md`
