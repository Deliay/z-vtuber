---
description: B站直播录播功能实现 Plan——基于 PRD v1.0 与技术提案（feat/live-recording @5f1ff09）的实施阶段划分与任务分解；契约先行（recording.openapi.yaml 新建 + bilibili-crawler 契约追加录播端点与 recording 订阅类型），common 演进（SubscriptionType.Recording + recording_status 共享集合），新建 servers/recording（开播轮询 + playurl 选流 + ffmpeg 单进程多输出 RAW/AV1/实时音频 + Redis per-room 锁续期 fencing），api 录播代理与状态聚合，web 录播订阅类型与录制状态页，crawler 一行适配（AccountInfoJob 覆盖 Recording 订阅），Aspire/Dockerfile/CI 工程接入
type: Permanent
---

# B站直播录播 - 实现 Plan

**功能名称**: B站直播录播
**关联 PRD**: `docs/product/draft/20260913-live-recording-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20260913-proposal-live-recording.md`
**Plan 版本**: v1.0
**创建日期**: 2026-09-13
**作者**: Workflow Agent（代起草）
**feat-branch**: `feat/live-recording`（需求总分支，已推送 origin；需求总 PR：<https://github.com/Deliay/z-vtuber/pull/89>）

## 1. 概述

本 Plan 基于 PRD v1.0 与技术提案编写，指导「B站直播录播」的编码实施。改动分七块：① 契约——新建 `contracts/recording.openapi.yaml`，`contracts/bilibili-crawler.openapi.yaml` 追加录播端点并将订阅 type 枚举扩展 `recording`（先于实现）；② common 演进——`SubscriptionType.Recording = 3`、`recording_status` 共享集合实体/仓储/唯一索引（recording 写、api 读，按归属约定入 common）；③ 新建 `servers/recording`（Z.Vtuber.Recording）：订阅监听、开播轮询、playurl 选流、ffmpeg 单进程多输出（RAW copy + AV1 二压 + 实时音频 pipe）、Redis per-room 锁（续期 + fencing）、音频 Hub、自身 API；④ api 新增录播索引/下载/实时音频代理与录制状态聚合；⑤ web 订阅页「录播」类型 + 新增「录制状态」页；⑥ crawler 一行适配（`AccountInfoJob` 覆盖 Recording 订阅，保障 mid→roomId 资料抓取）；⑦ 工程接入（slnx/Aspire/Dockerfile/CI/codegen）。实施全程遵循 `docs/engineering/common-rules.md`（TDD、契约式开发、核心逻辑覆盖率 ≥ 80%）、`backend-rules.md`、`frontend-rules.md` 及各子项目 AGENTS.md。

### 1.0 上游核实结论（代码现状复查）

对需求总分支 `feat/live-recording`（HEAD `5f1ff09`）代码核实，提案技术路径**整体可行**，现状细节与偏差如下，处理方式在对应任务中给出：

- **订阅模型**：`SubscriptionType`（`servers/common/Persistence/Entities/SubscriptionType.cs:6-21`）现有 `Dynamic=0/Danmaku=1/Guard=2`，均带 `[BsonRepresentation(BsonType.String)]`——新增 `Recording = 3` 沿用同形态，纯增量无迁移。`SubscriptionRecord` 仅存 `Mid/Type/SubscribedAt`，**订阅创建时不解析 roomId**（`SubscriptionService.SubscribeAsync` 为 `(Mid,Type)` SetOnInsert 幂等 upsert，`servers/common/Services/SubscriptionService.cs:40-57`）；roomId 由 crawler Job 经 `AccountBasicInfoRepository.GetLatestRoomIdByMidAsync`（`servers/common/Persistence/AccountBasicInfoRepository.cs:28`）惰性解析。**偏差**：提案 §3.2「创建时先经 basic info 由 mid 解析 roomId 并落库」与现状不符——现状为「订阅存 mid，消费方按需解析 roomId」。本 Plan 按现状实施：recording 订阅监听复刻 `LiveStatusJob` 的解析路径（任务 2.4），api 订阅端点仅放开 type 枚举（任务 2.9），行为与弹幕订阅完全一致（PRD 功能点 1「和弹幕一致」）。
- **`AccountInfoJob` 不覆盖 Recording 订阅（提案未覆盖的缺口）**：`servers/crawler/Jobs/AccountInfoJob.cs:30-38` 周期枚举仅 `Dynamic/Danmaku/Guard` 三类 mid；`OnSubscribed` 事件（:41）虽不限类型，但 `Subscribed` 是进程内事件——订阅写发生在 api 进程，crawler 实例收不到，纯录播订阅的主播将永远没有 basic info（mid→roomId 解析失败，录制永不触发）。**处理**：`AccountInfoJob.RunOnceAsync` 枚举追加 `SubscriptionType.Recording`（一行改动，任务 2.10）。这是对提案「crawler 抓取逻辑零改动」的必要最小偏离，不改任何抓取逻辑本身。
- **分布式锁设施已存在**：契约 `IDistributedLockProvider.AcquireAsync(key, ttl, waitTimeout)` 在 common（`servers/common/Locking/IDistributedLockProvider.cs:3-11`，handle 为 `IAsyncDisposable`），Redis 实现在 crawler 与 cookie 各一份同构拷贝（`servers/crawler/Locking/RedisDistributedLockProvider.cs`，`LockTakeAsync`+token+`LockReleaseAsync` 比对释放）。但既有实现**无续期、无持锁校验（fencing）**能力，不满足录制长时持锁语义。**处理**：按提案决策 6.1，recording 内自实现 `RecordingLock`（SET NX PX + 周期续期 + Lua 释放 + 每轮 fencing 校验失锁即杀 ffmpeg），以上述实现为基底扩展（任务 2.6），不上提 common、不改既有两处实现。
- **Cookie 取用**：`ICookieServiceClient`（checkout/report-invalid/cooldown）契约与实现均在 crawler（`servers/crawler/Services/CookieServiceClient.cs:8-13`），`NoAvailableCookieException`/`CookieCheckoutResult` 模型在 common。按归属约定该契约现仅 crawler 使用；recording 需要 checkout（playurl 拉流携带登录 Cookie）。**处理**：recording 内实现精简版 `CookieServiceClient`（仅 checkout + report-invalid，复用 common 模型，同构拷贝先例同分布式锁），不上提契约避免扩大 crawler 改动面（任务 2.5）。
- **`MongoDbHealthCheck` 在 crawler**（`servers/crawler/MongoDbHealthCheck.cs:6`），recording 复制同模式（任务 2.2）。
- **订阅 API 现状**：`BilibiliSubscriptionController`（`servers/api/Controllers/BilibiliSubscriptionController.cs`）type 解析走 `Enum.TryParse<SubscriptionType>` 已泛化（:38-42、:62-66），但 4 处 400 错误消息硬编码 `"danmaku, dynamic, guard"`（:29/:35/:41 等）需同步；契约 `contracts/bilibili-crawler.openapi.yaml` 的 unsubscribe `type` 参数有 `enum: [danmaku, dynamic, guard]`（:79）与描述文案需追加 `recording`。
- **web 现状**：`App.tsx` **不是 react-router**，是 `menuItems` 数组 + 状态切换（:15-19），新增页 = 数组加一项（修正提案 §7「+ 路由」表述）；`SubscriptionPage.tsx` 的 type 联合字面量 `'danmaku'|'dynamic'|'guard'` 硬编码于 :17/:41/:60/:189，`TYPE_LABELS`（:23-26）需加「录播」；`package.json:12` codegen 串行生成四份 schema，recording 契约追加同命令；web 只经 `/api` 访问 api 服务，录播代理全部走 api，**无需新增 vite/nginx 代理**（与 cookie 服务直连模式不同）。
- **测试设施现状**：`tests/Z.Vtuber.Tests` csproj 已引用 api/common/crawler/notifier/cookie 五项目（需追加 recording）；`MongoDbFixture`/`RedisFixture`（Testcontainers 4.13.0）可复用；`Fakes.cs` 按需增补；`StartupDiTests`（MongoDbFixture + IClassFixture）为启动 DI 断言先例。
- **slnx/AppHost/工程范本**：`servers/Z.Vtuber.slnx` Folder 结构需加 recording；`infra/apphost/AppHost.cs`（:1-55）现编排 mongo/messaging/api/cookie/crawler/notifier/web，cookie 服务已有 `WithEnvironment` 数据目录先例（:23-24），recording 需新增录播存储卷挂载；Dockerfile 范本 crawler 两阶段（`servers/crawler/Dockerfile`），runtime 阶段需追加 ffmpeg 安装；CI 范本 `.github/workflows/build-crawler-image.yml` / `build-notifier-image.yml`。
- **包版本基线**（无中央包管理，csproj 内联精确版本）：Aspire.MongoDB.Driver 13.4.6 / MongoDB.Driver 3.9.0 / Aspire.StackExchange.Redis 13.4.6，recording csproj 照抄；`InternalsVisibleTo Z.Vtuber.Tests` 沿用。

### 1.1 实施范围

- **做**：
  - 契约：`contracts/recording.openapi.yaml` 新建 v1.0.0；`contracts/bilibili-crawler.openapi.yaml` 追加 4 个录播端点 + 订阅 type 枚举/文案扩展 `recording`。
  - common：`SubscriptionType.Recording = 3`；`RecordingStatusRecord` 实体 + `RecordingStatusRepository`（RoomId 键 upsert/全量读）+ `MongoDbInitializer` 新增 `recording_status` 唯一索引。
  - recording 服务（新建 `servers/recording`）：骨架 + `Subscriptions/`（Recording 订阅监听）+ `Live/`（开播轮询、playurl 选流）+ `Recording/`（文件名模板纯函数、ffmpeg 多输出参数构造、场次编排、Redis 锁续期/fencing、状态上报）+ `Audio/`（实时音频 Hub 有界缓冲丢弃）+ `Controllers/` + Options + `launchSettings.json`（8083）。
  - api：`BilibiliLiveRecordingController`（列表/下载/音频流式代理 + 状态聚合读 Mongo）；`BilibiliSubscriptionController` 错误文案同步；`Program.cs` 注册 recording HttpClient。
  - web：codegen 生成 `schema-recording.d.ts` + 更新 `schema.d.ts`；`client.ts` 录播方法；`SubscriptionPage` 加「录播」类型；新增 `RecordingStatusPage`；`App.tsx` 菜单项。
  - crawler：`AccountInfoJob` 枚举追加 Recording（一行）。
  - 工程：slnx、AppHost（recording 编排 + 存储卷）、Dockerfile（含 ffmpeg）、`build-recording-image.yml`、codegen 脚本。
  - 测试：全部新增/改写测试，核心逻辑覆盖率 ≥ 80%，无回归。
- **不做**：crawler 抓取链路其他改动；微博链路；notifier；GPU 编码环境安装（部署前置条件）；录播文件自动清理/生命周期管理；web 直连 recording（一律经 api 代理）。

### 1.2 现状基线

- **新服务范本**：cookie 服务（Web SDK + Controllers + Options `ValidateOnStart` + `EnsureIndexesAsync` + `/health`）与 notifier（csproj 依赖版本）组合对照；录制宿主形态对齐 crawler（后台轮询 HostedService）。
- **mid→roomId 解析先例**：`LiveStatusJob.RunOnceAsync`（`GetMidsAsync(type)` → `GetLatestRoomIdByMidAsync` → 空则跳过）原样复刻到 recording 订阅监听。
- **开播检测先例**：`LiveStatusJob.CrawlAsync` 的 `GetLiveRoomInfoAsync`（cookie checkout + room get_info + LiveStatus==1）语义复刻；recording 不读 `LiveStatusRecord` 落库结果，自行轮询（状态机不共享，避免与弹幕订阅耦合）。
- **分布式锁范本**：crawler/cookie 的 `RedisDistributedLockProvider`（LockTake/LockRelease token 语义）为基底，扩展续期与 fencing。
- **锁 key 约定**：既有 `lock:` 前缀（crawler `lock:crawl:*`），录制锁用 `recording:lock:{roomId}`（提案既定）。
- **前端先例**：`NotificationPage`/`CookiePage` 的列表 + 轮询刷新形态对照录制状态页；`SubscriptionPage` 类型下拉直接加项。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash）与 Conventional Commits：

- 本需求开发全程只使用需求总分支 `feat/live-recording`：契约、实现、测试、文档全部直接在该分支提交，不切开发子分支（沿用近两期需求既定做法）。
- 需求总分支已推送 origin，需求总 PR 已存在（#89），提交照常推送更新分支。
- 提交信息使用 Conventional Commits：契约 `feat:`、测试先行 `test:`、实现 `feat:`、工程接入 `chore:`、文档 `docs:`；测试与实现分开提交。
- 并行 subagent 按子项目划分（recording 服务 / api / apps-web），共享文件（common 实体、`Fakes.cs`、测试 csproj、契约）归入主串行线先行，避免并行冲突（见第 4 节）。

## 3. 实施阶段

按「契约先行 → TDD 红-绿 → 工程接入 → 收口验证」推进。Phase 0 串行先行；Phase 1 测试先行（设施 1.1 先行，其余按子项目并行）；Phase 2 common（2.1）串行先行，随后 recording 服务 / api / web 三线并行；Phase 3 与实现可部分并行；Phase 4 串行收口。

### Phase 0: 契约先行

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | 新建 `contracts/recording.openapi.yaml` v1.0.0：① `GET /health`；② `GET /api/recording/status`（200：`items[]`，item 含 `roomId/state ∈ not_live\|recording\|error/hostName/sessionTitle/files[]/updatedAt`，与提案 §3.1 逐字段一致）；③ `GET /api/{roomId}/stream/audio`（200 `video/x-flv` chunked；404 未在录制）；④ `GET /api/{roomId}/recordings`（200 `{files: string[]}`，空数组而非错误）；⑤ `GET /api/{roomId}/recordings/{fileName}`（200 `application/octet-stream` + `Content-Disposition`；404） | `contracts/recording.openapi.yaml` | `npx openapi-typescript` 可成功生成类型；5 端点齐备 |
| 0.2 | `contracts/bilibili-crawler.openapi.yaml` 追加：① `GET /api/b/live/recording-status`（聚合列表，item 同 0.1② + `mid`/主播昵称字段按 backstage 聚合先例）；② `GET /api/b/live/{roomId}/recordings`（代理列表）；③ `GET /api/b/live/{roomId}/recordings/{fileName}`（代理下载）；④ `GET /api/b/live/{roomId}/live-stream/audio`（代理音频流，200/404）；⑤ subscribe/unsubscribe 的 `type` 枚举与 400 描述追加 `recording`（:66/:76/:79/:84 四处） | `contracts/bilibili-crawler.openapi.yaml` | codegen 通过；枚举含 recording |

### Phase 1: 测试先行（红）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | 测试设施（串行先行）：tests csproj 追加 recording 项目引用；`Fakes.cs` 新增 `FakeCookieServiceClient`（可注入 checkout 结果/异常序列，recording 侧精简接口）、`FakeBiliLiveApi`（可注入开播状态/场次名/playurl 结果）；`MongoDbFixture`/`RedisFixture` 沿用 | `tests/Z.Vtuber.Tests/Z.Vtuber.Tests.csproj`、`tests/Z.Vtuber.Tests/Fakes.cs` | 编译通过 |
| 1.2 | `RecordingFileNameTests`：① 模板渲染 `[RAW][123][某主播] 2026-09-13-晚间歌回.flv` 形态（encode 大写/日期格式/场次拼接）；② sanitize 剔除 `\/:*?"<>\|` 与控制字符；③ 超长场次名裁剪不截断关键信息；④ RAW/AV1 仅编码标识不同 | `tests/Z.Vtuber.Tests/RecordingFileNameTests.cs`（新） | 红 |
| 1.3 | `FfmpegProcessBuilderTests`：① http_stream 输入时 `-headers` 注入 Cookie/Referer/User-Agent；② m3u8 输入直接作为 `-i` 且无 headers 强制要求；③ 三路输出齐备：RAW `-c copy -f flv` 文件、AV1 `av1_nvenc -rc vbr -spatial-aq 1 -temporal-aq 1 -multipass fullres -b:v {0.9*raw}`、音频 `-map 0:a -vn -f flv pipe:1`；④ 码率 90% 计算与 playurl 元数据缺失时回退 `DefaultBitrateKbps`；⑤ `Av1:Enabled=false` 时跳过二压输出 | `tests/Z.Vtuber.Tests/FfmpegProcessBuilderTests.cs`（新） | 红 |
| 1.4 | `AudioStreamHubTests`：① 无订阅者时写入直接丢弃零缓冲；② 订阅者收到有序 chunk；③ 慢消费者缓冲达上限（256KB）后丢弃未发送数据并跳过、不阻塞广播与其他订阅者；④ 取消订阅释放资源；⑤ 内存占用有确定上界（断言 channel 容量语义） | `tests/Z.Vtuber.Tests/AudioStreamHubTests.cs`（新） | 红 |
| 1.5 | `RecordingLockTests`（Redis 集成）：① 获取/释放后他人可获取；② 持锁期间他人获取失败（waitTimeout=0 立即返回 null）；③ 续期保持锁超 TTL 不失效；④ 崩溃不续期 TTL 15s 自动过期、他人接管；⑤ fencing：锁被他人持有后原持有者校验失败；⑥ Lua 释放仅释放自持锁（token 比对）；⑦ 跨实例互斥（两个 provider 实例） | `tests/Z.Vtuber.Tests/RecordingLockTests.cs`（新） | 红 |
| 1.6 | 状态与仓储测试（Mongo 集成）：① `RecordingStatusRepository` upsert（RoomId 键）/全量读/状态迁移字段；② `MongoDbInitializer` 新增 `recording_status` 的 `RoomId` 唯一索引、重复执行幂等 | `tests/Z.Vtuber.Tests/RecordingStatusRepositoryTests.cs`（新）、`MongoDbInitializerTests.cs` | 红 |
| 1.7 | 录制编排测试（mock ffmpeg 进程层 + Redis/Mongo 集成）：① 未开播 → status=not_live；② 开播抢锁成功 → 启动录制、status=recording（hostname/场次/文件名）；③ 抢锁失败跳过本轮；④ ffmpeg 退出（下播）→ status=not_live + 释放锁；⑤ ffmpeg 异常退出 → status=error 含原因；⑥ 持锁期间失锁（fencing 失败）→ 立即终止进程；⑦ 订阅取消后不再录制 | `tests/Z.Vtuber.Tests/RecordingSessionTests.cs`（新） | 红 |
| 1.8 | recording API 测试：① `GET /api/{roomId}/recordings` 列出存储目录文件、无录播空数组；② 下载存在文件 200 + `Content-Disposition`、不存在 404；③ fileName 路径穿越（`../`、含分隔符）一律 400/404；④ 未录制访问 `/stream/audio` 404，录制中 200 + `video/x-flv`；⑤ `/api/recording/status` 响应形状；⑥ `/health` | `tests/Z.Vtuber.Tests/RecordingApiTests.cs`（新） | 红 |
| 1.9 | api 侧测试：① 订阅/退订 type=recording 成功、非法 type 400 文案含 recording；② 录播列表/下载代理转发（stub recording HttpClient），recording 不可达 502；③ 音频代理流式转发不缓冲（`ResponseHeadersRead`）；④ `GET /api/b/live/recording-status` 读 Mongo 聚合返回、recording 不可达不阻断 | `tests/Z.Vtuber.Tests/BilibiliLiveRecordingControllerTests.cs`（新）、`BilibiliSubscriptionControllerTests.cs` | 红 |
| 1.10 | ffmpeg 进程冒烟测试（环境有 ffmpeg 才运行，否则 Skip）：lavfi 生成 5s 测试源 → 迷你录制 → 断言 RAW/AV1 两文件非空、音频 pipe 有数据；AV1 编码器不可用时按降级路径断言仅 RAW + 音频 | `tests/Z.Vtuber.Tests/FfmpegRecordingSmokeTests.cs`（新） | 红（无 ffmpeg 环境标记 Skip 不失败） |
| 1.11 | 前端测试：① `client.test.ts`——录播方法路径（`/api/b/live/recording-status`、`/recordings`、`/live-stream/audio`）；② `SubscriptionPage.test.tsx`——类型下拉含「录播」、创建/取消按 recording 调用；③ `RecordingStatusPage.test.tsx`——三态渲染（未开播/录制中/录制出错）、录制中展示 hostname/场次名/文件名、空态、自动刷新轮询、接口失败错误态不白屏 | `apps/web/src/api/client.test.ts`、`apps/web/src/pages/subscription/SubscriptionPage.test.tsx`、`apps/web/src/pages/recording/RecordingStatusPage.test.tsx`（新） | `npm run test` 红 |

### Phase 2: 实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | common 演进（串行先行）：① `SubscriptionType` 新增 `Recording = 3`（同 `[BsonRepresentation(BsonType.String)]` 形态）；② 新增 `Persistence/Entities/RecordingStatusRecord.cs`（提案 §4.1 全字段：RoomId/State/HostName/SessionTitle/Files/Error/UpdatedAt，日期 `[BsonDateTimeOptions(Kind = Utc)]` 沿用现状形态）；③ 新增 `Persistence/RecordingStatusRepository.cs`（RoomId 键 upsert、全量读）；④ `MongoDbInitializer` 新增 `EnsureRecordingStatusIndexesAsync`（`recording_status` 集合 `RoomId_1` 唯一索引）并挂 `EnsureIndexesAsync`；集合名入 `MongoCollectionNames` | `servers/common/Persistence/Entities/SubscriptionType.cs`、`servers/common/Persistence/Entities/RecordingStatusRecord.cs`（新）、`servers/common/Persistence/RecordingStatusRepository.cs`（新）、`servers/common/Persistence/MongoDbInitializer.cs` | 1.6 转绿；既有测试无回归 |
| 2.2 | recording 服务骨架：`servers/recording/Z.Vtuber.Recording.csproj`（`Microsoft.NET.Sdk.Web`、net10.0、`InternalsVisibleTo Z.Vtuber.Tests`、Aspire.MongoDB 13.4.6 / MongoDB.Driver 3.9.0 / Aspire.Redis 13.4.6 内联精确版本、引用 common）；`Program.cs`（Controllers/OpenApi/HealthChecks/`AddMongoDBClient("mongo")`/`AddRedisClient("messaging")`/`RecordingOptions` `ValidateOnStart`/仓储/服务/HostedService 注册、启动 `EnsureIndexesAsync`、`MapHealthChecks("/health")`，范本 cookie + crawler）；`Configuration/RecordingOptions.cs`（节 `Recording`：`StorageDir`/`PollIntervalSeconds=30`/`LockTtlSeconds=15`/`AudioBufferBytes=262144`/`DefaultBitrateKbps=4000`/`Av1:Enabled`）；`Properties/launchSettings.json`（本地 8083）；`MongoDbHealthCheck` 复制 crawler 模式；slnx 加入项目 | `servers/recording/`（新）、`servers/Z.Vtuber.slnx` | 编译通过；缺配置启动 fail-fast（参照 `StartupDiTests` 先例补断言） |
| 2.3 | 订阅监听与开播轮询：`Subscriptions/RecordingSubscriptionWatcher.cs` + `Live/LiveStatusPoller.cs`（HostedService，30s + 抖动/退避：`GetMidsAsync(SubscriptionType.Recording)` → `GetLatestRoomIdByMidAsync` 解析 roomId（空则跳过并记日志，待 crawler 补抓 basic info）→ cookie checkout + room get_info 判开播/取场次名 → 未开播 upsert status=not_live，已开播交 `RecordingSession`） | `servers/recording/Subscriptions/`、`servers/recording/Live/LiveStatusPoller.cs`（新） | 1.7① 转绿 |
| 2.4 | 流地址解析：`Live/StreamResolver.cs`（playurl v2 展开 protocol/format/codec 组合，排除 hevc，按画质偏好选流；优先 `http_stream+flv`，fallback `http_hls`；返回流地址 + 流形式 + 码率元数据；参考 AyeRecorder `PickStream`）；`Services/CookieServiceClient.cs`（精简同构：checkout + report-invalid，复用 common `CookieCheckoutResult`/`NoAvailableCookieException`） | `servers/recording/Live/StreamResolver.cs`、`servers/recording/Services/CookieServiceClient.cs`（新） | 单测覆盖选流矩阵（1.3④ 联动）；编译通过 |
| 2.5 | 文件名与 ffmpeg 参数构造：`Recording/RecordingFileName.cs`（纯函数 `Render(encode, roomId, anchorName, date, sessionTitle)`，sanitize + 裁剪）；`Recording/FfmpegProcessBuilder.cs`（按 1.3 断言构造多输出参数：http_stream 注入 `-headers`、m3u8 直给、码率 0.9 计算与回退、`Av1:Enabled=false` 跳过二压） | `servers/recording/Recording/RecordingFileName.cs`、`servers/recording/Recording/FfmpegProcessBuilder.cs`（新） | 1.2、1.3 转绿 |
| 2.6 | 录制锁：`Recording/RecordingLock.cs`（基底复刻 crawler `RedisDistributedLockProvider` 的 LockTake/LockRelease token 语义，扩展：持锁后每 5s 续期至 TTL 15s、`CheckOwnershipAsync` fencing 校验、Lua 脚本比对 token 释放、优雅停机主动释放） | `servers/recording/Recording/RecordingLock.cs`（新） | 1.5 转绿 |
| 2.7 | 音频 Hub：`Audio/AudioStreamHub.cs`（每房间一个 Hub；ffmpeg stdout 读取泵广播；每订阅者有界 `Channel<byte[]>`（上限 `AudioBufferBytes`，满则 DropOldest 等价策略丢弃跳过）；无订阅者直接丢弃；慢消费者不阻塞广播） | `servers/recording/Audio/AudioStreamHub.cs`（新） | 1.4 转绿 |
| 2.8 | 场次编排与状态上报：`Recording/RecordingSession.cs`（抢锁 → 选流 → 渲染文件名 → 启动 ffmpeg 多输出进程 → stdout 泵喂 Audio Hub → status=recording upsert（hostname/场次/双文件名）→ 进程退出：下播 status=not_live / 异常 status=error 含原因 → Lua 释放锁；每轮循环前 fencing 校验失锁立即 kill ffmpeg；接管续录按当时日期/场次名渲染新文件，不追加旧文件）+ `Recording/RecordingStatusReporter.cs`（状态迁移 + 周期心跳 upsert） | `servers/recording/Recording/RecordingSession.cs`、`servers/recording/Recording/RecordingStatusReporter.cs`（新） | 1.7 全绿 |
| 2.9 | recording Controllers：`Controllers/RecordingController.cs`（`/api/{roomId}/stream/audio` 未录制 404 / 录制中 200 `video/x-flv` chunked；`/api/{roomId}/recordings` 列 `{StorageDir}/{roomId}/`；`/api/{roomId}/recordings/{fileName}` 下载 + 路径穿越防护——仅允许平文件名、解析后必须落在存储目录内；`/api/recording/status`）+ `/health` | `servers/recording/Controllers/RecordingController.cs`（新） | 1.8 转绿 |
| 2.10 | api 侧改造：① `BilibiliSubscriptionController` 4 处 400 文案追加 recording；② 新增 `Controllers/BilibiliLiveRecordingController.cs`——`/api/b/live/{roomId}/recordings[/{fileName}]` 与 `/live-stream/audio` 流式代理（`HttpCompletionOption.ResponseHeadersRead` + `Stream.CopyToAsync`、音频 `Cache-Control: no-store`、recording 不可达 502），`/api/b/live/recording-status` 读 `recording_status` 聚合（可 join 订阅/昵称，按 backstage 先例内存 join；recording 不可达不阻断）；③ `Program.cs` 注册 recording HttpClient（`RecordingService:BaseUrl`，Aspire 经 `WithReference` 注入） | `servers/api/Controllers/BilibiliSubscriptionController.cs`、`servers/api/Controllers/BilibiliLiveRecordingController.cs`（新）、`servers/api/Program.cs` | 1.9 转绿 |
| 2.11 | crawler 适配（一行）：`AccountInfoJob.RunOnceAsync` 枚举追加 `SubscriptionType.Recording`（注释同步更新），保障录播订阅主播 basic info 周期抓取 | `servers/crawler/Jobs/AccountInfoJob.cs` | 既有 `AccountInfoJob` 测试适配转绿 |
| 2.12 | web 改造（仅依赖 Phase 0 契约，与后端并行）：① `package.json` codegen 追加 recording 契约 → 生成 `schema-recording.d.ts`（勿手改）+ 重新生成 `schema.d.ts`；② `client.ts` 新增 `listRecordingStatus`/`listRecordings`（音频流与下载为直链 URL，不经封装 fetch）；③ `SubscriptionPage.tsx` type 联合与 `TYPE_LABELS` 加「录播」、下拉加项；④ 新增 `pages/recording/RecordingStatusPage.tsx`（状态列表：三态标签 + hostname/场次名/文件名 + 周期轮询刷新 + 录播文件列表/下载入口 + 实时音频入口，风格对齐既有页面）；⑤ `App.tsx` `menuItems` 加「录制状态」 | `apps/web/package.json`、`apps/web/src/api/`、`apps/web/src/pages/subscription/SubscriptionPage.tsx`、`apps/web/src/pages/recording/RecordingStatusPage.tsx`（新）、`apps/web/src/App.tsx` | 1.11 转绿；`npm run lint`/`npm run build` 通过 |

### Phase 3: 工程接入

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 3.1 | Dockerfile 与 CI：`servers/recording/Dockerfile` 两阶段（build 同 crawler，restore 前同拷 common 与 recording csproj；runtime 阶段 `apt-get install -y ffmpeg`——NVENC 支持以部署镜像实测为准，无 GPU 环境靠 `Av1:Enabled=false` 降级）；`EXPOSE 8080`；`.github/workflows/build-recording-image.yml` 复制 crawler workflow，镜像名 `zero-tools/z-vtuber-recording`，paths 含 `servers/recording/**` 与 `servers/common/**` | `servers/recording/Dockerfile`（新）、`.github/workflows/build-recording-image.yml`（新） | 本地 `docker build` 成功；workflow 语法有效 |
| 3.2 | Aspire 编排：`AppHost.cs` 新增 `AddProject<Projects.Z_Vtuber_Recording>()` + `WithReference(mongo/messaging)` + `WithReference(cookie)` + `WithEnvironment("CookieService:BaseUrl", ...)` + 录播存储卷（`WithDataVolume("recordings")` 挂载 `/data/recordings`，与 `RecordingOptions.StorageDir` 一致）+ `WithHttpHealthCheck("/health")`；api 编排 `.WithReference(recording)` + `WithEnvironment("RecordingService:BaseUrl", recording.GetEndpoint("http"))` | `infra/apphost/AppHost.cs` | Aspire 一键拉起含 recording 的全套环境 |
| 3.3 | 部署说明：PR #89 描述补充生产部署要点——recording 单副本 + Recreate 更新策略、持久卷挂载、GPU/NVENC 前置或 `Recording:Av1:Enabled=false` 降级 | PR 描述（仓库外部署清单指引） | 描述完整 |

### Phase 4: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 4.1 | `dotnet test tests/Z.Vtuber.Tests` 全绿（含既有回归）；recording 核心逻辑（文件名/参数构造/音频 Hub/锁/场次编排）覆盖率 ≥ 80%；`npm run test`/`lint`/`build` 全绿；grep 确认：订阅 type 硬编码文案无遗漏、`recording_status` 读写路径齐备 | 全部通过 |
| 4.2 | 编排手工验证（z-aspire-orchestrator）：本地 Aspire 拉起全量 → web 创建「录播」订阅 → 开播 1 分钟内自动录制、状态页转「录制中」展示 hostname/场次/文件名 → `/api/b/live/{roomId}/live-stream/audio` 实时可听 → 下播产出 RAW + AV1 双文件、列表/下载可用 → 重启 recording 容器验证接管（旧实例释放锁、新实例 ≤30s 接管、无双写）→ 无 GPU 环境 `Av1:Enabled=false` 降级仅产 RAW | 手工验证记录 |
| 4.3 | AGENTS.md 同步：新建 `servers/recording/AGENTS.md`（技术栈/模块结构/API 面/配置表/开发流程，对齐 crawler/cookie 格式）；更新 `servers/common/AGENTS.md`（SubscriptionType.Recording、recording_status 实体/仓储）、`servers/api/AGENTS.md`（录播代理与状态聚合端点）、`servers/crawler/AGENTS.md`（AccountInfoJob 覆盖 Recording）、`apps/web/AGENTS.md`（录制状态页、schema-recording.d.ts）、`docs/engineering/backend-rules.md` 项目结构图补 recording 服务 | 文档与实现一致 |

## 4. 并行策略

- Phase 0（契约 0.1/0.2）串行先行——recording 控制器、api 代理、前端 codegen 均依赖契约。
- Phase 1：1.1（测试设施）串行先行；随后后端测试（1.2-1.10）与前端测试（1.11）互不依赖，可拆 subagent 并行。
- Phase 2：2.1（common）串行先行——三线均依赖 common 实体；2.1 完成后 recording 服务线（2.2 → 2.3/2.4 → 2.5-2.9 大体串行）、api 线（2.10/2.11）、web 线（2.12）分属不同子项目，按 z-coding 规范拆 subagent 并行，各 subagent 负责「实现 → 提交 → 测试修复 → 提交」闭环。
- Phase 3（3.1-3.3）与 Phase 2 互不依赖可并行。
- Phase 4 串行收口。全部提交在需求总分支 `feat/live-recording`。

## 5. 验收对照

| PRD 功能点 | 实现任务 | 验证方式 |
|-----------|---------|---------|
| 功能点 1：录播类型订阅（mid→roomId 和弹幕一致） | 0.2⑤、2.1①、2.3、2.10①、2.11、2.12③ | 1.9①、1.11② + 手工验证（4.2） |
| 功能点 2：开播自动录制 + RAW/AV1 双版本 | 2.3、2.4、2.5、2.8 | 1.3、1.7、1.10 + 手工验证（4.2） |
| 功能点 3：文件名命名规范 | 2.5 | 1.2 |
| 功能点 4：录制状态页（三态 + hostname/场次/文件名） | 0.1②、0.2①、2.1②-④、2.8、2.10②、2.12④⑤ | 1.6、1.8⑤、1.9④、1.11③ + 手工验证（4.2） |
| 功能点 5：录播文件浏览与下载 | 0.1④⑤、0.2②③、2.9、2.10② | 1.8①-③、1.9② + 手工验证（4.2） |
| 功能点 6：实时音频流（404/缓冲上界/慢消费者丢弃） | 0.1③、0.2④、2.7、2.9、2.10② | 1.4、1.8④、1.9③ + 手工验证（4.2） |
| 功能点 7：单实例录制与无缝接管 | 2.6、2.8、3.2、3.3 | 1.5、1.7③⑥ + 重启接管手工验证（4.2） |
| 非功能：核心逻辑覆盖率 ≥ 80%、全量测试绿 | 4.1 | `dotnet test` / `npm run test` |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| GPU/NVENC 可用性 | AV1 二压依赖 `av1_nvenc`，本地/CI/部分部署环境无 NVIDIA GPU | `Recording:Av1:Enabled=false` 降级仅产 RAW + 音频（提案 §9），状态页可标注；冒烟测试 1.10 覆盖降级路径 |
| ffmpeg 环境依赖 | 录制与冒烟测试依赖外部 ffmpeg 进程 | Dockerfile runtime 安装 ffmpeg（3.1）；测试环境缺失时冒烟测试 Skip 不失败（1.10） |
| mid→roomId 解析缺口 | 纯录播订阅主播无 basic info 则录制永不触发（提案未覆盖） | crawler `AccountInfoJob` 枚举追加 Recording（2.11，一行）；recording 解析失败跳过并记日志，下轮重试（2.3） |
| 锁续期复杂度 | 长时持锁需续期 + fencing，实现错误会导致双写或录制卡死 | 1.5 锁定续期/TTL 接管/fencing/跨实例互斥全矩阵；fencing 失锁立即 kill ffmpeg（2.8）；单副本 Recreate 部署为第一道保障（3.3） |
| 音频内存上界 | 慢消费者/多订阅者可能撑大内存 | 每订阅者有界 Channel + DropOldest（1.4③⑤ 锁定上界语义）；无订阅者零缓冲 |
| 代理大文件下载 | api 流式转发录播文件可能占连接 | `ResponseHeadersRead` + `CopyToAsync` 不缓冲（1.9③）；文件仅经内网转发 |
| 订阅 type 硬编码文案 | 控制器 400 文案与契约描述散点多处，易漏 | 0.2⑤ 与 2.10① 列明 4+4 处；4.1 grep 兜底 |
| 依赖 | StackExchange.Redis（经 Aspire.StackExchange.Redis 13.4.6）、Testcontainers mongo/redis 夹具、openapi-typescript ^7.13.0、部署镜像 ffmpeg（含 NVENC 构建） | 已核实（1.0）；ffmpeg/NVENC 以部署实测为准（3.1） |

## 7. 相关文档

- PRD：`docs/product/draft/20260913-live-recording-prd.md`（v1.0）
- 技术提案：`docs/engineering/proposals/20260913-proposal-live-recording.md`
- 契约：`contracts/recording.openapi.yaml`（Phase 0 新建 v1.0.0）、`contracts/bilibili-crawler.openapi.yaml`、`contracts/cookie.openapi.yaml`
- 新服务工程范本 Plan：[[20260911-plan-cookie-service]]、[[20260911-plan-notifier-milky]]
- 参考项目：[Zast.AyeRecorder](https://github.com/Deliay/Zast/tree/main/Zast.AyeRecorder)（录制与存储参考）
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`docs/engineering/frontend-rules.md`、`servers/common/AGENTS.md`、`servers/api/AGENTS.md`、`servers/crawler/AGENTS.md`、`apps/web/AGENTS.md`
