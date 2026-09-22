---
description: Transcript 展示优化实现 Plan——基于 PRD v1.0 与技术提案（feat/transcript-display-optimization）的契约先行与任务分解：四条实施线（试听链路 recording 音频片段端点 + api 代理、字幕链路 transcript-api SRT/ASS 渲染下载 + Content-Disposition 透传、提交上下文日志三落点、web 试听控件与下载按钮），TDD 测试先行，ffmpeg 抽取片段转 mp3、SubtitleRenderer 纯函数 golden 断言、日志脱敏红线断言
type: Permanent
---

# Transcript 展示优化 - 实现 Plan

**功能名称**: Transcript 展示优化
**关联 PRD**: `docs/product/draft/20260922-transcript-display-optimization-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20260922-proposal-transcript-display-optimization.md`（v1.0）
**Plan 版本**: v1.0
**创建日期**: 2026-09-22
**作者**: Workflow Agent（代起草）
**feat-branch**: `feat/transcript-display-optimization`（需求总分支，已推送 origin；需求总 PR：<https://github.com/Deliay/z-vtuber/pull/129>）

## 1. 概述

本 Plan 基于 PRD v1.0 与技术提案 v1.0 编写，指导「Transcript 展示优化」的编码实施。改动分四条线：① 试听链路——recording 新增 `GET /api/{roomId}/recordings/{fileName}/audio?startMs&endMs`（ffmpeg 输入 seek 抽取片段转 mp3 流式输出）+ api 代理镜像 + web 共享 `<audio>` 试听控件；② 字幕链路——transcript-api 新增 `GET /api/transcripts/task/{taskId}/subtitle?format=srt|ass`（`SubtitleRenderer` 纯函数渲染、UTF-8 BOM、文件名 sanitize + `filename*`）+ api 代理 `ForwardAsync` 补 `Content-Disposition` 透传 + web「下载 SRT / 下载 ASS」按钮；③ 提交上下文日志——run-task 提交打印**具体**上下文全文（组装完成处同样打印，覆盖 ASR 连接失败路径）、入队创建打印提交信息（手动/自动同字段），全程不打印凭据；④ 契约先行——3 份 OpenAPI 变更 + web codegen 后再动实现。实施全程遵循 `docs/engineering/common-rules.md`（契约先行、TDD、核心逻辑覆盖率 ≥ 80%）、`backend-rules.md`、`frontend-rules.md` 与各服务 `AGENTS.md`。

### 1.0 上游核实结论（代码现状复查）

对需求总分支 `feat/transcript-display-optimization` 代码逐项核实，提案技术路径**可行**，引用与现状描述全部属实；下列偏差点/细节在对应任务中显式处理：

- **提案行号引用核实无误**：recording 下载端点 `RecordingController.cs:116-145`（`IsSafeFileName` → `Path.GetFullPath` 前缀校验 → `GetRecordingFilesAsync` 录制中 400 → `File.Exists` 404 → `PhysicalFile(enableRangeProcessing: true)`）顺序与提案一致；api 下载代理 `BilibiliLiveRecordingController.cs:126-222`（Range/If-Range 透传、400 透传响应体、404/416/502 口径）可直接作为 audio 代理模板；`RecordingTranscriptProxyController.ForwardAsync`（:73-130）确认只透传 `Content-Type`/`Cache-Control`，**无** `Content-Disposition`；run-task 日志 `AliFunAsrService.cs:128-133` 确认只打 `contextLines` 条数；`TranscriptContextBuilder.MaxContextChars = 400`/`MaxScRecords = 500`（:24/:27）；上下文组装落点 `TranscriptTaskRunner.cs:97-111`；手动入队 `TranscriptTasksController.Enqueue`（:24-88）确认未注入 `ILogger`；自动建任务 `TranscriptEnqueueService.EnsureTaskAsync`（:164-205）确认无创建 Info 日志；web `client.ts:145-151`（`recordingAudioUrl`/`recordingDownloadUrl`）与 `TaskListTab.tsx:383-411`（识别结果标题行 + `formatPosition` 片段列表）现状与提案一致。
- **`AliFunAsrService` 已具备阶段/统计基础设施（20260918 交付）**：`AsrPhase`/`LogFailure`/`RunStats`/`MaxRawLogLength = 2048` 与 `TruncateRaw`（`AliFunAsrService.cs:40/389-390`）均已存在。实现点 6 的「`TruncateRaw` → `TruncateForLog` 重命名」会触碰既有调用点（`LogFailure` 内 `raw=TruncateRaw(json)` :284），必须同步改并跑 `AliFunAsrErrorLogTests` 回归，防行为漂移。
- **web 任务 DTO 字段齐备，无需扩契约数据结构**：codegen 产物 `schema-recording-transcript.d.ts` 的 `TranscriptTask` 已含 `roomId`/`title`/`file`/`modelName`（试听取音频与下载文件名所需字段全有），`TranscriptSegment` 含 `start`/`end`/`text`。
- **测试设施现状**：`FakeFunAsrServer` 已独立为 `tests/Z.Vtuber.Tests/FakeFunAsrServer.cs`（提案 §7 未指明位置，按现状文件使用）；捕获式 `ListLogger<T>` 在 `RecordingTranscriptFakes.cs:131`；Range 回归基线在既有 `RecordingApiTests.cs`；`TranscriptSegmentRepository.ListByTaskAsync`（按 `StartTime` 升序）已存在可直接复用。
- **复制粘贴风险点**：api 下载代理对 `Content-Disposition` 有「上游没有则补 `attachment`」的兜底（`BilibiliLiveRecordingController.cs:195-202`）。audio 代理**不得**带此兜底——片段为 `<audio>` 内联播放，响应不设 `Content-Disposition`（任务 1.2 以测试断言锁死）。

### 1.1 实施范围

- **做**：
  - `contracts/`：`recording.openapi.yaml` + 音频片段端点；`recording-transcript.openapi.yaml` + 字幕下载端点；`bilibili-crawler.openapi.yaml` + 两条代理镜像；`apps/web` `npm run codegen` 重新生成（勿手改）。
  - `servers/recording`：`RecordingController` + audio action（文件解析抽私有方法与下载共用）、`Audio/AudioClipExtractor`（`IAudioClipExtractor`，ffmpeg `-ss`/`-t` → mp3 `pipe:1`）、`RecordingOptions` + `AudioClipMaxDurationMs`（默认 300000）、启动 mp3 编码探测与 WAV 降级。
  - `servers/api`：`BilibiliLiveRecordingController` + audio 代理 action；`RecordingTranscriptProxyController.ForwardAsync` + `Content-Disposition` 透传。
  - `servers/recording-transcript-api`：`Subtitles/SubtitleRenderer`（SRT/ASS 纯函数）+ `Subtitles/SubtitleFileName`、`Controllers/TranscriptSubtitleController` 字幕下载端点、`TranscriptTasksController` 入队 Info 日志。
  - `servers/recording-transcript-consumer`：run-task 提交打印具体上下文全文（`TruncateRaw` → `TruncateForLog`）、`TranscriptTaskRunner` 上下文组装完成 Info、`TranscriptEnqueueService` 入队创建 Info。
  - `apps/web`：`client.ts` + `recordingAudioClipUrl`/`transcriptSubtitleUrl`、`SegmentAudioPlayer`（共享 `<audio>` 单例）+ `TaskListTab` 识别结果面板改造 + 下载按钮。
  - `tests/Z.Vtuber.Tests` 与 `apps/web` Vitest 测试（清单见 §3 Phase 1）；5 份 `AGENTS.md` 文档同步。
- **不做**：直播进行中场次回放；跨句连续播放；字幕样式配置与 vtt 格式；字幕/音频缓存预生成；识别上下文持久化；录播下载链路 Range 能力零改动（锁定回归）；crawler/notifier/cookie/common 实体零改动；无新增 NuGet/npm 依赖。

### 1.2 现状基线

- **recording 端点范本**：`RecordingController.DownloadRecording`（:116-145）与 `RecordingFakes`/`RecordingApiTests`（录制中判定、路径穿越、Range 回归）。
- **api 代理范本**：`BilibiliLiveRecordingController.DownloadRecording`（:126-222，状态映射/流式转发）与 `BilibiliLiveRecordingControllerTests`。
- **transcript-api 端点范本**：`TranscriptTasksController`（`ObjectId.TryParse` 404、`ErrorResponse`）与 `TranscriptApiTests`。
- **进程测试范本**：`FfmpegRecordingSmokeTests`（lavfi 造音频、无 ffmpeg 环境 Skip）。
- **日志断言范本**：`AliFunAsrErrorLogTests`（`ListLogger` 捕获 + 消息包含断言）。
- **ASR 契约测试设施**：`FakeFunAsrServer`（本地假 websocket server）。
- **web 测试范本**：`TaskListTab.test.tsx`、`client.test.ts`（URL 构造器断言模式）。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash、分支不复用）与 Conventional Commits：

- 本需求开发全程只使用需求总分支 `feat/transcript-display-optimization`：契约、实现、测试、文档全部直接在该分支提交，不切开发子分支（沿用前两期需求既定做法）。
- 需求总分支已推送 origin，需求总 PR 已存在（#129），提交照常推送更新分支。
- 提交信息使用 Conventional Commits：契约先行 `feat(contracts):`、测试先行 `test:`、实现 `feat:`、文档 `docs:`；测试与实现分开提交；scope 使用 `transcript`/`recording`/`web` 等既有口径。

## 3. 实施阶段

按「契约先行（0）→ TDD 测试先行（红）→ 实现（绿）→ 收口验证」推进。Phase 0 串行先行；Phase 1/2 按四条线并行（线内串行）；Phase 3 串行收口。

### Phase 0: 契约先行（串行，全部实现的联调基线）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | `recording.openapi.yaml` + `GET /api/{roomId}/recordings/{fileName}/audio`：query `startMs`/`endMs`（int64 必填，`endMs > startMs`、区间 ≤ `AudioClipMaxDurationMs`）；200 `audio/mpeg`（`Cache-Control: no-store`，不承诺 Range）/400/404/500 `{"error": "audio clip failed"}`；行为矩阵按提案 §3.1（`endMs` 超文件长度输出到结尾、录制中 400、忽略 `Range` 头） | `contracts/recording.openapi.yaml` | 契约自洽，与提案 §3.1 一致 |
| 0.2 | `recording-transcript.openapi.yaml` + `GET /api/transcripts/task/{taskId}/subtitle`：query `format`（`srt` 默认 \| `ass`，其他 400）；200 含 `Content-Type`（`application/x-subrip; charset=utf-8` / `text/x-ass; charset=utf-8`）与 `Content-Disposition`（`attachment; filename="<ascii>.<ext>"; filename*=UTF-8''...`）；404 `{"error": "task not found"}`；任意状态任务可下载（RUNNING 为已入库片段、无片段为合法空字幕） | `contracts/recording-transcript.openapi.yaml` | 契约自洽 |
| 0.3 | `bilibili-crawler.openapi.yaml` + 两条代理镜像：`GET /api/b/live/{roomId}/recordings/{fileName}/audio`（query 原样透传，200/400/404 透传、不可达 502）与 `GET /api/transcripts/task/{taskId}/subtitle`（200 含 `Content-Disposition` 透传 / 400 / 404 / 502 现状口径） | `contracts/bilibili-crawler.openapi.yaml` | 与 0.1/0.2 语义一致 |
| 0.4 | web codegen：`npm run codegen` 重新生成 `schema.d.ts`、`schema-recording-transcript.d.ts`（勿手改）；提交契约 + codegen 作为评审基线 | `apps/web/src/api/schema*.d.ts` | 生成物与契约一致；`npm run lint` 通过 |

### Phase 1: 测试先行（红）

**线 A：试听链路（recording + api 代理）**

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | `RecordingAudioClipTests`（新）：lavfi 生成 5s 正弦音频 → `startMs=1000&endMs=2500` 返回 200 `audio/mpeg` 且输出时长 ≈ 1.5s（ffprobe 断言）；`endMs` 超文件长度 → 200 输出到结尾；`endMs <= startMs`/`startMs < 0`/区间超上限 → 400；文件不存在 → 404；录制中 → 400（复用录制中判定口径）；非法文件名 → 400、路径穿越 → 404；带 `Range` 头 → 忽略返回 200 整片段；无 ffmpeg 环境 Skip（沿用 `FfmpegRecordingSmokeTests` 模式） | `tests/Z.Vtuber.Tests/RecordingAudioClipTests.cs` | 红（端点不存在） |
| 1.2 | `BilibiliLiveRecordingControllerTests` 扩展（新）：audio 代理 query 透传、200 流式透传 `Content-Type`（`audio/mpeg`）、**不设置 `Content-Disposition`**（对照下载代理兜底行为显式断言）、400 透传响应体、404、recording 不可达 502 | `tests/Z.Vtuber.Tests/BilibiliLiveRecordingControllerTests.cs` | 红 |

**线 B：字幕链路（transcript-api + api 透传）**

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.3 | `SubtitleRendererTests`（新，golden 断言）：SRT 时间 `HH:MM:SS,mmm`（小时两位起、超 99 小时进位）、条目结构与序号从 1 起、文本换行原样保留、`end <= start` 异常片段按原值渲染；ASS 时间 `H:MM:SS.cc`（厘秒**向下取整**）、`[Script Info]`/`[V4+ Styles]`/`[Events]` 文件头与 `Style: Default` 字段逐项 golden、转义 `{`→`\{`、`}`→`\}`、换行→`\N`；UTF-8 **BOM** 断言；空片段：SRT 空串、ASS 仅文件头 | `tests/Z.Vtuber.Tests/SubtitleRendererTests.cs` | 红 |
| 1.4 | `TranscriptSubtitleApiTests`（新）：预置多片段任务（含毫秒级非整秒时间）→ SRT/ASS 各 golden 文本与 `Content-Type`、`Content-Disposition` 含 `filename*`（中文场次标题场景）；`format=ssa` → 400；taskId 非法/不存在 → 404；无片段任务 → 200 合法空字幕；RUNNING 任务 → 已入库片段渲染；文件名 sanitize/兜底（`SubtitleFileName`：特殊字符折叠 `_`、空段兜底 `transcript-{taskId 前 8 位}`）单测 | `tests/Z.Vtuber.Tests/TranscriptSubtitleApiTests.cs`、`tests/Z.Vtuber.Tests/SubtitleFileNameTests.cs` | 红 |
| 1.5 | `RecordingTranscriptProxyControllerTests` 扩展（新）：上游带 `Content-Disposition` → 透传；不带 → 不设置；既有 JSON/SSE 端点行为不回归；字幕代理 200/400/404/502 | `tests/Z.Vtuber.Tests/RecordingTranscriptProxyControllerTests.cs` | 红 |

**线 C：提交上下文日志（consumer + transcript-api）**

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.6 | `TranscriptSubmitLogTests`（新）：手动 `Enqueue` 建任务成功 → Info 含 `taskId/recordingId/roomId/file/modelId/mode/source=manual`；409 幂等命中 → Debug；自动 `EnsureTaskAsync` 建任务成功 → 同字段 Info（`mode=live|file`、`source=auto`）；**所有日志断言不含模型 apiKey 明文** | `tests/Z.Vtuber.Tests/TranscriptSubmitLogTests.cs` | 红 |
| 1.7 | `AliFunAsrContextLogTests`（新，`FakeFunAsrServer` + `ListLogger`）：3 条上下文 → run-task 提交日志含每条上下文原文与 `contextLines=3`/`contextChars`；空上下文 → 明确「空上下文」记录；超长上下文（>2KB 口径）→ 截断标记 `...(truncated)` 且不含完整原文；任务启动组装完成 Info（`lines`/`chars`/`context=`）；**日志不含 apiKey 明文** | `tests/Z.Vtuber.Tests/AliFunAsrContextLogTests.cs` | 红 |

**线 D：web（Vitest + RTL）**

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.8 | web 测试（新/扩展）：`recordingAudioClipUrl`/`transcriptSubtitleUrl` URL 构造器断言（含 `encodeURIComponent` 与 query）；片段行点击播放 → mock `HTMLAudioElement.src` 为带 `startMs/endMs` 的片段 URL 并 `play()`；点击另一句切源；再点同一句停止；`error` 事件 → 「音频暂不可用」；`task.file` 为空 → 播放按钮置灰；切换任务/「收起」/卸载 → `pause()` 且清空 `src`；「下载 SRT/ASS」href 正确、无片段置灰（`aria-disabled` + title） | `apps/web/src/api/client.test.ts`、`apps/web/src/pages/transcripts/TaskListTab.test.tsx`、`apps/web/src/pages/transcripts/SegmentAudioPlayer.test.tsx` | 红 |

### Phase 2: 实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | **recording 音频片段端点**：① `RecordingOptions` + `AudioClipMaxDurationMs`（默认 300000，保持 `ValidateOnStart` 语义）；② `Audio/AudioClipExtractor`（`IAudioClipExtractor.ExtractAsync(fullPath, startMs, endMs, output, ct)`，注册 DI）：`ffmpeg -hide_banner -loglevel error -ss {startSec} -t {durSec} -i {fullPath} -vn -ac 1 -ar 44100 -b:a 96k -f mp3 pipe:1`——`-ss`/`-t` 置于 `-i` 之前（输入 seek 秒开）、直写 `Response.Body` 不落盘、`ct`/`ResponseAborted` 取消 kill 进程并回收（沿用 `RecordingProcess` 口径）、stderr 泵丢弃、`exitCode != 0` 且零字节 → 500 `{"error": "audio clip failed"}`；③ `RecordingController` + `GET {roomId:long}/recordings/{fileName}/audio`：文件解析抽私有方法与 `DownloadRecording` 共用（`IsSafeFileName` → 前缀校验 → 录制中 400 → `File.Exists` 404 顺序不变），参数校验 `startMs >= 0`/`endMs > startMs`/区间 ≤ 上限任一不满足 400，响应 `Content-Type: audio/mpeg` + `Cache-Control: no-store`，`endMs` 超文件长度自然输出到结尾不报错，忽略 `Range` 头；④ 启动探测 `ffmpeg -encoders` 是否含 libmp3lame，缺失记 Warn 并降级 `-f wav`（`audio/wav`，语义不变）；⑤ `build-recording-image.yml` 增加 `ffmpeg -encoders \| grep mp3` 构建期断言 | `servers/recording/Controllers/RecordingController.cs`、`servers/recording/Audio/AudioClipExtractor.cs`、`servers/recording/Configuration/RecordingOptions.cs`、`servers/recording/Program.cs`、`.github/workflows/build-recording-image.yml` | 1.1 转绿 |
| 2.2 | **api 代理两处**：① `BilibiliLiveRecordingController` + `[HttpGet("{roomId:long}/recordings/{fileName}/audio")]`：构造上游 `api/{roomId}/recordings/{Uri.EscapeDataString(fileName)}/audio` + `Request.QueryString` 透传，`ResponseHeadersRead` + `CopyToAsync` 流式转发；状态映射与 `DownloadRecording` 同口径（404→404、400→透传响应体、不可达/超时→502），成功仅透传 `Content-Type` 与 `Cache-Control`，**不设 `Content-Disposition`**、不透传 Range 语义；② `RecordingTranscriptProxyController.ForwardAsync`（:73-130）补 `Content-Disposition` 透传（上游 `response.Content.Headers.ContentDisposition` 存在时写入，既有 JSON/SSE 端点不携带该头不受影响） | `servers/api/Controllers/BilibiliLiveRecordingController.cs`、`servers/api/Controllers/RecordingTranscriptProxyController.cs` | 1.2、1.5 转绿 |
| 2.3 | **字幕渲染与下载端点（transcript-api）**：① `Subtitles/SubtitleRenderer` 纯函数（`RenderSrt`/`RenderAss`/`FormatSrtTimestamp`/`FormatAssTimestamp`，格式细节即契约，按 1.3 golden 实现：SRT 序号 + `HH:MM:SS,mmm` + 空行分隔、文本换行保留；ASS v4+ 文件头（`ScriptType: v4.00+`、`PlayResX: 1920`、`PlayResY: 1080`、`WrapStyle: 2`、`ScaledBorderAndShadow: yes`）+ `Style: Default`（`Fontname: Microsoft YaHei`、`Fontsize: 64`、`PrimaryColour: &H00FFFFFF`、`OutlineColour: &H00000000`、`Outline: 2`、`Alignment: 2`、`MarginV: 60`）+ `Dialogue: 0,0:MM:SS.cc,...`，厘秒向下取整，转义 `\{`/`\}`/`\N`；统一 UTF-8 with BOM）；② `Subtitles/SubtitleFileName.Build(title, modelName, taskId, extension)`：`{sanitize(标题)}-{sanitize(模型名)}.{ext}`，sanitize 仅保留中英文/数字/`-`/`_` 其余折叠 `_`，空段兜底 `transcript-{taskId 前 8 位}`；③ `Controllers/TranscriptSubtitleController`：`GET /api/transcripts/task/{taskId}/subtitle`——`format` 枚举校验（默认 srt）→ `ObjectId.TryParse` 失败/任务不存在 404 → `segments.ListByTaskAsync(taskId)`（升序）→ 渲染 → `File(Encoding.UTF8.GetPreamble() + bytes, contentType, fileDownloadName)`（框架生成 `Content-Disposition` 与 `filename*`）；任意状态可下载、无片段返回合法空字幕 | `servers/recording-transcript-api/Subtitles/SubtitleRenderer.cs`、`servers/recording-transcript-api/Subtitles/SubtitleFileName.cs`、`servers/recording-transcript-api/Controllers/TranscriptSubtitleController.cs` | 1.3、1.4 转绿 |
| 2.4 | **提交上下文日志（三落点 + 脱敏）**：① `AliFunAsrService` run-task 提交日志（:128-133）扩展 `contextChars` 与 `context=` 具体上下文全文（`\n` 连接各上下文行；空上下文明确「空上下文」）；`TruncateRaw` 重命名 `TruncateForLog`（含 `LogFailure` 内 :284 调用点同步），2KB 截断口径不变；② `TranscriptTaskRunner`（:97-111）`BuildAsync` 返回后 Info `transcript context assembled: taskId=... lines=... chars=... context=...`（同截断口径，覆盖 ASR 连接失败路径）；③ `TranscriptTasksController` 注入 `ILogger<>`，`Enqueue` 建任务成功后 Info `transcript task enqueued: taskId=... recordingId=... roomId=... file=... modelId=... mode=file source=manual`，409 幂等记 Debug；④ `TranscriptEnqueueService.EnsureTaskAsync` 建任务成功后同字段 Info（`mode=live|file`、`source=auto`）；⑤ 脱敏红线：只输出上下文文本与任务元信息（模型仅 `Id`/`Name`/`Model`），**绝不输出 `transcript_models.ApiKey`** | `servers/recording-transcript-consumer/Asr/AliFunAsrService.cs`、`servers/recording-transcript-consumer/Tasks/TranscriptTaskRunner.cs`、`servers/recording-transcript-api/Controllers/TranscriptTasksController.cs`、`servers/recording-transcript-consumer/Enqueue/TranscriptEnqueueService.cs` | 1.6、1.7 转绿；`AliFunAsrErrorLogTests` 回归绿 |
| 2.5 | **web 试听控件与下载按钮**：① `client.ts` + `recordingAudioClipUrl(roomId, fileName, startMs, endMs)`/`transcriptSubtitleUrl(taskId, format)`（与 `recordingDownloadUrl` 同模式，`encodeURIComponent` + `URLSearchParams`）；② `SegmentAudioPlayer.tsx`（抽离，保持 Tab 文件体量）：共享单例 `<audio>`（`useRef<HTMLAudioElement>`，同一时刻只播一句）+ 统一播放控件（当前播放句起止时间 + 文字摘要、进度、停止）；③ `TaskListTab.tsx` 识别结果面板改造（:383-411）：标题行（与「收起」并排）+「下载 SRT」「下载 ASS」（`<a href={transcriptSubtitleUrl(...)} download>`，`segments.length === 0` 时 `aria-disabled` + title「暂无识别结果」置灰）；片段行前加「播放/停止」按钮（点击设 `audio.src` 并 `play()`，当前句以 `segment.start` 为键高亮，再点同句停止、点他句切源）；时间展示复用 `formatPosition`；④ 生命周期：`ended`/`pause` 清高亮；切任务/「收起」/卸载 `pause()` 并清空 `src`；⑤ 错误态：`error` 事件或 `play()` 失败（400 录制中/404 文件不存在）→ 「音频暂不可用」提示不阻断；`task.file` 为空 → 播放按钮置灰 + 提示 | `apps/web/src/api/client.ts`、`apps/web/src/pages/transcripts/SegmentAudioPlayer.tsx`、`apps/web/src/pages/transcripts/TaskListTab.tsx` | 1.8 转绿 |
| 2.6 | codegen 一致性复核 + `npm run lint`/`npm run build`、`dotnet build` 全通过 | 全部 | 构建零告警 |

### Phase 3: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 3.1 | 全量测试与覆盖率：`dotnet test tests/Z.Vtuber.Tests` 全绿（含既有回归：`RecordingApiTests` Range 206/416、`AliFunAsrErrorLogTests`/`AliFunAsrOffsetTests` ASR 分阶段失败日志、SSE/任务链路测试）；`apps/web` `npm run test` 全绿；`SubtitleRenderer`/`SubtitleFileName`/音频参数校验/`TruncateForLog` 核心逻辑覆盖率 ≥ 80%（common-rules） | 全部通过 |
| 3.2 | 安全与红线 grep：新增日志拼装处不含 `_apiKey`/`ApiKey`/`Authorization`；日志断言不含 apiKey 明文；字幕文件名 sanitize 后无 CR/LF 注入（`Content-Disposition` 头注入防护）；audio 代理响应无 `Content-Disposition` | grep/断言无违规 |
| 3.3 | 文档同步：`servers/recording/AGENTS.md`（API 面 + audio 端点、`Audio/` + AudioClipExtractor、`AudioClipMaxDurationMs` 配置、镜像流水线断言）、`servers/api/AGENTS.md`（audio 代理 + `Content-Disposition` 透传）、`servers/recording-transcript-api/AGENTS.md`（API 面 + subtitle 端点、`Subtitles/` 模块）、`servers/recording-transcript-consumer/AGENTS.md`（`Asr/`、`Tasks/`、`Enqueue/` 上下文/入队日志描述）、`apps/web/AGENTS.md`（`SegmentAudioPlayer` 与 `TaskListTab` 描述）；本 Plan 与提案/PRD 链接互相有效 | 文档与实现一致 |
| 3.4 | 手工验证（z-aspire-orchestrator，可选但推荐）：本地 Aspire 拉起 → 已落盘录播任选一句试听（区间与展示一致、切换/停止无残留、3 小时录播不慢）→ 下载 SRT/ASS 在常见播放器加载录播视频核对时间轴与中文无乱码（文件名含场次标题与模型）→ 触发一次识别确认 run-task 日志含具体上下文、入队日志含提交信息、无凭据 → 录制中场次试听提示「音频暂不可用」 | 手工验证记录 |

## 4. 并行策略

- **Phase 0 串行先行**（0.1→0.2→0.3→0.4）：契约是四条线的联调与 codegen 基线，必须最先合入。
- **Phase 1/2 四条线并行，线内串行**：
  - **线 A 试听后端**：1.1→1.2→2.1→2.2①（recording 端点先于其代理）；
  - **线 B 字幕后端**：1.3→1.4→1.5→2.2②→2.3（renderer → 端点 → 透传）；
  - **线 C 日志**：1.6→1.7→2.4（与 A/B 零文件交集，可最先开工）；
  - **线 D web**：1.8→2.5（依赖 Phase 0 codegen；组件以 mock audio 测试驱动，不阻塞于 A/B 联调，联调在 3.4 收口）。
- **文件冲突规避**：`TaskListTab.tsx` 同时承载试听与下载入口——web 改动（含 `client.ts`、测试）**全部收敛在线 D 单线**，线 A/B 只做后端；线 C 的 `TruncateForLog` 重命名与 2.4 同线串行，避免 `AliFunAsrService.cs` 冲突。
- **依赖关系**：2.2① 依赖 0.3 契约 + 2.1 联调口径（测试用 fake HttpClient 不依赖真实 recording）；2.5 的 URL 形态依赖 0.4 codegen；3.x 全线完成后串行收口。
- 全部提交在需求总分支 `feat/transcript-display-optimization`。

## 5. 验收对照

| PRD 功能点 | 实现任务 | 验证方式 |
|-----------|---------|---------|
| 功能点 1：识别片段语音试听 | 2.1、2.2①、2.5②③④⑤ | 1.1、1.2、1.8 + 3.4 手工验证（区间一致/切换停止/历史任务/不残留） |
| 功能点 2：字幕文件下载（SRT/ASS） | 0.2、2.2②、2.3、2.5③ | 1.3、1.4、1.5、1.8 + 3.4 手工验证（播放器对齐/中文不乱码/文件名兜底/RUNNING 部分字幕） |
| 功能点 3：识别提交上下文日志 | 2.4①②③④ | 1.6、1.7 + 3.2 grep（具体上下文/空与截断标识/提交信息/无凭据） |
| 非功能：性能（2 秒出声/只传片段） | 2.1（输入 seek + 片段抽取）、2.5② | 1.1（时长断言）+ 3.4 手工验证 |
| 非功能：安全（沿用访问路径/日志无凭据） | 2.2（既有代理路径）、2.4⑤ | 3.2 grep |
| 非功能：兼容（既有下载/实时音频/历史结果） | 2.2②（不影响既有端点）、2.3（任意状态可下载） | 3.1 既有回归 + 3.4 |
| 契约与质量门槛（codegen 一致/覆盖率 ≥ 80%/TDD） | 0.4、2.6、3.1 | `npm run codegen` diff、覆盖率报告、红-绿提交记录 |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| 镜像 ffmpeg 缺 libmp3lame | mp3 编码不可用则片段端点失效 | 启动探测 `ffmpeg -encoders`，缺失 Warn + 降级 WAV（`audio/wav`，接口语义不变）；镜像流水线构建期断言（2.1⑤，提案 §9.2 决策） |
| `-ss` seek 精度 | 播放区间须与展示起止时间一致 | `-ss`/`-t` 前置输入 seek + 转码模式 `accurate_seek` 默认开启（精确到采样）；1.1 断言输出时长 ≈ 区间 |
| audio 代理误抄下载代理的 `Content-Disposition` 兜底 | attachment 头对 `<audio>` 内联播放语义不符 | 2.2① 显式不设该头；1.2 断言锁死 |
| `TaskListTab.tsx` 多线并改冲突 | 线 A/B 都要动识别结果面板 | web 改动全部收敛线 D 单线（§4） |
| CI/开发机无 ffmpeg | 进程测试无法执行 | 沿用 `FfmpegRecordingSmokeTests` 无 ffmpeg Skip 模式；3.4 本地 Aspire 手工兜底 |
| `TruncateRaw` 重命名行为漂移 | 触碰 `LogFailure` 既有调用点与既有日志断言 | 仅重命名不改口径（2KB + `...(truncated)`）；`AliFunAsrErrorLogTests` 回归不动（2.4①） |
| ASS 厘秒取整 / SRT 小时进位细节争议 | 格式细节即契约，播放器兼容敏感 | 1.3 golden 断言锁定（厘秒向下取整、小时两位起）；3.4 真实播放器验证 |
| 非 ASCII 文件名 `Content-Disposition` | 部分客户端不识别裸 UTF-8 文件名 | `File(...)` 重载由框架生成 `filename*`，ascii 段兜底；1.4 中文标题场景断言 |
| 长录播首播慢 | 3 小时 FLV 输入打开耗时影响 2 秒出声 | 输入 seek（`-ss` 前置）不需解码前段；区间上限 300s 控制单次成本（提案 §6.1 决策 3） |
| 依赖 | recording 运行镜像含 ffmpeg（Dockerfile 已装）；`transcript_segments`/`transcript_tasks` 既有数据；`ListByTaskAsync`、`FakeFunAsrServer`、`ListLogger`、`RecordingFakes` 测试设施 | 均已核实存在（§1.0），零新增外部依赖 |

## 7. 相关文档

- PRD：`docs/product/draft/20260922-transcript-display-optimization-prd.md`（v1.0）
- 技术提案：`docs/engineering/proposals/20260922-proposal-transcript-display-optimization.md`（v1.0）
- 前序 Plan：[[20260916-plan-recording-transcript]]、[[20260917-plan-transcript-model-context]]、[[20260918-plan-transcript-asr-error-observability]]、[[20260918-plan-transcript-fixes]]
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`docs/engineering/frontend-rules.md`
- 模块规范：`servers/recording/AGENTS.md`、`servers/api/AGENTS.md`、`servers/recording-transcript-api/AGENTS.md`、`servers/recording-transcript-consumer/AGENTS.md`、`apps/web/AGENTS.md`
- 既有契约：`contracts/recording.openapi.yaml`、`contracts/recording-transcript.openapi.yaml`、`contracts/bilibili-crawler.openapi.yaml`
