---
description: 直播录播语音转写实现 Plan——基于 PRD v1.0 与技术提案（feat/recording-transcript @69ba69a）的实施阶段划分与任务分解；契约先行（recording-transcript.openapi.yaml 新建 + bilibili-crawler 契约追加转写代理端点），common 演进（5 新集合实体/仓储/索引 + recording-started/finished 事件契约），recording 服务改造（recordings 场次归档 + 开始/结束事件 + 存量清洗），新建 transcript-consumer（事件订阅 enqueue + 对账补建 + 扫表抢锁续期 fencing + 双音源 ffmpeg pipe + FunASR 流式识别断点续跑）与 transcript-api（任务/设置/模型/SSE），api 代理，web Transcripts 页（EventSource），Mongo standalone（非事务顺序写）与 Aspire/Dockerfile/CI 工程接入
type: Permanent
---

# 直播录播语音转写 - 实现 Plan

**功能名称**: 直播录播语音转写（Transcript）
**关联 PRD**: `docs/product/draft/20260916-recording-transcript-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20260916-proposal-recording-transcript.md`
**Plan 版本**: v1.0
**创建日期**: 2026-09-16
**作者**: Workflow Agent（代起草）
**feat-branch**: `feat/recording-transcript`（需求总分支，已推送 origin，HEAD `69ba69a`；需求总 PR：<https://github.com/Deliay/z-vtuber/pull/105>）

## 1. 概述

本 Plan 基于 PRD v1.0 与技术提案（含两次打回修订：API 路由按 roomId 相关性分组、追加 recording-started 事件支持开播即实时转写）编写，指导「直播录播语音转写」的编码实施。改动分八块：① 契约——新建 `contracts/recording-transcript.openapi.yaml`，`contracts/bilibili-crawler.openapi.yaml` 追加转写代理端点（先于实现）；② common 演进——`recordings`/`transcript_tasks`/`transcript_segments`/`transcript_models`/`transcript_auto_enqueue` 五集合的实体/仓储/索引 + `RecordingEventChannels` 事件契约（started/finished/segment）；③ `servers/recording` 改造——`RecordingSession` 开始归档 + 发布 recording-started、结束回写 EndedAt + 发布 recording-finished、存量录播一次性幂等清洗；④ 新建 `servers/recording-transcript-consumer`——双事件订阅 enqueue、5min 对账补建、10s 扫表抢锁（Redis 锁续期/fencing）、双音源 ffmpeg pipe（live 接实时音频流 / file `-ss` 断点 seek）、`AliFunAsrService` websocket 成句 yield、segment+position 顺序落库 + Redis pub；⑤ 新建 `servers/recording-transcript-api`——enqueue/任务列表/详情/retry/SSE/设置/模型 API；⑥ api 代理 `/api/transcripts/**` 与 `/api/recordings/**`；⑦ web 新增 Transcripts 导航（任务列表 Tab SSE 实时 segments / 设置 Tab 识别设置+模型设置）；⑧ 工程接入——Mongo standalone（非事务顺序写 + 唯一索引幂等）、slnx/Aspire/Dockerfile/CI/codegen。实施全程遵循 `docs/engineering/common-rules.md`（TDD、契约式开发、核心逻辑覆盖率 ≥ 80%）、`backend-rules.md`、`frontend-rules.md` 及各子项目 AGENTS.md。

### 1.0 上游核实结论（代码现状复查）

对需求总分支 `feat/recording-transcript`（HEAD `69ba69a`）代码核实，提案技术路径**整体可行**，现状细节与须注意的偏差如下，处理方式在对应任务中给出：

- **Mongo 保持 standalone，非事务顺序写 + 唯一索引幂等（下游决策）**：`MongoDbFixture` 为 standalone `mongo:7.0`（`tests/Z.Vtuber.Tests/MongoDbFixture.cs:11-13`，无 `--replSet`）；AppHost mongo 资源同样为 standalone（`infra/apphost/AppHost.cs:3-6`）。**处理**：segment 落库与 position 推进改为非事务顺序写（先 insert segment 再更新 position），靠 `(TaskId, StartTime)` 唯一索引 + 幂等重跑兜底；**不改动** `MongoDbFixture`/AppHost 为 replica set，无事务依赖（任务 1.1/2.10 按此实现，任务 3.2/3.3 仅落实 standalone 编排与部署说明）。
- **`mid` 不在 `RecordingSession` 作用域**：`RecordingSession.RunAsync(roomId, anchorName, room, cookieId, cookie, ct)`（`servers/recording/Recording/RecordingSession.cs:39-45`）的 `LiveRoomInfo` 仅 `(RoomId, Title, IsLive)`（`Live/LiveModels.cs:4`）；mid 仅在 `LiveStatusPoller` 层已知（`Live/LiveStatusPoller.cs:87/102`）。**处理**：`RunAsync` 增加 `mid` 参数由 Poller 透传（任务 2.2），供归档与两个事件载荷使用。
- **结束分支的文件清单**：正常结束 `exitCode==0` 分支在 `RecordingSession.cs:229-232`；`files` 列表（RAW 必有、AV1 条件性）在开始处已渲染完毕（:117-129）于作用域内可用；`ReportNotLiveAsync` 落库时 `Files=[]`（`RecordingStatusReporter.cs:18-26`）不影响归档——`recordings.Files` 在开始 insert 时已写入。**处理**：开始时 insert 归档 + 发布 started；exitCode==0 分支回写 `EndedAt` 后发布 finished（任务 2.3）；fencingLost（:217-223）与停机（:224-228）分支不发布。
- **文件名时间为 12 小时制**：`RecordingFileName.Render` 模板 `[{encode}][{roomId}][{anchor}]{yyyy-MM-dd-hh-mm-ss}-{title}.flv`（`Recording/RecordingFileName.cs:23`，`hh` 为 12 小时制）。**处理**：存量清洗以**文件创建时间**为匹配基准（提案既定），文件名仅解析场次名辅助，不按文件名时间匹配，规避 12 小时制歧义（任务 2.4，风险见 §6）。
- **场次匹配数据源已存在**：`LiveSessionRecord`（`servers/common/Persistence/Entities/LiveSessionRecord.cs:6-46`，集合 `bilibili_live_session`，含 `RoomId/Title/StartDate/StopDate`）适合按 RoomId + 时间窗匹配；`LiveSessionRepository` 无「按 RoomId+时间查场次」现成方法，需新增（任务 2.1）。`bilibili_live_status`（`LiveStatusRecord`）作兜底。
- **新集合登记三处**：`MongoCollectionNames`（`MongoDbInitializer.cs:7-27`，现 18 集合）、`MongoDbInitializer.EnsureIndexesAsync`（:33-53）、`MongoDbFixture.ResetAsync` drop 名单（:31-51）。迁移登记用 `zvtuber_meta` marker 模式（:953-1004 先例）；清洗迁移需访问录播存储目录，只能随 **recording 服务**启动执行（common 不感知 StorageDir）。
- **事件订阅模板**：`CookieInvalidEventSubscriber`（`servers/notifier/Events/CookieInvalidEventSubscriber.cs:9-69`，BackgroundService + `ChannelMessageQueue.ReadAsync` 循环）直接复用形态；common `Live/` 目录现无任何 recording 事件频道（新增 `RecordingEventChannels`）。
- **锁范本**：`RecordingLock`（`servers/recording/Recording/RecordingLock.cs`，SET NX PX + `pexpire` 续期 Lua + token 比对释放 Lua + `CheckOwnershipAsync` fencing）语义与任务锁一致，`TranscriptTaskLock` 仿写（key `transcript:task-lock:{taskId}`，TTL 30s / 续期 10s）。
- **实时音频出口已就绪**：`GET /api/{roomId}/stream/audio` 未录制 404、录制中 `video/x-flv` chunked（`servers/recording/Controllers/RecordingController.cs:101-135`），live 任务直接消费、404 探测作「直播已结束」判定；录播文件下载原生支持 Range（`PhysicalFile(enableRangeProcessing: true)`），ffmpeg `-ss` seek 高效。
- **测试设施**：tests csproj 已引用全部 6 个 server 项目（需追加两个新项目）；`RecordingFakes.cs` 已有 `FakeRecordingProcessFactory/FakeRecordingProcess` 等可复用；`Fakes.cs` 按需增补 `FakeStreamingAsrService` 等。
- **web 现状**：`App.tsx` 为 `PageKey` 联合 + `menuItems` 数组（:8-21），新增页 = 加一项；codegen 脚本串行生成 5 份 schema（`package.json:12`），追加 recording-transcript 契约同命令；全仓库**无 EventSource 用法**，本需求引入首个前端 SSE；`schema-recording.d.ts` 已存在但 `client.ts` 未 import（转写 schema 生成后须在 `client.ts` 实际使用）。
- **包版本基线**（csproj 内联精确版本）：Aspire.MongoDB.Driver 13.4.6 / MongoDB.Driver 3.9.0 / Aspire.StackExchange.Redis 13.4.6（两新服务 csproj 照抄 recording）；`InternalsVisibleTo Z.Vtuber.Tests` 沿用；CI 范本 `.github/workflows/build-recording-image.yml`（镜像名 `zero-tools/z-vtuber-*`，paths 限 `servers/<svc>/**` + `servers/common/**`）。

### 1.1 实施范围

- **做**：
  - 契约：`contracts/recording-transcript.openapi.yaml` 新建 v1.0.0（提案 §3.1 全部端点）；`contracts/bilibili-crawler.openapi.yaml` 追加转写代理端点（同路径 `/api/transcripts/**`、`/api/recordings/**`，区别于既有 `/api/b/live/**` 前缀）。
  - common：`RecordingEventChannels` + `RecordingStartedEvent`/`RecordingFinishedEvent`/`TranscriptSegmentEvent` record；5 个新实体 + 5 个仓储；`LiveSessionRepository` 新增按 RoomId+时间窗查询；`MongoDbInitializer` 新集合索引登记（含唯一索引）。
  - recording：`RecordingSession` 归档 insert + started/finished 发布（mid 参数透传）；`RecordingEventPublisher`；`RecordingBackfillMigration` 存量清洗（幂等 marker）。
  - transcript-consumer（新建，本地 8085）：事件订阅 ×2、enqueue 服务、对账补建、任务扫描器、任务锁、任务执行器、`IStreamingAsrService`/`AliFunAsrService`、`FfmpegAudioStream` 双音源。
  - transcript-api（新建，本地 8084）：4 控制器 + SSE replay/订阅/去重。
  - api：`RecordingTranscriptProxyController`（流式转发，SSE 禁缓冲）+ named HttpClient。
  - web：codegen 新增 `schema-recording-transcript.d.ts`；`client.ts` 转写方法；`pages/transcripts/` 三组件；`App.tsx` 菜单项。
  - 工程：slnx + 两项目、AppHost（两服务编排；Mongo 保持 standalone）、Dockerfile ×2（consumer 装 ffmpeg）、镜像 CI ×2、codegen 脚本。
  - 测试：全部新增/改写测试，核心逻辑覆盖率 ≥ 80%，无回归。
- **不做**：其他 ASR 提供商；转写结果二次加工；实时中断窗口回溯补识别；录播自动清理；crawler/notifier/cookie 零改动；web 直连新服务（一律经 api 代理）。

### 1.2 现状基线

- **新服务范本**：transcript-api 对齐 cookie（Web SDK + Controllers + Options `ValidateOnStart` + `EnsureIndexesAsync` + `/health`）；transcript-consumer 对齐 recording（Web SDK + HostedService + ffmpeg 外部进程 + Redis 锁）。
- **事件发布/订阅范本**：发布仿 crawler `RedisLiveEventPublisher`；订阅仿 notifier `CookieInvalidEventSubscriber`；频道常量仿 `CookieEventChannels`（常量式）。
- **锁范本**：`RecordingLock`（续期/fencing/Lua）整体仿写为 `TranscriptTaskLock`，不上提 common、不改既有实现（同 recording 决策）。
- **SSE 范本**：服务端 `servers/api/Controllers/BilibiliLiveController.cs` event/subscribe；前端无先例，`EventSource` 为首例。
- **代理范本**：`BilibiliLiveRecordingController` 流式代理（`ResponseHeadersRead` + `CopyToAsync`、不可达 502）。
- **迁移范本**：`MigrateCookieInvalidEventTypeSplitAsync`（marker + 幂等 + 回滚容错）。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash）与 Conventional Commits：

- 本需求开发全程只使用需求总分支 `feat/recording-transcript`：契约、实现、测试、文档全部直接在该分支提交，不切开发子分支（沿用近两期需求既定做法）。
- 需求总分支已推送 origin，需求总 PR 已存在（#105），提交照常推送更新分支。
- 提交信息使用 Conventional Commits：契约 `feat:`、测试先行 `test:`、实现 `feat:`、工程接入 `chore:`、文档 `docs:`；测试与实现分开提交。
- 并行 subagent 按子项目划分（recording 改造 / consumer / transcript-api / api / apps-web），共享文件（common 实体与事件契约、测试 csproj/fixture/Fakes、契约）归入主串行线先行，避免并行冲突（见第 4 节）。

## 3. 实施阶段

按「契约先行 → TDD 红-绿 → 工程接入 → 收口验证」推进。Phase 0 串行先行；Phase 1 测试先行（设施 1.1 先行，其余按子项目并行）；Phase 2 common（2.1）串行先行，随后 recording 改造 / consumer / transcript-api / api / web 多线并行；Phase 3 工程接入与实现可部分并行；Phase 4 串行收口。

### Phase 0: 契约先行

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | 新建 `contracts/recording-transcript.openapi.yaml` v1.0.0（transcript-api 服务面）：① `GET /health`；② `GET /api/recordings`（query `roomId?/limit?=50/before?`，200 `items[]` 含 `id/roomId/mid/title/files[]/startedAt/endedAt`）；③ `POST /api/transcripts/enqueue`（body `recordingId/modelId`；200 `{taskId,status:"PENDING"}`；404 录播或模型不存在/被禁用；409 返回已有 taskId）；④ `GET /api/transcripts/task`（query `recordingId?/roomId?/status?/limit?`，item 含 `id/recordingId/roomId/title/file/modelId/modelName/type/mode/source/status/position/error/createdAt/updatedAt`，`mode ∈ live\|file`、`source ∈ auto\|manual`）；⑤ `GET /api/transcripts/task/{taskId}`（`{task, segments[]}`，segment `{start,end,text}`）；⑥ `POST /api/transcripts/task/{taskId}/retry`（200 / 409 状态不允许）；⑦ `GET /api/transcripts/task/{taskId}/sse`（`text/event-stream`）；⑧ `GET /api/transcripts/settings`（全量列表 `roomId/mid/anchorName/enabled`）；⑨ `POST /api/recordings/{roomId}/transcripts/settings`（body `{enable: boolean}`）；⑩ `GET /api/recordings/{roomId}/transcripts`（`{roomId, autoEnqueueEnabled, items[]{recordingId,title,startedAt,endedAt,tasks[]}}`）；⑪ `GET/POST /api/transcripts/models`、`PUT /api/transcripts/models/{modelId}`（响应 schema **不含 apiKey**） | `contracts/recording-transcript.openapi.yaml`（新） | `npx openapi-typescript` 可成功生成类型；12 端点齐备；与提案 §3.1 逐字段一致 |
| 0.2 | `contracts/bilibili-crawler.openapi.yaml` 追加上述 ②-⑪ 的同路径代理端点（api 服务面，`/api/transcripts/**` 与 `/api/recordings/**`；SSE 端点标注 `text/event-stream`） | `contracts/bilibili-crawler.openapi.yaml` | codegen 通过；路径与 0.1 一致 |

### Phase 1: 测试先行（红）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | 测试设施（串行先行）：① tests csproj 追加 recording-transcript-consumer 与 recording-transcript-api 项目引用（项目骨架随本任务以最小形态创建，或引用先行占位）；② `MongoDbFixture` **保持 standalone `mongo:7.0` 不变**（无事务依赖，不需 `--replSet`/`rs.initiate`/`directConnection`）；③ `ResetAsync` drop 名单追加 5 新集合；④ `Fakes.cs` 增补 `FakeStreamingAsrService`（可注入 segment 序列/异常）、`FakeTranscriptTaskLockProvider`；`RecordingFakes.cs` 沿用 | `tests/Z.Vtuber.Tests/Z.Vtuber.Tests.csproj`、`MongoDbFixture.cs`、`Fakes.cs` | 编译通过；既有测试在 standalone 下全绿（无回归） |
| 1.2 | `TranscriptTaskRepositoryTests`（Mongo 集成）：① `RecordingId_1_ModelId_1` 唯一索引冲突抛 `MongoWriteException`（DuplicateKey）；② `TaskId_1_StartTime_1` 唯一索引幂等兜底（重复写入冲突可识别忽略）；③ 扫描查询：`Status_1__id_1` 按插入序取 PENDING；④ 状态机更新（PENDING→RUNNING→DONE/FAILED、FAILED→RUNNING retry 清 error）；⑤ position 原子推进；⑥ `transcript_models` 默认位部分唯一索引（至多一个 IsDefault）；⑦ `transcript_auto_enqueue` RoomId 唯一 upsert | `tests/Z.Vtuber.Tests/TranscriptTaskRepositoryTests.cs`（新） | 红 |
| 1.3 | `TranscriptTaskLockTests`（Redis 集成）：① 获取/释放后他人可获取；② 持锁期间他人获取失败；③ 10s 续期保持超 TTL 30s 不失效；④ 崩溃不续期 TTL 过期他人接管；⑤ fencing：锁易主后原持有者 `CheckOwnershipAsync` 失败；⑥ Lua 释放仅释放自持锁；⑦ 跨实例互斥 | `tests/Z.Vtuber.Tests/TranscriptTaskLockTests.cs`（新） | 红 |
| 1.4 | `TranscriptEnqueueTests`（Mongo 集成）：① 手动 enqueue 成功建 mode=file/source=manual 任务；② 同录播+同模型重复 enqueue 幂等（返回已有 taskId，不产生第二条）；③ 模型不存在/被禁用拒绝；④ 自动开播建 mode=live/source=auto、默认模型快照；⑤ File 选取优先 RAW 缺失回退首个 | `tests/Z.Vtuber.Tests/TranscriptEnqueueTests.cs`（新） | 红 |
| 1.5 | 事件订阅测试：① `RecordingStartedSubscriberTests`——启用自动识别的 roomId 收到 started 事件建 live PENDING 任务、未启用不建、重复事件幂等；② `RecordingFinishedSubscriberTests`——finished 事件对漏建场次补建（live 任务已存在则不重复） | `tests/Z.Vtuber.Tests/RecordingStartedSubscriberTests.cs`、`RecordingFinishedSubscriberTests.cs`（新） | 红 |
| 1.6 | `AliFunAsrOffsetTests`（本地假 websocket server 模拟 FunASR 协议）：① run-task/音频帧/finish-task 协议交互顺序；② 仅 `sentence_end=true` 成句 yield（中间结果不 yield）；③ yield 时间 = 返回时间 + 构造传入 offset；④ 连接中断抛异常 | `tests/Z.Vtuber.Tests/AliFunAsrOffsetTests.cs`（新） | 红 |
| 1.7 | `FfmpegAudioStreamTests`（环境有 ffmpeg 才运行，否则 Skip）：① lavfi 生成测试音频，`-ss {position}` seek 后输出起点正确；② 输出为 s16le 16kHz mono（字节率 32000B/s）；③ 本地 chunked HTTP 流模拟 `/stream/audio`，断言实时跟随与 EOF 正常收尾；④ 1 秒缓冲喂入语义 | `tests/Z.Vtuber.Tests/FfmpegAudioStreamTests.cs`（新） | 红（无 ffmpeg 环境 Skip 不失败） |
| 1.8 | `TranscriptTaskRunnerTests`（Mongo+Redis 集成 + Fake ASR/ffmpeg）：① 断点续跑——预置 position 与 segments，断言续跑不重不漏；② segment insert 后 position 更新为顺序写、`(TaskId, StartTime)` 唯一索引幂等（重复/并发同句写入不产生重复、position 不回退）；③ 文件识别完 → DONE；异常 → FAILED 含原因；④ live 任务接管探测：实时端点 200 → 接流从当前位置继续（不回溯）；404 → 转 file 模式从 position seek 补尾至 DONE；⑤ 模型 MaxConcurrency 门控 | `tests/Z.Vtuber.Tests/TranscriptTaskRunnerTests.cs`（新） | 红 |
| 1.9 | `TranscriptReconcilerTests`（Mongo 集成）：① 开播中漏建（`recording_status.state=recording` + 已启用 + 无当前场次任务 → 补建 live）；② 结束后漏建（`recordings.EndedAt!=null` + 无该 recording+默认模型任务 → 补建 file）；③ 历史不补（StartedAt/EndedAt 早于设置启用时间）；④ 周期重复执行幂等 | `tests/Z.Vtuber.Tests/TranscriptReconcilerTests.cs`（新） | 红 |
| 1.10 | `RecordingBackfillMigrationTests`：① 文件名解析场次名 + 文件创建时间落入 `bilibili_live_session` `[StartDate,StopDate]` 窗口匹配；② 无窗口匹配时 `bilibili_live_status` 兜底；③ 同场次 RAW/AV1 聚合一条 recordings；④ 已存在（RoomId+StartedAt 幂等键）跳过、重复执行幂等；⑤ 不匹配文件记 warning 不阻断 | `tests/Z.Vtuber.Tests/RecordingBackfillMigrationTests.cs`（新） | 红 |
| 1.11 | recording 服务改造测试：① 开始录制 insert recordings（title/room_id/mid/files 齐）+ pub recording-started（载荷含 liveAudioUrl）；② exitCode==0 回写 EndedAt + pub recording-finished（files/fileUrls）；③ fencingLost/停机分支不发布；④ 发布失败仅记日志不阻断录制 | `tests/Z.Vtuber.Tests/RecordingSessionTests.cs`（改）、`RecordingEventPublisherTests.cs`（新） | 红 |
| 1.12 | transcript-api 测试：① 各端点响应形状（0.1 契约对照）；② enqueue 404/409；③ retry 仅 FAILED 可重试（409）、重试后 status=RUNNING 且 error 清空；④ SSE：先 replay 已入库 segments 再转发实时 pub、按 `start` 去重、终态推 done 关闭、15s 心跳；⑤ models 响应不回传 apiKey、设默认原子清除其他默认位 | `tests/Z.Vtuber.Tests/TranscriptApiTests.cs`（新） | 红 |
| 1.13 | api 代理测试：① `/api/transcripts/**` 与 `/api/recordings/**` 转发至 transcript-api（stub HttpClient）；② 上游不可达 502；③ SSE 代理 `ResponseHeadersRead` 不缓冲、头透传 | `tests/Z.Vtuber.Tests/RecordingTranscriptProxyControllerTests.cs`（新） | 红 |
| 1.14 | 前端测试：① `client.test.ts`——转写方法路径与类型；② `TranscriptsPage.test.tsx`——两 Tab 渲染与切换；③ `TaskListTab`——任务列表渲染、进行中优先、EventSource mock 实时追加 segments、断线重连 replay 不丢；④ `SettingsTab`——自动识别开关切换、手动 enqueue 选择场次+模型、FAILED 重试交互、模型列表/添加/禁用/设默认表单、apiKey 不回显 | `apps/web/src/api/client.test.ts`、`apps/web/src/pages/transcripts/*.test.tsx`（新） | `npm run test` 红 |

### Phase 2: 实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | common 演进（串行先行）：① `Live/RecordingEventChannels.cs`（常量式：`Started="recording-started"`、`Finished="recording-finished"`、`SegmentChannel(taskId)="transcript-segment:{taskId}"`）+ `RecordingStartedEvent`/`RecordingFinishedEvent`/`TranscriptSegmentEvent` record（载荷字段与提案 §3.2 一致）；② `Persistence/Entities/` 新增 `RecordingRecord`/`TranscriptTaskRecord`/`TranscriptSegmentRecord`/`TranscriptModelRecord`/`TranscriptAutoEnqueueRecord`（字段与提案 §4 一致，UTC 日期沿用 `[BsonDateTimeOptions(Kind=Utc)]` 形态，状态/Mode/Source 字符串常量）；③ 仓储 ×5：`RecordingRepository`（insert/回写 EndedAt/分页列表/按 RoomId+StartedAt 幂等查）、`TranscriptTaskRepository`（insert/扫描查询/状态与 position 更新/按 RecordingId+ModelId 查）、`TranscriptSegmentRepository`（顺序 insert/按 TaskId 升序读）、`TranscriptModelRepository`（CRUD/默认位原子切换）、`TranscriptAutoEnqueueRepository`（upsert/全量读）；④ `LiveSessionRepository` 新增按 RoomId+时间窗查场次（清洗用）；⑤ `MongoCollectionNames` +5 集合常量；⑥ `MongoDbInitializer` 新增 `EnsureTranscriptIndexesAsync`（recordings `RoomId_1_StartedAt_-1`；transcript_tasks 唯一 `RecordingId_1_ModelId_1` + `Status_1__id_1`；transcript_segments 唯一 `TaskId_1_StartTime_1`；transcript_models IsDefault 部分唯一；transcript_auto_enqueue `RoomId_1` 唯一）挂 `EnsureIndexesAsync` | `servers/common/Live/RecordingEventChannels.cs`（新）、`servers/common/Persistence/Entities/*.cs`（新×5）、`servers/common/Persistence/*Repository.cs`（新×5、改 LiveSessionRepository）、`servers/common/Persistence/MongoDbInitializer.cs` | 1.2 转绿；既有测试无回归 |
| 2.2 | recording 改造·归档与事件：`Recording/RecordingEventPublisher.cs`（Redis pub started/finished，失败仅记日志）；`RecordingSession`——`RunAsync` 增加 `mid` 参数（`LiveStatusPoller` :102 透传）；开始处（ffmpeg 启动、files 渲染完成后 :146 附近）insert recordings + pub recording-started（liveAudioUrl 按自身服务内网地址拼 `/api/{roomId}/stream/audio`）；exitCode==0 分支（:229-232）回写 EndedAt 后 pub recording-finished（files + fileUrls 按 `/api/{roomId}/recordings/{urlEncodedFileName}` 拼接）；fencingLost/停机分支不发布 | `servers/recording/Recording/RecordingSession.cs`、`servers/recording/Recording/RecordingEventPublisher.cs`（新）、`servers/recording/Live/LiveStatusPoller.cs`、`servers/recording/Program.cs` | 1.11 转绿 |
| 2.3 | recording 改造·存量清洗：`Recording/RecordingBackfillMigration.cs`（启动时执行，沿用 `zvtuber_meta` marker 幂等模式：遍历 `{StorageDir}/{roomId}/*.flv` → 文件名解析场次名 + **文件创建时间**落 `bilibili_live_session` 窗口匹配（无匹配 `bilibili_live_status` 兜底）→ 同场次 RAW/AV1 聚合 → RoomId+StartedAt 幂等键跳过已存在 → 统计记日志、不匹配 warning 不阻断）；`Program.cs` 启动调用 | `servers/recording/Recording/RecordingBackfillMigration.cs`（新）、`servers/recording/Program.cs` | 1.10 转绿 |
| 2.4 | transcript-consumer 骨架：`servers/recording-transcript-consumer/Z.Vtuber.RecordingTranscript.Consumer.csproj`（`Microsoft.NET.Sdk.Web`、net10.0、`InternalsVisibleTo Z.Vtuber.Tests`、包版本照抄 recording csproj、引用 common）；`Program.cs`（Mongo/Redis/Options `ValidateOnStart`/仓储/服务注册、`EnsureIndexesAsync`、`/health` + HostedService 挂载）；`Configuration/TranscriptConsumerOptions.cs`（节 `TranscriptConsumer`：`ScanIntervalSeconds=10`/`LockTtlSeconds=30`/`LockRenewIntervalSeconds=10`/`AudioBufferSeconds=60`/`ReconcileIntervalSeconds=300`/`RecordingService:BaseUrl`/`FfmpegPath`）；`Properties/launchSettings.json`（8085）；`MongoDbHealthCheck` 复制同模式；slnx 加入 | `servers/recording-transcript-consumer/`（新）、`servers/Z.Vtuber.slnx` | 编译通过；缺配置 fail-fast |
| 2.5 | consumer·事件与 enqueue：`Events/RecordingStartedSubscriber.cs`/`Events/RecordingFinishedSubscriber.cs`（仿 `CookieInvalidEventSubscriber`：BackgroundService + ChannelMessageQueue 循环，单条失败记 warning 继续）；`Enqueue/TranscriptEnqueueService.cs`（幂等建任务：started→该 roomId 启用自动识别时按当前默认模型建 mode=live/source=auto；手动/finished 补建→mode=file；File 选取优先 RAW；唯一索引冲突识别为已存在返回既有 taskId；仅补「启用之后」的场次） | `servers/recording-transcript-consumer/Events/`、`Enqueue/`（新） | 1.4、1.5 转绿 |
| 2.6 | consumer·对账：`Reconcile/TranscriptReconciler.cs`（HostedService，5min 周期 + 抖动：开播中漏建——`recording_status.state=recording` + 已启用 + 无当前场次任务 → 补建 live PENDING；结束后漏建——`recordings.EndedAt!=null` + 已启用 + 无该 recording+默认模型任务 → 补建 file PENDING；StartedAt/EndedAt 须 ≥ 设置启用时间） | `servers/recording-transcript-consumer/Reconcile/`（新） | 1.9 转绿 |
| 2.7 | consumer·任务锁与扫描器：`Tasks/TranscriptTaskLock.cs`（仿 `RecordingLock`：key `transcript:task-lock:{taskId}`、SET NX PX TTL 30s、10s `pexpire` 续期 Lua、token 比对释放 Lua、`CheckOwnershipAsync` fencing）；`Tasks/TranscriptTaskScanner.cs`（HostedService，10s 周期 + 抖动：查 `status=PENDING` 或 `RUNNING 且锁 key 不存在`，按 `_id` 升序逐条抢锁；抢锁成功且该模型在跑数 < `MaxConcurrency` 才交 Runner；RUNNING 无锁即接管续跑） | `servers/recording-transcript-consumer/Tasks/TranscriptTaskLock.cs`、`TranscriptTaskScanner.cs`（新） | 1.3 转绿；扫描查询单测绿 |
| 2.8 | consumer·音频链路：`Audio/FfmpegAudioStream.cs`（双音源统一输出 16kHz mono s16le pipe：live 模式 `ffmpeg -i {liveAudioUrl} -vn -ac 1 -ar 16000 -f s16le pipe:1` 实时跟随；file 模式追加 `-ss {position/1000}s` 断点 seek；stdout 按 1 秒缓冲（32000B）经 `System.IO.Pipelines` 桥接为入参 Stream；有界缓冲上限 60s 音频，满则暂停读 stdout 形成背压） | `servers/recording-transcript-consumer/Audio/FfmpegAudioStream.cs`（新） | 1.7 转绿 |
| 2.9 | consumer·ASR：`Asr/IStreamingAsrService.cs`（`StreamingAsrSegment(long Start, long End, string Text)` + `IAsyncEnumerable<StreamingAsrSegment> TranscribeAsync(Stream, CancellationToken)`）；`Asr/AliFunAsrService.cs`（构造传入初始 position offset；`ClientWebSocket` 直连 FunASR realtime，握手带 ApiKey，run-task/音频帧/finish-task 协议；仅 `sentence_end=true` 成句 yield，时间 = 返回时间 + offset；连接中断抛异常） | `servers/recording-transcript-consumer/Asr/`（新） | 1.6 转绿 |
| 2.10 | consumer·任务执行器：`Tasks/TranscriptTaskRunner.cs`（单任务编排：按 Mode 选音源——live 任务先探测实时音频端点，200 接流（offset 取接管时刻录制时长，由归档 StartedAt 推算，中断窗口不回溯）、404 转 file 模式从 position seek 补尾；每个 yield **顺序落库**：先 insert segment 再更新 position=segment.End（非事务；`(TaskId,StartTime)` 唯一索引冲突忽略兜底幂等、position 不回退）；落库后 pub `transcript-segment:{taskId}`；EOF/文件读完 → DONE、异常 → FAILED 含原因；处理循环每轮 fencing 校验失锁即终止；finally Lua 释放锁） | `servers/recording-transcript-consumer/Tasks/TranscriptTaskRunner.cs`（新） | 1.8 全绿 |
| 2.11 | transcript-api 服务：`servers/recording-transcript-api/Z.Vtuber.RecordingTranscript.Api.csproj`（Web SDK、版本照抄、引用 common）+ `Program.cs`（8084 launchSettings）+ 4 控制器：`Controllers/RecordingsArchiveController.cs`（`GET /api/recordings`、`GET /api/recordings/{roomId}/transcripts`、`POST .../settings` upsert）、`Controllers/TranscriptTasksController.cs`（enqueue/列表/详情/retry——仅 FAILED 可重试置 RUNNING 清 error/409）、`Controllers/TranscriptSettingsController.cs`（全量列表，关联 `AccountBasicInfoRepository` 补 anchorName）、`Controllers/TranscriptModelsController.cs`（CRUD，响应剔除 apiKey，isDefault 原子切换）；`Sse/TranscriptSegmentStream.cs`（replay 全量 → 订阅 Redis 转发、`start` 键去重、15s 心跳、终态 done 关闭） | `servers/recording-transcript-api/`（新）、`servers/Z.Vtuber.slnx` | 1.12 转绿 |
| 2.12 | api 代理：`Controllers/RecordingTranscriptProxyController.cs`（`/api/transcripts/**` 与 `/api/recordings/**` 流式转发：`ResponseHeadersRead` + 不缓冲、SSE 端点头透传、上游不可达 502；范本 `BilibiliLiveRecordingController`）；`Program.cs` 注册 named HttpClient（`TranscriptService:BaseUrl`） | `servers/api/Controllers/RecordingTranscriptProxyController.cs`（新）、`servers/api/Program.cs` | 1.13 转绿 |
| 2.13 | web 改造（仅依赖 Phase 0 契约，与后端并行）：① `package.json` codegen 追加 recording-transcript 契约 → 生成 `schema-recording-transcript.d.ts`（勿手改）+ 重新生成 `schema.d.ts`；② `client.ts` 转写方法（enqueue/任务列表/详情/retry/设置/模型 CRUD；SSE 用原生 `EventSource` 直取 URL）；③ `pages/transcripts/TranscriptsPage.tsx`（两 Tab 切换）；④ `TaskListTab.tsx`（任务列表：状态/场次/模型/进度 position；选中任务展示 segments 流，EventSource 实时追加、断开自动重连）；⑤ `SettingsTab.tsx`（识别设置：主播开关列表 + 手动 enqueue 场次/模型选择器 + 按主播查看场次任务 + FAILED 重试；模型设置：列表 + 添加模型表单含 API Key + 启用/禁用/设默认/最大并发）；⑥ `App.tsx` `PageKey` + `menuItems` 加「Transcripts」 | `apps/web/package.json`、`apps/web/src/api/`、`apps/web/src/pages/transcripts/`（新×3）、`apps/web/src/App.tsx` | 1.14 转绿；`npm run lint`/`npm run build` 通过 |

### Phase 3: 工程接入

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 3.1 | Dockerfile 与 CI ×2：transcript-api 照 api/cookie 纯净两阶段模式；consumer 照 recording 模式（runtime `apt-get install -y ffmpeg`）；均 `EXPOSE 8080`、restore 前同拷 common csproj；`.github/workflows/build-recording-transcript-api-image.yml` 与 `build-recording-transcript-consumer-image.yml` 复制 recording workflow，镜像名 `zero-tools/z-vtuber-recording-transcript-{api,consumer}`，paths 限各自服务目录 + `servers/common/**` | `servers/recording-transcript-api/Dockerfile`、`servers/recording-transcript-consumer/Dockerfile`（新）、`.github/workflows/`（新×2） | 本地 `docker build` 成功；workflow 语法有效 |
| 3.2 | Aspire 编排（Mongo 保持 standalone）：① AppHost mongo 资源**维持 standalone，不做 replica set 改造**（非事务顺序写 + 唯一索引幂等，无事务前置）；② `AddProject` 两新服务 + `WithReference/WaitFor(mongo, messaging)` + `WithHttpHealthCheck("/health")`；consumer 加 `WithReference(recording)` + `WithEnvironment("RecordingService:BaseUrl", ...)`；api 加 `WithEnvironment("TranscriptService:BaseUrl", transcriptApi.GetEndpoint("http"))` | `infra/apphost/AppHost.cs` | Aspire 一键拉起含两新服务的全套环境（standalone Mongo） |
| 3.3 | 部署说明：PR #105 描述补充生产部署要点——MongoDB 保持 standalone（非事务顺序写 + 唯一索引幂等，无 replica set 前置）；consumer 镜像含 ffmpeg、可多副本（锁保证单任务单节点）；transcript-api 无状态多副本；配置项清单（`TranscriptConsumer:*`、`RecordingService:BaseUrl`、`TranscriptService:BaseUrl`） | PR 描述（仓库外部署清单指引） | 描述完整 |

### Phase 4: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 4.1 | `dotnet test tests/Z.Vtuber.Tests` 全绿（含既有回归与 standalone Mongo 集成测试）；核心逻辑（锁/扫描/执行器/ASR offset/对账/清洗/enqueue）覆盖率 ≥ 80%；`npm run test`/`lint`/`build` 全绿；grep 确认：5 新集合在 `MongoCollectionNames`/`EnsureIndexesAsync`/`MongoDbFixture.ResetAsync` 三处登记齐备、契约端点与实现路由一致、apiKey 不出现在任何响应 DTO | 全部通过 |
| 4.2 | 编排手工验证（z-aspire-orchestrator）：本地 Aspire 拉起全量 → 页面为主播启用自动识别 → 开播 1 分钟内出现 live 任务并实时产出 segments（任务列表 Tab 实时可见）→ 下播任务自动 DONE、完整 Transcript 可查 → 杀掉 consumer 容器验证接管（任务被他节点续跑、segments 不重复；live 接管直播已结束时从断点补尾至 DONE）→ 手动对已结束场次 enqueue（重复发起 409）→ FAILED 任务重试 → 模型添加/禁用/设默认生效且 apiKey 不回显 | 手工验证记录 |
| 4.3 | AGENTS.md 同步：新建 `servers/recording-transcript-consumer/AGENTS.md` 与 `servers/recording-transcript-api/AGENTS.md`（对齐 recording/cookie 格式）；更新 `servers/common/AGENTS.md`（5 新集合实体/仓储/事件频道）、`servers/recording/AGENTS.md`（归档/事件/清洗）、`servers/api/AGENTS.md`（转写代理端点）、`apps/web/AGENTS.md`（Transcripts 页、schema-recording-transcript.d.ts、EventSource 先例）、`docs/engineering/backend-rules.md` 项目结构图补两新服务 | 文档与实现一致 |

## 4. 并行策略

- Phase 0（契约 0.1/0.2）串行先行——transcript-api、api 代理、前端 codegen 均依赖契约。
- Phase 1：1.1（测试设施，Mongo 保持 standalone 不变）串行先行；随后后端测试（1.2-1.13）与前端测试（1.14）互不依赖，可按子项目拆 subagent 并行。
- Phase 2：2.1（common）串行先行——各线均依赖 common 实体/事件契约；随后 recording 改造线（2.2 → 2.3）、consumer 线（2.4 → 2.5/2.6 → 2.7 → 2.8/2.9 → 2.10 大体串行）、transcript-api 线（2.11）、api 线（2.12）、web 线（2.13）分属不同子项目，按 z-coding 规范拆 subagent 并行，各 subagent 负责「实现 → 提交 → 测试修复 → 提交」闭环。
- Phase 3（3.1-3.3）与 Phase 2 互不依赖可并行（Mongo 保持 standalone，无 replica set 前置）。
- Phase 4 串行收口。全部提交在需求总分支 `feat/recording-transcript`。

## 5. 验收对照

| PRD 功能点 | 实现任务 | 验证方式 |
|-----------|---------|---------|
| 功能点 1：录播场次归档（含存量清洗） | 2.1②③、2.2、2.3 | 1.10、1.11 + 手工验证（4.2） |
| 功能点 2：按主播启用自动识别（开播即实时识别） | 0.1⑧⑨、2.2、2.5、2.6、2.10、2.13⑤ | 1.5、1.9、1.12 + 手工验证（4.2） |
| 功能点 3：手动发起识别（同场同模型唯一） | 0.1③、2.1⑥（唯一索引）、2.5、2.11、2.13⑤ | 1.4①-③、1.12② + 手工验证（4.2） |
| 功能点 4：识别队列与断点续跑（单节点持锁/宕机接管/live-file 双形态） | 2.7、2.8、2.9、2.10 | 1.2、1.3、1.7、1.8 + 杀容器接管手工验证（4.2） |
| 功能点 5：任务列表 Tab（实时 Segments） | 0.1④⑤⑦、2.11、2.12、2.13③④ | 1.12④、1.13③、1.14③ + 手工验证（4.2） |
| 功能点 6：设置 Tab（识别设置 + 模型设置） | 0.1⑧-⑪、2.11、2.13⑤ | 1.12①⑤、1.14④ + 手工验证（4.2） |
| 非功能：覆盖率 ≥ 80%、全量测试绿、apiKey 不外泄 | 4.1 | `dotnet test` / `npm run test` / grep |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| 非事务顺序写幂等 | segment insert 与 position 更新非原子（下游决策不改 Mongo 部署） | `(TaskId, StartTime)` 唯一索引冲突忽略 + position 仅向前推进，幂等重跑/接管最终一致；1.8② 锁定幂等语义 |
| FunASR 协议外部依赖 | websocket 协议以三方文档为准，联调前无法验证真实行为 | 假 websocket server 契约测试锁定协议交互与 offset/sentence_end 语义（1.6）；失败路径断点已落库可重试（1.8③） |
| 实时识别背压 | 识别慢于直播实时速度时背压传导至 recording `AudioStreamHub`（DropOldest），极端情况丢段 | 有界缓冲上限 60s（2.8）；模型 MaxConcurrency 门控（2.7）；PRD 明确中断缺口不回溯，以监控兜底 |
| mid 透传改动 | `RecordingSession.RunAsync` 签名变更涉及 LiveStatusPoller 调用点 | 改动面收敛在两文件（2.2）；1.11 锁定行为 |
| 文件名 12 小时制 | `RecordingFileName` 模板 `hh` 为 12 小时制，按文件名解析时间有歧义 | 清洗以**文件创建时间**为匹配基准（2.3），文件名仅解析场次名辅助 |
| live 接管 offset 精度 | 接管时刻的「已识别时长坐标」由归档 StartedAt 推算，与实际识别进度可能有偏差 | 偏差只影响时间轴标注不影响内容连续性；1.8④ 锁定语义；PRD 允许中断窗口缺口 |
| ffmpeg 环境依赖 | consumer 识别与音频测试依赖外部 ffmpeg | Dockerfile runtime 安装（3.1）；测试环境缺失时 1.7 标记 Skip 不失败 |
| 共享文件并行冲突 | common 实体、契约、测试 csproj/fixture 为多线共同依赖 | 全部归入主串行线先行（Phase 0/1.1/2.1），并行线只动各自子项目（第 4 节） |
| 依赖 | Aspire.MongoDB.Driver 13.4.6 / MongoDB.Driver 3.9.0 / Aspire.StackExchange.Redis 13.4.6、Testcontainers 4.13.0、openapi-typescript、部署镜像 ffmpeg、阿里云 FunASR API Key 与外网访问 | 版本已核实（1.0）；API Key 由页面模型配置注入 |

## 7. 相关文档

- PRD：`docs/product/draft/20260916-recording-transcript-prd.md`（v1.0）
- 技术提案：`docs/engineering/proposals/20260916-proposal-recording-transcript.md`
- 契约：`contracts/recording-transcript.openapi.yaml`（Phase 0 新建 v1.0.0）、`contracts/bilibili-crawler.openapi.yaml`、`contracts/recording.openapi.yaml`
- 前序 Plan：[[20260913-plan-live-recording]]（录播功能）、[[20260911-plan-cookie-service]]（新服务工程范本）
- FunASR realtime websocket API：<https://platform.qianwenai.com/docs/api-reference/speech-recognition/fun-asr-realtime/websocket-api.md>
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`docs/engineering/frontend-rules.md`、`servers/common/AGENTS.md`、`servers/recording/AGENTS.md`、`servers/api/AGENTS.md`、`apps/web/AGENTS.md`
