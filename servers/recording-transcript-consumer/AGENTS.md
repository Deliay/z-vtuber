# Z.Vtuber.RecordingTranscript.Consumer

## 技术栈说明

- 语言：C# (.NET 10)
- 框架：ASP.NET Core Web（`Microsoft.NET.Sdk.Web`，挂最小 Kestrel 提供 `/health`，主体为多个 `BackgroundService`）
- 音频转码：ffmpeg 外部进程（`System.Diagnostics.Process`，实时流跟随 / 文件 `-ss` seek，统一输出 16kHz mono s16le pipe）
- ASR：websocket 直连 FunASR realtime（`System.Net.WebSockets.ClientWebSocket`，仅 `sentence_end=true` 成句）
- 数据库：MongoDB（`Aspire.MongoDB.Driver` + `MongoDB.Driver`，读写 `recordings`/`transcript_*`/`recording_status`；segment+position 顺序落库，唯一索引幂等）
- 分布式锁 / 事件：Redis（`Aspire.StackExchange.Redis`，任务锁 SET NX PX + 续期 + Lua 释放 + fencing；pub/sub 订阅 started/finished、发布 segment）
- 依赖服务：recording 服务（`IRecordingServiceClient`，`RecordingService:BaseUrl`）

## 项目模块示意图

```mermaid
graph LR
    REC[servers/recording] -->|"pub recording-started / recording-finished"| R[(Redis)]
    R -->|sub| CONS[servers/recording-transcript-consumer]
    CONS -->|"pub transcript-segment:{taskId}"| R
    CONS -->|读写 tasks/segments/recordings| M[(MongoDB)]
    CONS -->|"实时任务: ffmpeg 拉 /api/{roomId}/stream/audio"| REC
    CONS -->|"离线任务: ffmpeg -ss 拉录播文件"| REC
    CONS -->|websocket| ASR[阿里云 FunASR]
    Common[servers/common Z.Vtuber.Common] -.被引用.-> CONS
```

## 模块结构

录播转写消费服务：订阅开播/结束事件做幂等 enqueue，周期对账补建漏建场次；扫表抢 Redis 锁领取任务，经 ffmpeg 取音频（实时流或录播文件断点 seek）喂 FunASR 流式识别，segment 顺序落库并 Redis pub 供 SSE。

- `Configuration/`：`TranscriptConsumerOptions`（节 `TranscriptConsumer`）、`RecordingServiceOptions`（节 `RecordingService`，拼接 recording 内网 URL）
- `Events/`：`RecordingStartedSubscriber`（订阅 `recording-started`，roomId 启用自动识别时按设置绑定的 `ModelId` 建 mode=live/source=auto 任务）、`RecordingFinishedSubscriber`（订阅 `recording-finished` 补建 mode=file 任务）——均为 `BackgroundService` + `ChannelMessageQueue.ReadAsync` 循环，单条失败记 warning 继续
- `Context/`：`TranscriptContextBuilder`（识别上下文组装：输入 `RecordingRecord`，按优先级汇集主播名 → 最新一天舰长名单 → 窗口内 SC 用户名/粉丝牌 → SC 内容，`HashSet` 去重、按 400 字符上限整行截断；任一类查询失败/为空降级跳过）
- `Enqueue/`：`TranscriptEnqueueService`（幂等建任务：同录播+模型唯一索引，File 优先 `[RAW]` 回退首个；开播/结束/对账/手动共用；重复冲突返回既有 taskId；自动路径从 `TranscriptAutoEnqueueRecord.ModelId` 取模型，未绑定（待完善）或模型不存在/被禁用时记 Warn 不建任务；**建任务成功记 Info 提交信息日志** `transcript task enqueued`（taskId/recordingId/roomId/file/modelId/mode=live|file/source=auto，与手动入队同字段便于检索对账，绝不输出模型 apiKey））
- `Reconcile/`：`TranscriptReconciler`（HostedService，`ReconcileIntervalSeconds` 周期 + 启动抖动：开播中漏建补 live、结束后漏建补 file；仅补「启用之后」的场次，唯一索引幂等）
- `Tasks/`：`TranscriptTaskScanner`（HostedService，周期扫描 PENDING / RUNNING 无锁任务，按 `_id` 升序抢锁接管；并发门控口径为「持锁 RUNNING 数 + 内存预留」，僵尸 RUNNING（无锁）不占额度、不饿死同模型 PENDING，Redis 异常按「持有」保守计数；修复宕机重启后任务卡死）、`TranscriptTaskLock`（`RedisTranscriptTaskLockProvider`：key `transcript:task-lock:{taskId}`，SET NX PX + 续期 Lua + token 比对释放 + `CheckOwnershipAsync` fencing）、`TranscriptTaskRunner`（单任务编排：**仅 Position==0 的 live 任务接实时流**，404 或已有进度则转 file 从 position seek 补尾；文件以运行时最新 `recording.Files` 解析，未就绪时暂缓为 PENDING 重试不携空文件名；上下文 `BuildAsync` 组装完成后 Info `transcript context assembled`（taskId/lines/chars/context 全文，覆盖 ASR 连接失败路径）、逐句 fencing 校验，EOF→DONE、异常→FAILED、finally 释放锁）、`TranscriptSegmentPersister`（顺序写：先 insert segment 再更新 position；`(TaskId, StartTime)` 唯一索引冲突幂等忽略）、`TranscriptSegmentPublisher`（pub `transcript-segment:{taskId}`）
- `Asr/`：`IStreamingAsrService`（`StreamingAsrSegment(Start, End, Text)` + `IAsyncEnumerable` 抽象）、`IStreamingAsrServiceFactory.Create(TranscriptModelRecord model, long offset, IReadOnlyList<string> context)`（模型名/API Key 取自 DB 模型记录，`model` 为实际发给服务商的模型 ID）、`AliFunAsrService`（构造传入 offset 与上下文，websocket 协议 run-task/音频帧/finish-task，仅成句 yield，时间 = 返回时间 + offset；连接/run-task/task-started/finish-task/task-finished/关闭等关键节点 Info 日志、成句进度节流日志；分阶段失败日志（`AsrPhase`：Connecting/Starting/Streaming/Finishing）经统一 `LogFailure` 输出 phase/taskId/model/elapsedMs/bytesSent/segmentsYielded 运行统计，task-failed 额外记录错误码/描述与原始报文（`TruncateForLog` 统一 ≤2KB 截断带 `...(truncated)`，原 `TruncateRaw` 语义通用化改名、调用点同步且口径不变）；**run-task 提交日志打印具体上下文**：`FunASR run-task sent` 含 taskId/model/contextLines/contextChars/`context=` 上下文全文（`\n` 连接，空则记「空上下文」，超长按 `TruncateForLog` 截断），全程不输出模型 apiKey；连接失败/启动超时/异常断开/停滞超时均落 Warn 且异常消息带 `[Phase]` 前缀使落库 `Error` 可读；服务端 `AsrIdleTimeoutSeconds` 无消息即抛 TimeoutException 使任务 FAILED 可重试）、`FunAsrProtocol`（客户端指令 `header.action`＝run-task/finish-task、服务端事件 `header.event`＝task-started/result-generated/task-finished/task-failed；`TryParseTaskFailed` 结构化解析 error_code/message 供日志与异常消息复用，`ParseErrorMessage` 保持行为兼容；`BuildRunTask` 上下文非空时填充 `input.context` 单条 user 消息，为空维持空 `input`）
- `Audio/`：`FfmpegAudioStream`（`FfmpegAudioArgs` 参数构造、`IAudioProcess`/`IAudioProcessFactory`、`IAudioSource`/`IAudioSourceFactory`；实时/live 与 file 双音源统一 s16le pipe，按 1 秒（32000B）分块、有界缓冲形成背压）
- `Services/`：`RecordingServiceClient`（`IRecordingServiceClient`：拼实时音频/录播文件 URL、HEAD 探测实时流可用性）、`TranscriptModelMigration`（启动迁移：为 `Model` 缺失/为空的 `transcript_models` 记录回填 `Model`，取旧配置 `TranscriptConsumer:AsrModel`，缺省 `fun-asr-realtime`；`zvtuber_meta` marker 幂等，已填不覆盖）
- `Program.cs`：注册 Mongo/Redis/Options（`ValidateOnStart`）/仓储/服务/HttpClient（`RecordingService:BaseUrl`）/HostedService，启动幂等索引初始化 + 清理 `transcript_models` 存量 `IsDefault` 字段迁移（`CleanupTranscriptModelIsDefaultAsync`），`/health`
- `MongoDbHealthCheck.cs`：与 crawler 同模式
- `Properties/launchSettings.json`：本地 8085
- `Dockerfile`：两阶段构建（aspnet 运行时 + `apt-get install ffmpeg`，`EXPOSE 8080`）
- 镜像流水线：`.github/workflows/build-recording-transcript-consumer-image.yml`（`zero-tools/z-vtuber-recording-transcript-consumer`）

## API 面

无对外 REST 业务面；仅 `GET /health`（进程存活 + MongoDB 连通性）。所有状态经 Mongo 与 Redis 事件对外暴露（transcript-api 提供查询与 SSE）。

## 配置

- `ConnectionStrings:mongo`：MongoDB 连接串
- `ConnectionStrings:messaging`：Redis 连接串
- `RecordingService:BaseUrl`（节 `RecordingService`，默认 `http://recording/`）：recording 服务内网地址
- `TranscriptConsumer` 节（`ValidateOnStart`）：`ScanIntervalSeconds`(10)、`LockTtlSeconds`(30)、`LockRenewIntervalSeconds`(10)、`AudioBufferSeconds`(60)、`ReconcileIntervalSeconds`(300)、`ScanBatchSize`(50)、`FfmpegPath`(ffmpeg)、`AsrEndpoint`(`wss://dashscope.aliyuncs.com/api-ws/v1/inference/`)、`AsrIdleTimeoutSeconds`(45，服务端停滞超时→任务 FAILED 可重试)。**已删除全局 `AsrModel` 兜底**——实际模型名取自 `transcript_models.Model`（启动迁移会直读旧 `TranscriptConsumer:AsrModel` 配置值用于存量回填后即废弃）
- 健康检查端口：本地 launchSettings 8085

## 部署要点

- runtime 镜像含 ffmpeg（Dockerfile 已装）；可多副本部署——Redis 任务锁保证同一任务单节点执行，宕机后锁 TTL 过期由他节点接管续跑
- FunASR API Key 与模型名均由 `transcript_models` 页面配置注入，本服务按任务的模型记录读取，不经配置项下发
- 升级注意：存量自动识别设置 `ModelId` 为空即进入"待完善"（自动转写暂停），需在设置页补选模型；存量模型经启动迁移自动回填 `Model`

## 开发流程

- TDD 开发（单元测试与集成测试：`tests/Z.Vtuber.Tests`，Testcontainers MongoDB + Redis；ASR 以本地假 websocket server 契约测试，ffmpeg 测试在无 ffmpeg 环境 Skip）
- 参考：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`docs/engineering/proposals/20260916-proposal-recording-transcript.md`
