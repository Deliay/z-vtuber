---
description: 后台录播预览与切片下载实现 Plan：web 以 mpegts.js（MSE）直读既有录播下载端点（Range 已透传）在线播放，vendored 字节偏移续播桥接 + 客户端关键帧预测实现未缓冲 seek；recording 新增 clip 端点（ffmpeg -c copy 窗口切片 → fMP4）与 api 代理镜像；契约先行 + TDD（seek 纯函数/播放器桥接/预览弹窗/clip 接口/代理透传），AV1 与 MSE 缺失按 PRD 降级，既有下载/音频/录制链路零回归
type: Permanent
---

# 后台录播预览与切片下载 - 实现 Plan

**功能名称**: 后台录播预览与切片下载
**关联 PRD**: `docs/product/draft/20261021-recording-preview-clip-prd.md`（v1.1）
**关联技术提案**: `docs/engineering/proposals/20261021-proposal-recording-preview-clip.md`（v1.1 返工修订：mpegts.js + 关键帧预测）
**Plan 版本**: v1.0
**创建日期**: 2026-10-21
**作者**: Workflow Agent（代起草）
**需求总分支**: `feat/recording-preview-clip`（自 main 切出，含 `3b8e71c` + 返工修订 `de56c04`；需求总 PR #195）
**实现分支**: 同需求总分支 `feat/recording-preview-clip`（按仓库既定做法直接在该分支提交，随 PR #195 评审）

## 1. 概述

依据 PRD v1.1 与技术提案 v1.1，本期落地两条链路：

1. **在线预览与定位播放（纯前端、零服务端转码）**：`apps/web` 新增 `mpegts.js`（MSE）播放器，直接读取既有录播下载端点 `GET /api/b/live/{roomId}/recordings/{fileName}`（api 代理的 Range/206 透传已在 20260914 需求打通）。FLV 无关键帧索引导致 mpegts 原生 seek 静默失效，故移植参考实现 [Deliay/sumire](https://github.com/Deliay/sumire/) 的字节偏移续播补丁与关键帧预测：线性估算字节偏移 → `Range` 窗口探测（非 206 一律放弃）→ FLV tag 三重校验并优先对齐视频关键帧 → 从该字节偏移续播；缓冲按 50MB 字节预算折算秒数，避免数小时录播击穿 MSE 配额。
2. **切片下载（服务端零转码）**：`servers/recording` 新增 `GET /api/{roomId}/recordings/{fileName}/clip?startMs&endMs`，以 ffmpeg `-c copy` 输入 seek 截取窗口并输出 fragmented MP4；`servers/api` 新增同名 `/api/b/live/.../clip` 流式代理；`apps/web` 在预览弹窗内选起止点后以 `<a download>` 流式下载片段。预览与切片起点均以关键帧为落点，保证「预览所见即片段所得」。

改动分四块：① **契约先行**——`contracts/recording.openapi.yaml` 与 `contracts/bilibili-crawler.openapi.yaml` 新增 clip 端点，`npm run codegen` 再生成 web 类型；② **recording**——`Video/VideoClipExtractor`、控制器 clip 动作、`VideoClipMaxDurationMs` 配置、DI 注册；③ **api**——clip 代理与长流超时口径；④ **web**——mpegts 依赖、字节偏移桥接、关键帧预测、单例播放器、预览弹窗、历史录播「预览」入口与下载 URL/错误文案。全程遵循 `docs/engineering/common-rules.md`（契约先行、TDD、核心逻辑覆盖率 ≥ 80%、单用例 ≤ 5s）、`backend-rules.md`、`frontend-rules.md` 及各子项目 AGENTS.md。

### 1.0 现状核实结论（读码确认，需求总分支 @ `de56c04`）

- **web 现状**：`RecordingStatusPage.tsx:127-146` 的 `renderFileList` 只渲染文件名下载链接（`recordingDownloadUrl`）与场次/大小，无预览入口；`client.ts:245-247` 为下载 URL 构造器、`:249-256` 为音频片段 URL 构造器（`URLSearchParams` + `encodeURIComponent` 范式可复用）；`client.ts:56-107` 为服务端错误码 → 中文文案映射；`utils/format.ts:41-70` 已有 `formatPosition`/`parsePosition`（`m:ss` / `m:ss.S` ↔ 毫秒），可直接用于进度与区间输入；`apps/web/package.json` 当前仅 `wavesurfer.js` 一个新增依赖，尚无 `mpegts.js`。
- **recording 现状**：`RecordingController.cs:118-128` 下载端点使用 `PhysicalFile(..., enableRangeProcessing: true)`，Range/206/416 由框架原生支持；`:130-179` 音频片段端点示范了「参数校验顺序 → `ResolveRecordingFileAsync`（路径穿越/录制中 400/不存在 404）→ 流式抽取 → 零字节失败 500」的完整口径；`:294` 起为 `ResolveRecordingFileAsync`。`Audio/AudioClipExtractor.cs` 示范 `IRecordingProcessFactory` + `BuildArguments` 纯函数 + `pipe:1` + 取消 kill 回收；`RecordingProcess.cs` 的 `FfmpegRecordingProcessFactory` 可原样复用。`RecordingOptions.cs:56-64` 已有 `AudioClipMaxDurationMs`/`AudioMerge*` 配置范式，尚无视频片段上限。`Program.cs:45-47` 注册音频抽取器/合并器，**尚无 `Video/` 目录与视频抽取器注册**。
- **文件与编码现状**：`Recording/RecordingFileName.cs` 文件名模板为 `[{RAW|AV1}][{roomId}][{anchorName}] {日期}-{场次标题}.flv`；`Live/StreamResolver.cs:12-18` 明确排除 `hevc`、优先 `http_stream + flv + avc`，因此 RAW 录播为 H.264 + AAC（mpegts.js 可播），AV1 二压文件为 AV1 编码（mpegts.js 不支持，需按 PRD 置灰）。
- **api 现状**：`BilibiliLiveRecordingController.cs:127-224` 下载代理已透传 `Range`/`If-Range`，透传 200/206/416 与 `Accept-Ranges`/`Content-Range`/`Content-Length`；`:225-278` 音频片段代理示范「query 原样透传 + `ResponseHeadersRead` + `CopyToAsync` + 400 响应体透传 + 404 + 502」；`Program.cs:51-54` 的 `RecordingClientName` 未显式设超时（默认 100s），`:72-76` 的视频下载 `CrawlerClientName` 已用 `Timeout.InfiniteTimeSpan` 先例。
- **契约现状**：`contracts/recording.openapi.yaml:92`（下载）/`:163`（音频片段）与 `contracts/bilibili-crawler.openapi.yaml:300`（下载代理）/`:365`（音频片段代理）结构清晰，均无 clip 端点；web `codegen` 会从这两份契约生成 `schema.d.ts`/`schema-recording.d.ts`。
- **测试设施现状**：`tests/Z.Vtuber.Tests/RecordingAudioClipTests.cs` 提供「lavfi 合成源 + 真 ffmpeg + ffprobe 验时长 + 无 ffmpeg 环境早退」模式；`RecordingFakes.cs` 的 `FakeRecordingProcessFactory`/`FakeRecordingProcess` 可注入零字节/非零退出失败；`BilibiliLiveRecordingControllerTests.cs` 的 `StubHandler`/`StubHttpClientFactory` 可断言上游 query 与响应头/状态码。以上均无需新增测试基座。
- **参考实现核实（Deliay/sumire @ main）**：`apps/bookstore/features/recordings/player.ts` 单例、`computeBufferConfig`（`BUFFER_BUDGET_BYTES=50MB`、前瞻 60%、`DEFAULT_BITRATE_BPS=50Mbps`、秒数钳 `[0.1, 300]`）、`probeSeekable`（`Range: bytes=0-0`）；`seek.ts` 的 `estimateByteOffset`/`computeScanBytes`（10s，钳 256KB~8MB）/FLV tag 三重校验/视频关键帧优先/last-wins 与本 Plan 一致；`patches/mpegts.js@1.8.2.patch` 实际是**新增 `dist/mpegts-byte-offset-seek.js` 包装层并把包 `main` 指过去**（不是对 `dist/mpegts.js` 做原地 monkey-patch），其 `seekToByteOffset` 依赖 `player._player_engine._transmuxer._controller` 等内部字段，且 `package.json` 将 `mpegts.js` 精确锁定为 `1.8.2`。

### 1.1 口径澄清（规划期裁定，随实现评审）

1. **mpegts.js 精确锁定 `1.8.2`**：参考补丁直接访问播放器内部字段（`_player_engine`/`_transmuxer`/`_controller`/`_mse_controller`/`_loading_controller`/`_seeking_handler`），不能用 `^1.8.2` 自动升级；`apps/web/package.json` 写 `"mpegts.js": "1.8.2"`，并提交 lock 文件变更。
2. **vendored 桥接采用「显式 attach」而非全局 monkey-patch**：新增 `mpegtsByteOffsetSeek.ts`，导出 `attachByteOffsetSeek(player)`（附加 `seekToByteOffset`/`setBufferLimits`）与可测的 `applyByteOffsetSeek`；`recordingPlayer.ts` 在 `mpegts.createPlayer` 返回后调用 attach。语义与 sumire 补丁一致，但不需要替换 npm 包 `main`、不依赖模块加载顺序，便于单测与动态 import。
3. **mpegts 只在浏览器运行时动态加载**：`recordingPlayer.ts` 用 `await import('mpegts.js')` 缓存单例，避免 jsdom/SSR 期求值浏览器全局；组件测试统一 `vi.mock('mpegts.js')` 或 mock `recordingPlayer`，不在 jsdom 中跑真实 MSE。
4. **预览入口门控**：仅历史录播列表（`listRecordings` 已排除录制中文件）提供「预览」；文件名以 `[AV1]` 开头 → 置灰 + 「AV1 编码暂不支持在线预览」；`isPlaybackSupported()` 为 false → 置灰 + 「当前浏览器不支持在线预览」；两者均不影响完整文件下载与片段下载。
5. **定位口径**：播放器拿到 `duration` 前禁用拖动（进度条 `max=0` 天然门控）；`probeSeekable`（`Range: bytes=0-0`，206 或 `accept-ranges: bytes`）失败 → 禁用拖动并提示「该文件暂不支持跳转」，仍可顺序播放；目标在 `video.buffered` 内走原生 `currentTime`，否则走 `seekUnbuffered`；非 206 一律取消响应体并降级提示，**绝不整文件 buffer**；并发 seek last-wins。
6. **切片参数与抽帧口径**：`startMs`/`endMs` 必填、`startMs >= 0`、`endMs > startMs`、`endMs - startMs <= Recording:VideoClipMaxDurationMs`（默认 `7_200_000` = 2h），错误码与音频片段同构；ffmpeg 参数固定为 `-ss <start> -t <duration> -i <file> -map 0:v:0 -map 0:a:0? -c copy -movflags frag_keyframe+empty_moov -avoid_negative_ts make_zero -f mp4 pipe:1`（输入 seek 秒开、原始编码、fMP4 可流式；`-avoid_negative_ts make_zero` 保证独立播放时间轴从 0 开始）。
7. **起点对齐关键帧**：前端传入预览定位落点的关键帧毫秒（FLV tag 时间戳）；后端 `-ss` 输入 seek 会吸附到不晚于该时间的最近关键帧，与预览所见一致。手动输入起点时界面提示「起点会对齐到最近关键帧」；`endMs` 超文件长度由 ffmpeg 自然输出到结尾。
8. **clip 响应头**：`Content-Type: video/mp4`、`Cache-Control: no-store`、`Content-Disposition: attachment`。文件名含中文/空格，**不得直接把原始文件名拼进 ASCII 头**：使用 `ContentDispositionHeaderValue` 同时设置 ASCII 兜底 `FileName`（如 `clip-{start}-{end}.mp4`）与 RFC 5987 `FileNameStar`（`{原文件名}-{start}-{end}.mp4`），api 代理原样透传该头。
9. **下载失败提示**：`<a download>` 无法感知服务端 4xx/5xx，为满足 PRD「片段生成失败提示」，点击「下载片段」时先做一次轻量预检 `fetch(url)`：响应头到达即 `abort` 并取消响应体；非 2xx 读取错误体经 `apiErrorMessage` 提示且不触发下载；2xx 再创建临时 `<a download>` 触发浏览器流式下载。预检会让 ffmpeg 短暂启动一次（首字节后即取消，服务端 kill 回收），这是为错误可见性付出的可控成本；不在前端整段缓冲片段。
10. **api 代理口径**：clip 代理 query 原样透传；上游 400 透传响应体、404 → 404、500 `{"error":"video clip failed"}` 透传为 500（**不归并 502**）、不可达/超时 → 502；成功透传 `Content-Type`/`Content-Disposition`/`Cache-Control` 后 `CopyToAsync`。为不改变既有列表/下载/音频代理的超时行为，**新增专用 named client `RecordingStreamClientName`**（同 recording BaseAddress，`Timeout.InfiniteTimeSpan`）仅供 clip 使用；避免把共享 `RecordingClientName` 改为无限超时导致状态列表在 recording 网络悬挂时永久等待。客户端断开由 `RequestAborted` 取消上游。
11. **无 Dockerfile 变更**：当前 recording 镜像仅保留 mp3 编码构建断言（`.github/workflows/build-recording-image.yml:24-37`），不存在 v1.0 方案提到的 libx264 断言；clip 为 `-c copy`，无需新增编码器依赖。
12. **无持久化/迁移/新配置必填项**：`VideoClipMaxDurationMs` 有默认值；片段即生成即下载、不落盘、不缓存、不提供片段管理；不改录制流程、存储结构、既有下载/Range/音频试听/音频合并行为。
13. **AV1 切片仍可用**：AV1 仅不支持在线预览；clip 端点对 AV1 文件照常 `-c copy` 输出 MP4（ffmpeg 支持 AV1 in MP4），手工验收覆盖。
14. **UI 口径**：antd v6 `Modal`/`Button`/`Slider`/`Input`/`Alert`/`Typography.Link`/`Tooltip`，反馈统一 `App.useApp().message`，置灰用 `aria-disabled` + `preventDefault` + Tooltip（与训练页下载数据集同口径），不写野生内联样式大对象；测试用 `renderWithProviders`。
15. **测试口径**：前端纯函数（seek/缓冲/桥接）优先、组件测试 mock 播放器与 seek；后端真 ffmpeg 用例沿用「无 ffmpeg/ffprobe 早退」模式，合成源 ≤ 6s、单用例 ≤ 5s；clip 接口测试覆盖参数矩阵/文件解析/响应头/失败注入；代理测试覆盖 query、400/404/500/502 与响应头。

### 1.2 实施范围

- **做**：
  - 契约：`contracts/recording.openapi.yaml` 新增 clip 端点；`contracts/bilibili-crawler.openapi.yaml` 新增代理镜像；`apps/web` `npm run codegen` 再生成类型。
  - `servers/recording`：`Video/VideoClipExtractor.cs`（接口/异常/`BuildArguments` 纯函数/流式抽取）、`RecordingController` clip 动作、`RecordingOptions.VideoClipMaxDurationMs`、`Program.cs` DI 注册。
  - `servers/api`：`BilibiliLiveRecordingController.ProxyRecordingClip`、`Program.cs` recording 客户端长流超时。
  - `apps/web`：`mpegts.js@1.8.2` 依赖、`mpegtsByteOffsetSeek.ts`、`recordingSeek.ts`、`recordingPlayer.ts`、`RecordingPreviewPlayer.tsx`、`RecordingStatusPage.tsx` 预览入口、`client.ts` `recordingClipUrl` 与 clip 错误文案。
  - 测试：`VideoClipExtractorTests`、`RecordingVideoClipTests`、`BilibiliLiveRecordingControllerTests` clip 用例、前端 `recordingSeek.test.ts`/`recordingPlayer.test.ts`/`mpegtsByteOffsetSeek.test.ts`/`RecordingPreviewPlayer.test.tsx`/`RecordingStatusPage.test.tsx`/`client.test.ts`。
  - 文档：`apps/web/AGENTS.md`、`servers/recording/AGENTS.md`、`servers/api/AGENTS.md`；PRD 验收勾选与文档互链（实现收口阶段）。
- **不做**：服务端预览端点/转码/remux 窗口流、HLS 化、FLV 关键帧索引注入（录制端改造）、逐帧精确剪切、AV1 在线预览、切片持久化/任务队列/断点续传、预览内倍速/快捷键/弹幕、转写页/训练页复用、播放器 worker 模式、对既有下载/音频/合并/录制流程的语义改动。

## 2. 分支与提交流程

- 需求总分支 `feat/recording-preview-clip` 已推送 origin（PR #195，base: main）；本 Plan 提交到该分支随 PR 评审。
- 实现按 z-coding「编写实现」流程推进：**先契约、再后端/前端并行、每块 TDD 红-绿、测试与实现分开提交**；全部提交直接入需求总分支（与 `20260914-plan-recording-download-range.md` 同做法），不另切开发子分支。
- 遵循 z-git：只允许 merge，禁止 rebase/squash；工作区开始前保持 clean（当前 `de56c04` 干净）。
- 提交顺序（Conventional Commits）：
  1. `docs(recording-preview-clip):` 本实现 Plan（本次提交）；
  2. `feat(contracts):` 两份 OpenAPI 新增 clip + `npm run codegen`；
  3. `test(recording):` clip 抽取器/接口测试先行；`feat(recording):` `VideoClipExtractor` + 控制器 + 配置 + DI；
  4. `test(api):` clip 代理测试先行；`feat(api):` `ProxyRecordingClip` + 超时口径；
  5. `test(web):` seek/桥接/播放器/弹窗/页面测试先行；`feat(web):` 依赖 + 四个模块 + 页面/客户端接入；
  6. `docs(recording-preview-clip):` AGENTS.md、PRD 验收勾选、提案/Plan 互链。
- 每个提交前运行对应测试；收口提交前运行全量前后端测试与构建（见 Phase 6）。

## 3. 实施阶段

### Phase 0: 技术文档（先行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | PRD（上游节点产出） | `docs/product/draft/20261021-recording-preview-clip-prd.md` | 已完成（v1.1） |
| 0.2 | 技术提案（上游节点产出） | `docs/engineering/proposals/20261021-proposal-recording-preview-clip.md` | 已完成（v1.1 返工修订 `de56c04`） |
| 0.3 | 实现 Plan（本文档） | `docs/engineering/plans/20261021-plan-recording-preview-clip.md` | 任务/文件/验收明确；§1.1 偏差已标注 |

### Phase 1: 契约先行

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | `contracts/recording.openapi.yaml` 新增 `GET /api/{roomId}/recordings/{fileName}/clip`（operationId `downloadRecordingClip`）：`RoomId` + `fileName` + `startMs`/`endMs`（int64，必填，起点 ≥ 0、终点 > 起点、区间 ≤ `Recording:VideoClipMaxDurationMs`）；`200` `video/mp4` binary + `Content-Disposition: attachment` + `Cache-Control: no-store`，描述「ffmpeg -c copy 原始编码、fMP4、起点吸附最近关键帧、endMs 超长输出到结尾」；`400`（参数非法/超上限/录制中/文件名非法）、`404`（不存在）、`500`（`video clip failed`） | `contracts/recording.openapi.yaml` | 契约自洽，`npx openapi-typescript` 可解析 |
| 1.2 | `contracts/bilibili-crawler.openapi.yaml` 新增 `GET /api/b/live/{roomId}/recordings/{fileName}/clip` 代理镜像（operationId `proxyRoomRecordingClip`）：query 透传、200 头同 1.1、400/404/500 透传、502 `{"error":"recording service unavailable"}` | `contracts/bilibili-crawler.openapi.yaml` | 与 1.1 语义一致 |
| 1.3 | `apps/web` 执行 `npm run codegen`，提交 `schema.d.ts`/`schema-recording.d.ts` 生成物（勿手改） | `apps/web/src/api/` | 生成成功，clip 端点出现在类型中，无无关 diff |

### Phase 2: 后端测试先行（红）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | `VideoClipExtractorTests`：`BuildArguments` 断言 `-ss`/`-t` 在 `-i` 前、`-map 0:v:0`/`-map 0:a:0?`、`-c copy`、`-movflags frag_keyframe+empty_moov`、`-avoid_negative_ts make_zero`、`-f mp4`、`pipe:1`、毫秒→秒格式（1500ms→1.5）；`ExtractAsync` 零字节 + 非零退出抛 `VideoClipFailedException`，有字节 + 非零不抛 | `tests/Z.Vtuber.Tests/VideoClipExtractorTests.cs` | 红 |
| 2.2 | `RecordingVideoClipTests`（真 ffmpeg，无 ffmpeg/ffprobe 早退）：lavfi `testsrc2` + `sine` 生成 6s 320x240 H.264/AAC FLV（`-g` 约 2s）；断言 200 + `video/mp4` + `Content-Disposition`（`filename*=UTF-8''` 含原文件名）+ `Cache-Control: no-store`、产物可被 ffprobe 解析且时长 ≈ 请求区间、参数矩阵 400（缺参/负起点/终点≤起点/超上限）、录制中 400、不存在 404、非法文件名 400、ffmpeg 失败 500（`FakeRecordingProcessFactory` 注入） | `tests/Z.Vtuber.Tests/RecordingVideoClipTests.cs` | 红 |
| 2.3 | `BilibiliLiveRecordingControllerTests` 增补 clip 代理用例：query 原样到达上游、上游 200 → 200 + `Content-Type`/`Content-Disposition`/`Cache-Control`/字节透传、上游 400 响应体透传、上游 500 `video clip failed` 透传（不归并 502）、上游 404 → 404、不可达/超时 → 502 | `tests/Z.Vtuber.Tests/BilibiliLiveRecordingControllerTests.cs` | 红 |

### Phase 3: 后端实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 3.1 | `Video/VideoClipExtractor.cs`：`IVideoClipExtractor.ExtractAsync(fullPath, startMs, endMs, output, ct)`、`VideoClipFailedException`、`BuildArguments` 纯函数（§1.1-6）、沿用 `IRecordingProcessFactory`（stderr 泵、取消 kill 回收、零字节非零退出抛异常） | `servers/recording/Video/VideoClipExtractor.cs` | 2.1 转绿 |
| 3.2 | `RecordingOptions` 新增 `VideoClipMaxDurationMs`（`[Range(1, 86_400_000)]`，默认 `7_200_000`，注释「单个视频片段时长上限，超限 400」）；`Program.cs` 注册 `IVideoClipExtractor` | `servers/recording/Configuration/RecordingOptions.cs`、`servers/recording/Program.cs` | 配置校验通过；DI 可解析 |
| 3.3 | `RecordingController` 新增 clip 动作：校验顺序同音频（`startMs and endMs are required` → `startMs must be >= 0` → `endMs must be greater than startMs` → `clip duration must be <= {VideoClipMaxDurationMs} ms`）→ `ResolveRecordingFileAsync`（路径穿越/录制中 400/不存在 404）→ 设置 `video/mp4`/`Cache-Control: no-store`/RFC 5987 `Content-Disposition` → 流式抽取 → 零字节失败清头并 500 `{"error":"video clip failed"}` | `servers/recording/Controllers/RecordingController.cs` | 2.2 转绿；既有音频片段/下载用例零回归 |
| 3.4 | `BilibiliLiveRecordingController.ProxyRecordingClip`（照音频代理）：`GET api/{roomId}/recordings/{escaped}/clip{Request.QueryString}`、`ResponseHeadersRead` + `CopyToAsync`；400 透传响应体、404、500 透传（保留 JSON 体）、其余非成功/不可达/超时 → 502；成功透传三头；`Program.cs` 新增 `RecordingStreamClientName`（同 BaseAddress，`Timeout.InfiniteTimeSpan`）供 clip 使用，既有 `RecordingClientName` 不动 | `servers/api/Controllers/BilibiliLiveRecordingController.cs`、`servers/api/Program.cs` | 2.3 转绿；既有列表/下载/音频代理超时口径零变化 |

### Phase 4: 前端测试先行（红）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 4.1 | `recordingSeek.test.ts`：`estimateByteOffset`（线性/钳制/非法）、`computeScanBytes`（10s/钳 256KB~8MB）、`findVideoKeyframeBoundary`/`findNextTagBoundary`（合成 FLV tag：三重校验失败跳过、`frameType=1` 判定、毫秒时间戳）、`seekUnbuffered`（非 206 取消 body 且不调用 applySeek、`Content-Range` 校正、窗口上限、无关键帧回退任意 tag、无 tag 估算直试、并发 last-wins 丢弃过期结果） | `apps/web/src/pages/recording/recordingSeek.test.ts` | 红 |
| 4.2 | `mpegtsByteOffsetSeek.test.ts`：`applyByteOffsetSeek` 调用顺序（flush → `_internalAbort` → `remuxer.seek`/`insertDiscontinuity` → `_loadSegment(0, offset)` → `resumeTransmuxer` → `directSeek`）；lazyLoad guard 在首个 `media_segment`/`recommend_seekpoint` 或 3s 超时恢复、连续 seek 仅续命且仅最外层恢复 previous；内部结构缺失显性抛错；`setBufferLimits` 合并配置 | `apps/web/src/pages/recording/mpegtsByteOffsetSeek.test.ts` | 红 |
| 4.3 | `recordingPlayer.test.ts`：`computeBufferConfig`（50MB 预算、前瞻 60%/回退 40%、duration 未知按 50Mbps、秒数钳 `[0.1,300]`、`(forward+backward)×码率 ≤ 预算`）；`probeSeekable`（206 / `accept-ranges` / 异常 false）；单例与代次（加载期被 destroy/新 play 取代则丢弃）；patch 未 attach 时显性返回 false | `apps/web/src/pages/recording/recordingPlayer.test.ts` | 红 |
| 4.4 | `RecordingPreviewPlayer.test.tsx`（mock `recordingPlayer`/`recordingSeek`/`fetch`）：打开即 play、关闭/卸载 destroy；duration 未知拖动禁用、probe 失败禁用并提示；已缓冲走原生 `currentTime`、未缓冲走 `seekUnbuffered` 且失败提示；「设为起点/终点」记录当前毫秒、非法手输拦截、起点≥终点拦截、清空；未选全下载置灰；下载预检 2xx 触发临时 `<a download>`、4xx/5xx 经 `apiErrorMessage` 提示且不触发；AV1/MSE 不支持提示；播放错误提示「预览暂不可用」 | `apps/web/src/pages/recording/RecordingPreviewPlayer.test.tsx` | 红 |
| 4.5 | `RecordingStatusPage.test.tsx` 增补：历史文件行渲染「预览」入口；`[AV1]` 文件置灰 + Tooltip；MSE 不支持置灰；点击 RAW 打开弹窗并传 `fileSize`；`client.test.ts` 增补 `recordingClipUrl`（文件名编码 + `startMs`/`endMs` query）与 clip 错误文案映射 | `apps/web/src/pages/recording/RecordingStatusPage.test.tsx`、`apps/web/src/api/client.test.ts` | 红 |

### Phase 5: 前端实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 5.1 | `apps/web/package.json` 新增 `"mpegts.js": "1.8.2"`（精确锁定），更新 lock 文件；`npm install` 后 `npm run build` 可解析 | `apps/web/package.json`、lock | 依赖树无重复/冲突 |
| 5.2 | `mpegtsByteOffsetSeek.ts`：移植 sumire 补丁逻辑为 TS（`applyByteOffsetSeek`/`attachByteOffsetSeek`/`setBufferLimits`/lazyLoad guard/seek 对齐兜底）；内部结构不符抛错；导出 guard 常量与测试接缝 | `apps/web/src/pages/recording/mpegtsByteOffsetSeek.ts` | 4.2 转绿 |
| 5.3 | `recordingSeek.ts`：`estimateByteOffset`/`computeScanBytes`/FLV tag 扫描/`seekUnbuffered`（last-wins、非 206 取消、窗口上限、关键帧优先、降级链） | `apps/web/src/pages/recording/recordingSeek.ts` | 4.1 转绿 |
| 5.4 | `recordingPlayer.ts`：动态 import、`isPlaybackSupported`/`probeSeekable`/`computeBufferConfig`/`play`（单例+代次+attach 桥接）/`configureBufferForBitrate`/`seekToByteOffset`/`pausePlayer`/`resumePlayer`/`destroyPlayer`；`createPlayer` 配置 `{ type:'flv', isLive:false, url }` + `seekType:'range'` + **首播即传入 `computeBufferConfig(fileSize, 0)`**（避免默认 180s 前瞻），metadata 到手后 `setBufferLimits` 重算 | `apps/web/src/pages/recording/recordingPlayer.ts` | 4.3 转绿 |
| 5.5 | `RecordingPreviewPlayer.tsx`：Modal + `<video>` + 自定义控制条（播放/暂停、进度 Slider、时间显示、跳转输入、音量、全屏）；定位策略（buffered 原生 / 未缓冲 seek）；区间选择（设为起点/终点、手输 `parsePosition` 校验、清空、时长展示、关键帧提示）；下载（预检 + 临时 `<a download>`，未选全置灰）；AV1/MSE/probe/播放错误提示；卸载 `destroyPlayer(url)` | `apps/web/src/pages/recording/RecordingPreviewPlayer.tsx` | 4.4 转绿；UI 规范符合 `frontend-rules.md` |
| 5.6 | `RecordingStatusPage.tsx`：`renderFileList` 每行加「预览」（RAW 可用、AV1/MSE 不支持置灰 + Tooltip），挂载 `RecordingPreviewPlayer`，传 `roomId/fileName/fileSize`；`client.ts` 新增 `recordingClipUrl` 与 clip 错误码中文映射；进度/区间复用 `formatPosition`/`parsePosition` | `apps/web/src/pages/recording/RecordingStatusPage.tsx`、`apps/web/src/api/client.ts` | 4.5 转绿；既有页面用例零回归 |

### Phase 6: 收口验证与文档同步

| # | 任务 | 验收 |
|---|------|------|
| 6.1 | 后端全量：`dotnet build` + `dotnet test tests/Z.Vtuber.Tests --logger "trx;LogFileName=tests.trx"` + `dotnet run --project tests/Z.Vtuber.TestReport -- ... --check`（单用例 ≤5s，新增慢用例登记例外） | 全绿 |
| 6.2 | 前端全量：`npm run codegen`（0 diff 复核）、`npm run test`、`npm run lint`、`npm run build` | 全绿 |
| 6.3 | 编排手工验收（z-aspire-orchestrator）：真实 RAW 录播预览约 1s 起播（画面+声音、原始画质）；拖动到未加载位置约 1s 续播且落点为关键帧、不冻结；长时间播放内存稳定；选段下载片段可独立播放、时长≈区间、与预览起点一致；AV1 入口置灰但完整/片段下载可用；MSE/Range 降级提示；片段参数非法/文件不存在/失败提示 | 手工记录 |
| 6.4 | 文档同步：`apps/web/AGENTS.md`（新增文件/依赖/预览交互/时长上限口径）、`servers/recording/AGENTS.md`（`Video/VideoClipExtractor`、API 表 clip、配置 `VideoClipMaxDurationMs`）、`servers/api/AGENTS.md`（clip 代理与长流超时）；PRD 验收项勾选；提案/Plan 互链 | 文档与实现一致 |
| 6.5 | 契约一致性 grep：两份 OpenAPI 的 clip 参数/状态码/响应头/错误文案一致；`git diff` 检查无既有端点语义改动；更新 PR #195 说明 | 一致 |

## 4. 并行策略

- **Phase 1 契约先行**由单线程完成（1.1 → 1.2 → 1.3），避免两份契约与 codegen 生成物冲突。
- 契约冻结后按子项目并行（z-coding 建议的 `@general` 委托）：
  - **后端线**：`servers/recording` + `servers/api` + `tests/Z.Vtuber.Tests`；按 Phase 2 → 3 推进，先写测试再实现，接口以契约/本 Plan §1.1 为准。api 代理可用 `StubHandler` 独立开发，不等待 recording 完成。
  - **前端线**：`apps/web` 全部改动；按 Phase 4 → 5 推进，纯函数（seek/桥接/缓冲）先绿，再接播放器与弹窗；组件测试 mock 播放器，不依赖后端。
- 文件所有权：契约/生成物归契约线；`servers/recording`/`servers/api`/`tests` 归后端线；`apps/web` 归前端线；`docs/**/AGENTS.md` 收口阶段统一更新。三条线无文件写交集。
- 测试与实现分开提交；每条线在提交前跑自己的测试，收口由主线跑全量（Phase 6）。
- 不并行修改 `apps/web/package.json` 与 lock（依赖安装单线程），其余前端文件可并行但同一提交内按模块拆分。

## 5. 验收对照

| PRD 功能点/验收 | 实现任务 | 验证方式 |
|-----------------|---------|---------|
| 功能点 1：预览入口、约 1s 起播、原始画质、无完整下载/转码 | 5.1/5.4/5.5/5.6 | 4.3/4.4/4.5 + 手工 6.3 |
| 功能点 1：拖动/输入时间定位任意位置、关键帧落点、不卡死 | 5.2/5.3/5.4/5.5 | 4.1/4.2/4.4 + 手工 6.3 |
| 功能点 1：暂停/继续/音量/全屏 | 5.5 | 4.4 |
| 功能点 2：设为起点/终点、手输校验、清空、未选全禁用 | 5.5 | 4.4 |
| 功能点 2：起点对齐关键帧提示 | 5.5 | 4.4 + 手工 6.3 |
| 功能点 3：片段下载、独立可播、原始画质、时长≈区间 | 1.1/1.2/3.1/3.3/3.4/5.6 | 2.1/2.2/2.3 + 手工 6.3 |
| 功能点 3：超上限拒绝、下载不改原文件 | 3.2/3.3 | 2.2 |
| 异常：录制中不可预览/截取 | 5.6（历史列表已排除）+ 3.3（录制中 400） | 4.5/2.2 |
| 异常：AV1 不支持预览、MSE 缺失、Range 探测失败 | 5.4/5.5/5.6 | 4.3/4.4/4.5 |
| 异常：文件不存在/参数非法/生成失败/播放失败/断网 | 3.3/3.4/5.5 | 2.2/2.3/4.4 |
| 非功能：内存有上限、边生成边传输、不影响录制 | 5.4（50MB 预算）/3.1（pipe）/既有架构 | 4.3/2.1 + 手工 6.3 |
| 兼容性：既有下载/Range/音频试听/合并零回归 | 3.3/3.4 不动既有分支 | Phase 6 全量回归 |
| 契约同步、覆盖率 ≥80%、单用例 ≤5s | 1.1-1.3/6.1/6.2 | codegen + 全量测试 + TestReport |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| mpegts 内部字段随版本漂移 | `seekToByteOffset` 依赖 `_player_engine` 等私有字段 | 精确锁定 `1.8.2`；`attachByteOffsetSeek` 集中访问；结构不符显性抛错/返回 false；4.2 单测锁定 |
| ESM/CJS 模块形态差异 | sumire 通过替换包 `main` 包装，本仓库为 Vite ESM | 不替换 `main`，改为显式 attach；动态 import 同一模块实例；构建后手工验证 patch 生效 |
| lazyLoad 暂停死锁 | 续播首个媒体段为音频且旧缓冲已 flush 时可能永停 | 优先视频关键帧 tag；lazyLoad guard（首段/seekpoint/3s 超时恢复）；`directSeek` 对齐；seek 后周期夹齐；手工长录播验证 |
| Range 语义不完整 | 代理/上游忽略 Range 返回 200 会导致整文件 buffer | `probeSeekable` 门控；`seekUnbuffered` 非 206 一律取消 body 并提示；不整文件缓存 |
| 内存增长 | 高码率长录播默认 180s 前瞻击穿 MSE 配额 | 50MB 字节预算、前瞻 60%/回退 40%、duration 到手后 `setBufferLimits` 重算；4.3 断言 |
| FLV tag 扫描误判 | 字节窗口可能命中伪 tag | 三重校验（tagType/dataSize/PrevTagSize）+ 窗口上限 + 视频关键帧优先；无关键帧回退任意 tag；失败显性提示 |
| clip 文件名非 ASCII | `Content-Disposition` 直接拼中文可能触发头校验/乱码 | `ContentDispositionHeaderValue` ASCII 兜底 + `FileNameStar` RFC 5987；2.2 用中文文件名断言 |
| `<a download>` 无法感知 4xx/5xx | PRD 要求「生成失败提示」 | 点击先 `fetch` 预检（响应头到达即 abort）；非 2xx 经 `apiErrorMessage` 提示；2xx 再触发下载；预检会增加一次 ffmpeg 启动，记录为已知成本 |
| 长片段被代理超时截断 | 既有 `RecordingClientName` 默认 100s | 新增专用 `RecordingStreamClientName`（`Timeout.InfiniteTimeSpan`，同 Crawler 视频下载先例）仅供 clip；不改变列表/下载/音频代理；客户端断开联动取消；2.3 覆盖超时映射 |
| `-c copy` 时间轴/AV1 兼容 | 输入 seek + fMP4 可能出现负时间戳/AV1 封装差异 | `-avoid_negative_ts make_zero`；2.2 用 ffprobe 验可解析/时长；AV1 片段手工验收（不支持则记录并回退评审） |
| 手动输入起点非关键帧 | 片段实际起点会吸附到更早关键帧 | 前端提示「起点会对齐到最近关键帧」；预览「设为起点」直接取关键帧落点；PRD 已接受秒级偏差 |
| 预览/切片依赖 ffmpeg 与浏览器能力 | 生产环境 ffmpeg 已具备（recording 镜像）；Safari/iOS MSE 可能缺失 | 仅支持桌面近两年浏览器；MSE 缺失置灰提示；完整/片段下载不受影响 |
| 测试慢/依赖环境 | 真 ffmpeg 合成与 ffprobe 可能超 5s 或无工具 | 合成源 6s/320x240/ultrafast；无 ffmpeg/ffprobe 早退（沿用音频用例）；TestReport 护栏 |
| 无新增服务端资源 | 预览不走服务端，clip 为 I/O bound `-c copy` | 不新增容器/卷/迁移；`VideoClipMaxDurationMs` 有默认值；回滚仅版本回退 |

## 7. 相关文档

- PRD：`docs/product/draft/20261021-recording-preview-clip-prd.md`（v1.1）
- 技术提案：`docs/engineering/proposals/20261021-proposal-recording-preview-clip.md`（v1.1）
- 参考实现 [Deliay/sumire](https://github.com/Deliay/sumire/)：
  - `apps/bookstore/features/recordings/player.ts`（单例、字节预算缓冲、`probeSeekable`）
  - `apps/bookstore/features/recordings/seek.ts`（关键帧预测：估算 → Range 窗口 → FLV tag 扫描 → 字节偏移续播）
  - `patches/mpegts.js@1.8.2.patch`（`seekToByteOffset`/`setBufferLimits`/lazyLoad guard）
- 既有录播 Plan：`docs/engineering/plans/20260913-plan-live-recording.md`、`20260914-plan-recording-download-range.md`、`20260914-plan-recording-file-list-metadata.md`
- 契约：`contracts/recording.openapi.yaml`、`contracts/bilibili-crawler.openapi.yaml`
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`docs/engineering/frontend-rules.md`、`apps/web/AGENTS.md`、`servers/recording/AGENTS.md`、`servers/api/AGENTS.md`
