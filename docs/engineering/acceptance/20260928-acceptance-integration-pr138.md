---
description: integration 分支验收报告——AI 训练模块第一期「音频数据集」合入 release 前的代码 Review 与测试验证结果（PR #138）
type: Permanent
---

# Integration 验收报告：AI 训练 - 音频数据集（PR #138）

- **验收日期**: 2026-09-28
- **PR**: https://github.com/Deliay/z-vtuber/pull/138 （integration → release）
- **Review 范围**: `release..integration` 代码差距（15 commits，63 files，+9486/-34；基线 `release` = b1afc41，`integration` = 85f2f9a = origin/main 最新）
- **关联 PRD**: `docs/product/draft/20260927-training-audio-dataset-prd.md`
- **关联提案/Plan**: `docs/engineering/proposals/20260927-proposal-training-audio-dataset.md`、`docs/engineering/plans/20260927-plan-training-audio-dataset.md`
- **上游 Review 意见**: 无（`6ab9475286b810ca6d6d59ff-修复Review意见-产物-Review处理结论`：reworkCount=0、rejectInfo=null、PR #138 reviews=[] / comments=[]）
- **验收结论**: **通过**（变更范围内无 P1/P2 阻塞问题；附 1 项 P3、2 项 P4 跟进项，及 1 项范围外预先存在失败说明（6 个用例），均不阻塞本次合入）

## 一、变更内容概述

本次 integration 相对 release 的增量为「AI 训练模块第一期：音频数据集」单一特性，以全新 `training` 服务 + 全新 web「训练」分组承载，**transcript / recording / consumer 既有能力零改动**：

1. **契约先行**：新增 `contracts/training.openapi.yaml`（8 端点：数据集 list/create/rename/delete、条目 list、`items/import`、`items/{itemId}` PUT、`items/batch-delete`）；`contracts/bilibili-crawler.openapi.yaml` 同步 **api 代理镜像契约**（+412 行，502 口径 `training service unavailable`）；web `schema-training.d.ts` / `schema.d.ts` 经 `openapi-typescript` codegen 同步。
2. **数据层（`servers/common`）**：`TrainingDatasetRecord` / `TrainingDatasetItemRecord` + `TrainingDatasetRepository` / `TrainingDatasetItemRepository`；`MongoDbInitializer.EnsureTrainingIndexesAsync` 建 `(Type, Name)` 唯一（重名 409）、`(DatasetId, AudioKey)` 唯一（导入去重与标识唯一性基础）+ `(DatasetId, AudioType)` / `(DatasetId, Source)` 过滤索引。**无迁移、无回填**。
3. **training 服务（`servers/training`，新增，本地 8086）**：`Program.cs`（Mongo + 仓储 + `TranscriptApiClient` named client + 启动建索引 + `/health`）、`TrainingDatasetsController` / `TrainingDatasetItemsController`、`Contracts/Dtos.cs`（`JsonElement` 区分「字段缺失 / 显式 null」）、`Contracts/TrainingMapping.cs`、`Clients/TranscriptApiClient.cs`、`MongoDbHealthCheck`、Dockerfile + `.github/workflows/build-training-image.yml`；`servers/Z.Vtuber.slnx` 注册，`infra/apphost/AppHost.cs` 接入 Aspire 编排（training → mongo，api → training）。
4. **api 代理镜像（`servers/api`）**：`TrainingProxyController` 8 端点（query/body/Content-Type/上游状态码与响应体透传，不可达或超时 502），`Program.cs` 注册 named client `TrainingService`。
5. **web（`apps/web`）**：主菜单 `menuGroups` 分组结构 → 新增「训练」分组 →「数据集」入口；`DatasetsPage` 多 Tab 容器（本期仅「音频数据集」）；`DatasetSelector`（下拉 + 新建/重命名/删除，二次确认、重名提示）；`TranscriptImportDialog`（模态弹窗，仅列 `status=DONE` 任务，反馈 imported/skipped）；`DatasetItemList`（七字段、四字段内联编辑、多选批量删除、分组浏览 + 分组头全选）；`DatasetItemWaveform` + `waveformLoader.ts`（wavesurfer.js 波形试听，本仓库唯一新增 npm 依赖，经抽象可注入 mock）；`api/client.ts` 新增 9 个训练 API + `trainingAudioClipUrl` + `apiErrorMessage` 错误码中文映射；`utils/format.ts` 新增 `parsePosition`。4 份 AGENTS.md 同步。

## 二、验证结果

- **构建**: `dotnet build Z.Vtuber.slnx` **0 错误**（29 warnings 均为 xUnit/nullable 分析器既有告警；NU1903（SSH.NET 2025.1.0、Microsoft.OpenApi 2.0.0）为 release 已存在告警，非本次引入）。
- **测试（.NET 全量）**: `dotnet test` **1286 例中 1280 通过、6 失败**（耗时 5m57s，含 Testcontainers Mongo/Redis 集成测试）。较 PR #134 报告的 1151 例**净增 135 例，全部为本次差距新增的训练相关用例**，且新增用例 100% 通过。6 例失败全部位于本次差距未触及的区域（详见问题 1）。
  - 新增测试文件 8 个：`TrainingMappingTests`（13）、`TrainingDatasetRepositoryTests`（12）、`TrainingDatasetItemRepositoryTests`（14）、`TrainingDatasetsApiTests`（14）、`TrainingDatasetItemsApiTests`（17）、`TrainingImportApiTests`（12）、`TrainingProxyControllerTests`（19）、`TranscriptApiClientTests`（3）。
- **测试（web）**: `npm test -- --run` **263/263 全部通过**（15 个测试文件；较 PR #134 报告的 188 例净增 75 例）。新增：`client.test`（训练 API + `apiErrorMessage`）、`DatasetsPage.test`（12）、`DatasetSelector`（含于 DatasetsPage 测试）、`TranscriptImportDialog.test`（9）、`DatasetItemList.test`（16）、`DatasetItemWaveform.test`（12）、`waveformLoader.test`（7）、`App.test`（菜单分组）。
- **类型检查 / 构建（web）**: `npx tsc --noEmit` **0 错误**；`npm run build` 成功（`tsc -b && vite build`，wavesurfer.js 正常拆分为独立 chunk）。
- **契约一致性**: `npm run codegen` 对 **7 份契约**重新生成 **0 diff**（`git status --porcelain` 为空），提交的 `schema*.d.ts` 与契约完全一致；api 代理镜像契约与 `TrainingProxyController` 逐端点对齐。
- **PRD 验收点逐条核对**:

| PRD 验收点 | 判定 | 证据 |
|---|---|---|
| 功能点1：导航出现「训练」分组 + 「数据集」入口 | ✅ | `App.tsx` `menuGroups`（缺省组不渲染标题，既有 5 项渲染不变）+ `App.test` |
| 功能点1：进入后为多 Tab 页面，当前仅「音频数据集」Tab | ✅ | `DatasetsPage` `TABS=[{key:'audio'}]` `role="tablist"`，结构可扩展 |
| 功能点2：下拉框列出全部音频数据集并可切换 | ✅ | `DatasetSelector` + `listTrainingDatasets`（`GET /api/training/datasets?type=audio`，CreatedAt 升序） |
| 功能点2：新建只需输入名字 | ✅ | `createTrainingDataset`（`type` 固定 `audio`），trim 非空 ≤128（`TrainingDatasetsApiTests`） |
| 功能点2：可对当前数据集重命名 | ✅ | `renameTrainingDataset`（PUT），回读落库结果（`TrainingDatasetsApiTests`） |
| 功能点2：删除需二次确认，删除后数据集及其数据不再可见 | ✅ | `window.confirm`（文案含「数据一并清除」）+ 服务端级联 `DeleteByDatasetAsync` → `DeleteAsync`（`TrainingDatasetsApiTests.Delete_*`） |
| 功能点2：新建/重命名重名时提示且不生效 | ✅ | `(Type, Name)` 唯一索引 → 409 `dataset name exists` → web「数据集名字已存在」；DB 抛 `MongoWriteException`/`MongoBulkWriteException` 双兜底 |
| 功能点3：未选定数据集时不显示导入入口 | ✅ | `DatasetsPage`：`selectedId && <button>从 Transcript 导入`（`DatasetsPage.test` 断言） |
| 功能点3：仅可选择识别完成的 Transcript | ✅ | `TranscriptImportDialog` `listTranscriptTasks({status:'DONE'})` + 客户端二次过滤 `task.status==='DONE'`；服务端 `Import` 非 DONE → 400 `task not done`（`TrainingImportApiTests.Import_NotDoneTask_*` PENDING/RUNNING/FAILED） |
| 功能点3：导入后每条数据的类型/来源/地址/标识/起止时间/文本与识别结果一致 | ✅ | `Import_DoneTask_MapsItemFields`（`TrainingImportApiTests`）逐字段断言：`audioType=transcript`、`source=taskId`、`audioUri=b/live/{roomId}/recordings/{file}`、`audioKey=transcript:{taskId}:{rawStart}`、起止/文本取详情返回值 |
| 功能点3：设置了播放偏移的 Transcript，导入起止时间为偏移后口径 | ✅ | `Import_OffsetTask_UsesShiftedTimesAndRawStartAudioKey`：`StartMs/EndMs` = 详情返回值（已含 `task.OffsetMs`），仅 `AudioKey` 用原始 start |
| 功能点3：重复导入不产生重复数据，并反馈导入/跳过数量 | ✅ | `Import_Reimport_SkipsAllDuplicates` → `imported=0, skipped=N`；web `TranscriptImportDialog` 「已导入 X 条，跳过 Y 条」 |
| 功能点4：列表完整展示七个字段 | ✅ | `List_ReturnsSevenFields_OrdersByCreatedAtThenAudioKey`（`TrainingDatasetItemsApiTests`）+ `DatasetItemList` 七列 + `colgroup` 列宽策略 |
| 功能点4：音频地址/文本/开始/结束时间可修改并保存生效 | ✅ | `Update_AudioUriOnly_*` / `Update_TextOnly_*` / `Update_StartMsOnly_*` / `Update_EndMsOnly_*` / `Update_CombinedFields_*`（缺失字段保持不变） |
| 功能点4：结束时间可置空（到音频末尾） | ✅ | `Update_EndMsExplicitNull_ClearsEndMs`；`DatasetItemList` 置空输入 + `parsePosition` 空串 → `endMs: null` |
| 功能点4：音频标识在数据集内唯一且不可编辑 | ✅ | `(DatasetId, AudioKey)` 唯一索引 + `TrainingDatasetItemUpdateRequest` 只有 4 个字段（`audioKey` 无更新通道）；`DatasetItemList` 不渲染编辑态 |
| 功能点4：非法时间（负值、结束早于开始）提示且不保存 | ✅ | 服务端 `Update_NegativeStartMs_Returns400InvalidTimeRange`、`Update_EndMsNotGreaterThanStart_Returns400InvalidTimeRange`；web `parsePosition` 正则只接受非负 `m:ss[.S]`，`endMs<=startMs` 拦截并提示不发请求 |
| 功能点5：点击列表项后展示该条音频的波形 | ✅ | `DatasetItemWaveform` + wavesurfer.js，点击行在正下方展开行（`colSpan=9`）插波形，同一时刻仅一行展开 |
| 功能点5：可播放/停止，播放区间与开始/结束时间一致 | ✅ | `waveformLoader.test` 锁定**片段相对时间轴** `play(0, (endMs-startMs)/1000)`（url 已服务端裁剪，按录播绝对秒 seek 会落到片段外致无声）；`DatasetItemWaveform` 播放/停止按钮 |
| 功能点5：结束时间为空时播放到末尾（或预览上限） | ✅ | `endMs` 空 → `startMs + 300000`（`WAVEFORM_PREVIEW_MAX_MS`）并标注「试听预览上限 5 分钟」；与 recording 片段端点 `AudioClipMaxDurationMs` 既有硬约束一致，recording 零改动 |
| 功能点5：音频地址失效时提示音频不可用，不影响其他操作 | ✅ | `trainingAudioClipUrl` uri 不可解析返回 `null` → `status='error'` → `role="alert"`「音频不可用」；加载失败走 `onError`；**播放失败**（`play()` reject）单列「音频暂不可用」并保留播放/停止可重试，不与加载失败混用（`DatasetItemWaveform.test` 12 例覆盖降级矩阵） |
| 功能点6：可勾选多条并一键删除 | ✅ | `DatasetItemList` 勾选 + `batchDeleteTrainingDatasetItems`，`BatchDelete_PartialUnknownIds_DeletesRestAndReturnsCount` |
| 功能点6：删除需确认，删除后列表刷新 | ✅ | `window.confirm` + 成功后刷新 |
| 功能点7：不分组/按来源/按类型切换 | ✅ | `groupMode` 三态 + `groups` 客户端聚合（`DatasetItemList.test`） |
| 功能点7：分组头勾选/取消勾选该分组全部项目 | ✅ | `toggleGroup(groupIds)`（全选/半选语义：`allIn` 则移除、否则补齐） |
| 功能点7：分组勾选后可直接批量删除 | ✅ | 分组勾选写入同一 `selected`，与批量删除按钮联动 |
| 边界：数据集为空 | ✅ | 空态「暂无音频数据，可从 Transcript 导入」 |
| 边界：导入的 Transcript 缺少可用音频地址 | ✅ | `Import_EmptyFile_ImportsWithEmptyAudioUri`（`file` null/空白 → `AudioUri=""` 照常导入）+ 试听提示「音频不可用」+ 可手工修订地址 |
| 非功能：安全（不新增公开访问面） | ✅ | 全部端点挂在既有 `/api/**` 管理后台口径下，无匿名端点；training 仅内网（Aspire `WithReference`，api 经 named client 访问） |
| 非功能：兼容（不影响既有录播/识别/字幕） | ✅ | `git diff --stat` 中 recording / recording-transcript-* / consumer 零文件；`transcript_*` 集合与索引零改动；无迁移键、无回填 |
| 非功能：性能（千级数据集流畅、导入不阻塞） | ✅ | 列表本期全量 + `table-layout:fixed`/`colgroup` 列宽约束（避免撑宽表格）；分组/多选在客户端聚合（一次拉全）；导入为单次批量 `InsertMany(IsOrdered=false)` |

- **提案/Plan 决策核对**:
  - **偏差 1（Plan §1.0，提案 §6.1 决策 4 的细化）——AudioKey 用「原始 start」而非「偏移后 start」**：已按 Plan 落地（`TrainingMapping.GenerateAudioKey(taskId, startMs - offsetMs)`），并有两处锁定断言钉死——`ToImportItem_OffsetChangedAfterFirstImport_KeepsSameAudioKey`（纯函数层）与 `Import_AfterOffsetChange_ReimportStillSkipsAll`（API 层，任务偏移变更后重复导入仍 `imported=0`）。该口径是 PRD 成功标准「重复导入同一 Transcript 不产生重复数据」的必要条件（任务级偏移可变，若用偏移后 start，偏移变更后重复导入会产生整份重复数据）；`StartMs/EndMs/Text` 仍为偏移后展示口径，满足「导入时间尊重 offset」。
  - **偏差 2（Plan §1.0，提案遗漏部署物）——Dockerfile + 镜像流水线**：已补齐（`servers/training/Dockerfile` 多阶段 sdk→aspnet，`.github/workflows/build-training-image.yml` 照抄 recording-transcript-api 模式，paths 含 `servers/training/**` + `servers/common/**`）。
  - **决策 5（试听 5 分钟上限）**：`endMs` 空 → `startMs+300000` + 界面标注，recording 片段端点零改动。已落地。
  - **决策 6（千级全量返回 + 契约预留分页扩展位）**：已落地（`TrainingDatasetItemList` 无分页字段，query 预留）。
  - **「不做」清单核对**：consumer/ASR、transcript 既有端点、recording 片段端点、情感/二次 ASR（`Extensions` 仅预留且有 `ToItemDto_WithExtensions_EmitsJsonObject` 断言）、多 Tab/多音频类型（仅结构预留）、条目分页——**均未越界实现**。

## 三、发现的问题

### 问题 1（P2，预先存在，非本次引入，不阻塞本次合入）- 全量测试 6 例失败，均为测试与既有实现不一致

`dotnet test` 全量 1286 例中 6 例失败：`RecordingFileNameTests.Render_Raw_BuildsExpectedTemplate`、`RecordingFileNameTests.Render_KeepsRoomIdAndAnchor_WhenTitleOverlong`、`FfmpegProcessBuilderTests.Av1TargetBitrate_IsNinetyPercent`、`FfmpegProcessBuilderTests.Av1TargetBitrate_FallsBackToDefault_WhenMetadataMissing`、`WeiboTimelineJobTests.RunOnce_RespectsMaxPagesPerCrawl`、`WeiboTimelineJobTests.RunOnce_PageAllExisting_Converges_NoSecondFetch`。

**判定为预先存在的证据**：本次 63 文件差距（+9486/-34）**零触及** recording 文件名 / ffmpeg 构建 / 微博抓取链路；3 个测试文件与 3 个对应实现在 merge-base（b1afc41）、`origin/release`、`origin/integration` 三处**逐字节一致**（`git diff` 为空），合并前后这些用例行为完全一致。该 6 例与 PR #134 验收报告（`20260927-acceptance-integration-pr134.md` 问题 1）记录的是**同一批**失败，未被修复。

失败性质（测试断言与既有实现不符）：
- `RecordingFileNameTests` ×2：断言期望 `[RAW][123][某主播] 2026-09-13-晚间歌回.flv`（`] ` 后带空格、日期到天），实现 `RecordingFileName.Render` 输出 `[RAW][123][某主播]2026-09-13-12-00-00-晚间歌回.flv`（无空格、含 `HH-mm-ss` 时间戳）；
- `FfmpegProcessBuilderTests.Av1TargetBitrate_*` ×2：断言在参数中找 `-b:v {90%}k`，而 `FfmpegProcessBuilder` 只输出 `-maxrate:v/-minrate:v`，参数里根本没有 `-b:v`，`IndexOf` 返回 -1 后与 `args[0]`（`-hide_banner`）比对而失败——需独立核实是实现丢失了 `-b:v`（AV1 二压目标码率可能未真正生效）还是测试未随实现更新；
- `WeiboTimelineJobTests` ×2：`MaxPagesPerCrawl=2` 时断言翻页次数 `>=2` 失败（实际收敛为 1 次）、全存量收敛用例 `CalledSinceIds` 断言不符——需核实 `WeiboTimelineJob` 翻页/收敛逻辑与测试口径的偏离方向。

**建议**：作为独立任务修复（补 `-b:v` 或更新断言、对齐文件名模板、核实微博翻页收敛语义），不与本特性绑定。是否阻塞合入请人类最终 Review 裁量；本报告不阻塞，理由是本次差距零触及、合并不改变这些用例的任何行为，且 PR #134 已按同一口径放行。

### 问题 2（P3）- 条目更新对非整数 / 越界 JSON 数字返回 500，而非契约约定的 400

**问题**：`servers/training/Controllers/TrainingDatasetItemsController.cs` `Update` 在判定 `startKind`/`endKind` 为 `JsonValueKind.Number` 后直接调用 `request!.StartMs.GetInt64()` / `request!.EndMs.GetInt64()`。`JsonElement.GetInt64()` 对**非整数或超出 Int64 的 JSON 数字**抛 `FormatException`，training 服务 `Program.cs` 无异常处理中间件，最终返回 **500**。

**实证**（.NET 10 `System.Text.Json`，独立探针复现）：

| 请求体片段 | 实际结果 | 契约期望 |
|---|---|---|
| `{"startMs": 1500}` | 正常更新 | ✅ |
| `{"startMs": 1.5}` | `FormatException` → 500 | 400 `invalid time range` |
| `{"endMs": 1e30}` | `FormatException` → 500 | 400 `invalid time range` |
| `{"startMs": 9223372036854775808}` | `FormatException` → 500 | 400 `invalid time range` |

契约 `TrainingDatasetItemUpdateRequest.startMs/endMs` 声明为 `type: integer, format: int64`，`PUT .../items/{itemId}` 的 400 语义为「非法时间区间」。虽然 `1.5` 本身已违反请求 schema（契约未定义 schema 非法输入的行为），但**返回 500 会误导调用方认为服务端故障**，且与「非法输入 → 400」的既有口径不一致。

**影响面**：本仓库 web 端**不可达**——`parsePosition` 只产出整毫秒、`Math.round(seconds*1000)` 只产出整数，不会发出非整数 `startMs/endMs`；仅第三方/手工构造请求可触发。**不涉及数据损坏**（异常发生在读取请求阶段，未写库）。

**建议**：改用 `request!.StartMs.TryGetInt64(out ...)`，失败按 `invalid time range` 返回 400（约 3 行改动 + 2 个用例）。可作为独立小修，不阻塞本次合入。

### 问题 3（P4）- 导入时不校验片段时间区间，异常片段原样落库

**问题**：`TrainingDatasetItemsController.Import` 对 `detail.Segments` 无条件映射落库，未调用 `TrainingMapping.IsValidTimeRange`。若 transcript 任务详情返回 `end <= start` 或 `start < 0` 的脏片段（识别结果异常/上游缺陷），会写入一条违反数据模型不变量的条目，且 `endMs <= startMs` 的条目在编辑时会被 `Update` 以 400 `invalid time range` 拒绝保存（除非先把 `endMs` 置空，因此**可用「结束时间置空」自救**，非死锁）。

**影响面**：需上游返回脏数据才触发，正常识别结果不可达。契约未规定导入侧校验，故不属契约违规。

**建议**：导入时跳过非法片段并计入 `skipped`（或单独计数反馈），或在契约 `importTrainingDatasetItems.description` 中明确「导入不做区间校验，由用户校对修订」。当前实现下建议至少补一条导入侧非法片段的用例锁定现状语义。

### 问题 4（P4）- `TrainingProxyController` 的 `[FromQuery]` 参数声明后未参与转发，易误导维护者

**问题**：`servers/api/Controllers/TrainingProxyController.cs` 的 `ListDatasets([FromQuery] string? type)`、`ListItems(..., [FromQuery] string? audioType, [FromQuery] string? source)` 声明了绑定参数但**从未使用**——实际转发靠 `Request.QueryString.Value` 整体透传。行为正确（query 原样透传、可扩展），但参数是「死声明」，后续维护者若误以为需要手动拼 query 会产生双重参数或漏参数。

**影响面**：零行为影响，纯可维护性。

**建议**：删除未使用的参数（保留 XML 注释说明「query 经 `Request.QueryString` 整体透传」），或在注释中显式标注「参数仅作接口文档用途，不参与转发」。

## 四、结论

本次 `release..integration` 差距（AI 训练模块第一期「音频数据集」）**功能完整、口径一致、测试充分**：PRD 7 个功能点与全部异常边界均有实现与断言覆盖，契约/codegen/8 端点服务/api 代理镜像/web/AGENTS.md/部署物同步齐全，TDD 提交链（PRD → 提案 → Plan → 契约 → 测试红 → 实现绿 → 文档）完整，Plan 两处偏差（AudioKey 原始 start 口径、补部署物）均已落地并有锁定断言。变更范围内**无 P1/P2 阻塞问题**，判定**通过**，交人类最终 Review 确认后合入 `release`。

跟进项（均不阻塞）：
1. **P3** 问题 2：条目更新对非整数/越界 JSON 数字改判 400（3 行 + 2 用例）；
2. **P4** 问题 3：导入侧片段区间校验（或契约明确不校验）+ 现状语义用例；
3. **P4** 问题 4：`TrainingProxyController` 死参数清理/注释；
4. **P2（范围外）** 问题 1：6 例预先存在测试失败（与 PR #134 同批）建议排期独立修复，其中 `FfmpegProcessBuilderTests.Av1TargetBitrate_*` 需优先核实 AV1 二压目标码率是否真的未生效。
