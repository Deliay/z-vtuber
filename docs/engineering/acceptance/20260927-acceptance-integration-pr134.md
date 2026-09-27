---
description: integration 分支验收报告——识别结果播放偏移（transcript playback offset）合入 release 前的代码 Review 与测试验证结果（PR #134）
type: Permanent
---

# Integration 验收报告：识别结果播放偏移（PR #134）

- **验收日期**: 2026-09-27
- **PR**: https://github.com/Deliay/z-vtuber/pull/134 （integration → release）
- **Review 范围**: `release..integration` 代码差距（7 commits，30 files，+2016/-55；本 PR 增量即 `impl/transcript-playback-offset` 经 PR #133 合入 main 的识别结果播放偏移特性）
- **关联 PRD**: `docs/product/draft/20260927-transcript-playback-offset-prd.md`
- **关联提案/Plan**: `docs/engineering/proposals/20260927-proposal-transcript-playback-offset.md`、`docs/engineering/plans/20260927-plan-transcript-playback-offset.md`
- **验收结论**: **通过**（变更范围内无阻塞问题；附 2 项 P3、2 项 P4 跟进项，及 1 项范围外预先存在失败说明（6 个用例），均不阻塞本次合入）

## 一、变更内容概述

本次 integration 相对 release 的增量为「识别结果播放偏移」单一特性，以任务为单位设置可正可负的偏移（毫秒，±24h），统一作用于三个出口：

1. **契约先行**：`contracts/recording-transcript.openapi.yaml` 新增 `PUT /api/transcripts/task/{taskId}/offset`（`UpdateTranscriptTaskOffsetRequest.offsetMs` 必填、范围 ±86400000 超出 400）、`TranscriptTask.offsetMs` 字段、`TranscriptSegment.start/end` 语义改为「偏移后展示时间」；`contracts/bilibili-crawler.openapi.yaml` 同步代理镜像端点（502 口径）；web `schema*.d.ts` 经 `openapi-typescript` codegen 同步。
2. **数据层**：`TranscriptTaskRecord.OffsetMs`（默认 0、无迁移，存量缺字段文档反序列化为 0）、`TranscriptTaskRepository.UpdateOffsetAsync`（Set OffsetMs + UpdatedAt，按 MatchedCount 返回命中）。
3. **三出口叠加偏移**：任务详情（`TranscriptTasksController.GetTask`）、SSE（`TranscriptSegmentStream` 建流时读一次 OffsetMs，replay 与实时事件统一 +offset，去重键保持原始 `StartTime`）、SRT/ASS 字幕（`TranscriptSubtitleController` + 既有 `SubtitleRenderer` 负值钳 0）。映射层 `ToSegmentDto` 不钳制负值（保持 `start` 单射，避免 web `mergeSegments` 以 start 为键互相覆盖）。
4. **代理镜像**：`servers/api` `RecordingTranscriptProxyController` 新增 `PUT transcripts/task/{taskId}/offset`（既有 `ForwardAsync` 已支持 PUT body/Content-Type 透传，400/404 原样透传、不可达 502）。
5. **web**：任务详情标题行「播放偏移」设置（秒输入、step 0.1、可正可负、±24h 本地校验、`Math.round(seconds*1000)` 保存），成功后清空已合并片段、重拉详情、重建 SSE；`SegmentAudioPlayer` 试听区间钳 `[0, ∞)`、整句越界（`end<=0`）播放按钮置灰；4 份 AGENTS.md 同步。

## 二、验证结果

- **构建**: `dotnet build` **0 错误**（29 warnings 均为 xUnit/nullable 分析器既有告警；NU1903（SSH.NET 2025.1.0、Microsoft.OpenApi 2.0.0）为 release 已存在告警，非本次引入）。
- **测试（本次差距相关）**: `dotnet test --filter "Transcript|RecordingTranscriptProxy"` **192/192 全部通过**（含 Testcontainers Mongo/Redis 集成测试）。本次新增用例：`TranscriptTaskOffsetApiTests` 17 例、`TranscriptMappingTests` 7 例、`TranscriptApiTests` SSE 偏移 1 例、`TranscriptTaskRepositoryTests` 4 例、`RecordingTranscriptProxyControllerTests` 4 例。
- **测试（web）**: `npm test -- --run` **188/188 全部通过**（新增 `TaskListTab.test` 5 例、`SegmentAudioPlayer.test` 3 例、`client.test` 2 例）；`npx tsc --noEmit` 0 错误；`npm run build` 成功。
- **契约一致性**: `npm run codegen` 对 6 份契约重新生成 **0 diff**，提交的 `schema*.d.ts` 与契约完全一致。
- **测试（全量）**: `dotnet test` 1151 例中 **1145 通过、6 失败**——6 例全部位于本次差距未触及的区域（详见问题 1），在 merge-base（a747b55）、release、integration 三处代码逐字节一致，判定为**预先存在、非本次引入**。
- **PRD 验收点逐条核对**:

| PRD 验收点 | 判定 | 证据 |
|---|---|---|
| 功能点1：每任务独立设置、互不影响 | ✅ | 偏移仅写 `transcript_tasks.OffsetMs`，任务级字段（`TranscriptTaskOffsetApiTests.UpdateOffset_*`） |
| 功能点1：正值/负值/0，0.1 秒精度 | ✅ | 2500/-1500/0 用例全过；web `step=0.1` + `Math.round(seconds*1000)`（2.5→2500、-1.5→-1500 用例） |
| 功能点1：即时生效、持久保存、回显当前值 | ✅ | `UpdateOffsetAsync` 持久化 + 响应回显 `offsetMs`；web 保存后重拉详情、输入框回显（测试断言） |
| 功能点1：不需重新识别、已识别内容不变 | ✅ | 仅 `$set OffsetMs/UpdatedAt`，consumer/识别链路零改动 |
| 功能点2：展示起止时间按偏移后口径 | ✅ | `GetTask_WithOffset_ShiftsSegmentTimesAndReportsOffsetMs` |
| 功能点2：试听区间与展示一致（点哪句听哪句） | ✅ | `SegmentAudioPlayer` 钳制用例（URL 为偏移后时间、起点钳 0、越界置灰） |
| 功能点2：修改偏移后再次试听生效 | ✅ | web 保存后清空片段 + 重拉 + 重建 SSE（测试断言 `getTask` 二次调用、旧 EventSource closed、新流建立） |
| 功能点2：进行中任务新片段同口径 | ✅ | `Sse_OffsetShiftsReplayAndLiveDataAndKeepsRawStartTimeAsDedupKey`（replay + 实时 + 去重键原始值） |
| 功能点3：SRT/ASS 时间轴按偏移后生成 | ✅ | `Subtitle_SrtWithOffset`/`Subtitle_AssWithOffset` golden 用例 |
| 功能点3：修改后重新下载为新口径 | ✅ | 字幕导出每次实时读 `task.OffsetMs`，无缓存 |
| 功能点3：未设置偏移与现状完全一致 | ✅ | `GetTask_LegacyTaskWithoutOffsetField`/`LegacyDocWithoutOffsetMsField` + 既有字幕 golden 全绿 |
| 边界：偏移后 < 0 | ✅ | 试听从 0 开始（钳制用例）、字幕条目钳 0（`Subtitle_NegativeOffset_ClampsEntryTimesToZero`）、整句越界置灰 |
| 边界：超出录播结尾 | ✅ | `<audio>` 播到结尾即止（既有行为，无改动） |
| 边界：非法输入不生效并提示 | ✅ | web 本地校验拒绝（不发请求）+ 服务端 400 保持原值（用例 ×2） |
| 边界：历史任务（含缺字段文档）偏移 0 | ✅ | 两个 legacy 用例（BsonDocument 缺字段反序列化为 0） |
| 边界：旧口径内容残留 | ✅ | 保存成功后清空已合并片段再重拉（测试断言） |
| 边界：极端大偏移（>24h）拒绝保存 | ✅ | 前端 999999 秒拒绝；服务端 ±86400001 → 400，±86400000 边界接受（用例齐全） |
| 非功能：安全（不新增公开访问面） | ✅ | 仅在既有 `/api/transcripts/**` 代理与转写服务上加 PUT，与既有端点同口径 |
| 非功能：兼容（无迁移、不影响识别） | ✅ | 无迁移键/回填，consumer 零改动 |

- **提案决策核对**: 映射层不钳制负值（保持 `start` 单射）、钳制在消费侧（字幕渲染既有 + web 试听）、毫秒存储 ±24h、SSE 建流读一次偏移（web 保存后主动重连）——均已按提案落地，且有对应断言钉死。

## 三、发现的问题

### P2（预先存在，非本次引入，不阻塞本次合入）- 全量测试 6 例失败，均为测试与既有实现不一致

`dotnet test` 全量 1151 例中 6 例失败：`RecordingFileNameTests.Render_Raw_BuildsExpectedTemplate`、`Render_KeepsRoomIdAndAnchor_WhenTitleOverlong`、`FfmpegProcessBuilderTests.Av1TargetBitrate_IsNinetyPercent`、`Av1TargetBitrate_FallsBackToDefault_WhenMetadataMissing`、`WeiboTimelineJobTests.RunOnce_RespectsMaxPagesPerCrawl`、`RunOnce_PageAllExisting_Converges_NoSecondFetch`。

**判定为预先存在的证据**：这 3 个测试文件及对应实现在 merge-base（a747b55）、origin/release、origin/integration 三处逐字节一致（`git diff origin/release origin/integration` 对相关文件为空），且本 PR 的 30 文件差距（+2016/-55）完全不触及 recording 文件名/ffmpeg 构建/微博抓取链路——合并前后这些用例行为完全一致。

失败性质（测试断言与既有实现不符）：
- `RecordingFileNameTests` ×2：断言期望 `[RAW][123][某主播] 2026-09-13-晚间歌回.flv`（`] ` 后带空格、日期到天），实现 `RecordingFileName.Render` 输出 `[RAW][123][某主播]2026-09-13-12-00-00-晚间歌回.flv`（无空格、含 `HH-mm-ss` 时间戳）；
- `FfmpegProcessBuilderTests.Av1TargetBitrate_*` ×2：断言在参数中找 `-b:v {90%}k`，而 `FfmpegProcessBuilder.cs:52-60` 只输出 `-maxrate:v/-minrate:v`（`Av1BitrateRatio=0.9` 在此计算），参数里根本没有 `-b:v`，`IndexOf` 返回 -1 后与 `args[0]`（`-hide_banner`）比对而失败——需独立核实是实现丢失了 `-b:v`（AV1 二压目标码率可能未真正生效）还是测试未随实现更新；
- `WeiboTimelineJobTests` ×2：`MaxPagesPerCrawl=2` 时断言翻页次数 `>=2` 失败（实际收敛为 1 次）、全存量收敛用例 `CalledSinceIds` 断言不符——需核实 `WeiboTimelineJob` 翻页/收敛逻辑与测试口径的偏离方向。

**建议**：作为独立任务修复（补 `-b:v` 或更新断言、对齐文件名模板、核实微博翻页收敛语义），不与本特性绑定。是否阻塞合入请人类最终 Review 裁量；本报告不阻塞，理由是本次差距零触及、合并不改变这些用例的任何行为。

### P3-1 - web 保存失败时把一切错误（含 502/网络错误）都提示为「偏移无效」

`TaskListTab.tsx` `handleSaveOffset` 的 `catch` 统一 `setOffsetError('偏移无效')`。契约明确区分 400（偏移无效）与 502（transcript 服务不可达）；当服务端不可达时提示「偏移无效」具有误导性（用户会误以为数值不对而反复重试）。建议按 `ApiError.status` 区分提示文案。不影响功能正确性（原值保持、可重试）。

### P3-2 - 「0.1 秒精度」仅是描述口径，服务端接受任意毫秒值

PRD/契约描述「0.1 秒精度」，实际 web `step=0.1` 只约束输入控件，`Math.round(seconds*1000)` 允许 0.05 秒等更细粒度落库，服务端也不做 0.1 秒步长校验。按「支持 0.1 秒精度」解读可接受（不违反验收标准），建议契约描述措辞对齐或服务端补步长校验。

### P4 - 次要

- 多客户端场景：SSE 流存续期间偏移不重读（建流时读一次），其他标签页/客户端修改偏移后，已打开页面停留在旧口径直到重新打开——PRD 边界表明确允许「切换/重新打开识别结果后为新口径」，提案亦记录为决策，符合预期，仅提醒知悉。
- `PUT offset` 校验顺序为「taskId 非法→404，再 body 校验→400，最后任务存在性→404」：offsetMs 越界 + 任务不存在时返回 400 而非 404。契约未定义两者同时非法时的优先级，实现可接受。

## 四、结论

本次 `release..integration` 差距（识别结果播放偏移）**功能完整、口径一致、测试充分**：PRD 三个功能点与全部异常边界均有实现与断言覆盖，契约/codegen/三出口/代理/AGENTS.md 同步齐全，TDD 提交链（PRD → Plan → 契约 → 测试红 → 实现绿 → 文档）完整。变更范围内**无 P1/P2 阻塞问题**，判定**通过**，交人类最终 Review 确认后合入 `release`。范围外 6 例预先存在失败（问题 1）建议排期独立修复。
