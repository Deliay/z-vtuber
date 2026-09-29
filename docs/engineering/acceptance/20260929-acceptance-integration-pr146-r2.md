---
description: integration 分支验收报告（第 2 轮 / 返工复审）——训练数据集工具优化（PR #146）返工提交 0e2115b 对上轮 4 项 P2 的修复复核结果：全部修复到位，结论通过
type: Permanent
---

# Integration 验收报告（第 2 轮·返工复审）：训练数据集工具优化（PR #146）

- **验收日期**: 2026-09-29
- **PR**: https://github.com/Deliay/z-vtuber/pull/146 （integration → release）
- **本轮 Review 范围**: 返工提交 `0e2115b`（`fix(training): 返工修复 PR #146 的 4 项 P2（Review 打回）及部分 P3/P4`），即对上轮报告 [[20260929-acceptance-integration-pr146]]（结论：不通过，4 项 P2）的修复验证；连同 `release..integration` 全差距复核
- **上游产物**: 修复Review意见节点的「返工修复说明 / 验证结果 / 变更文件清单 / 返工提交」（commit `0e2115b`，已推送 origin/integration）
- **验收结论**: **通过**——上轮 4 项 P2 逐项修复到位且有测试锁定，独立复跑构建/测试/契约一致全部达标；残留 2 项 P3 级未处理项（不阻塞）与少量 P4 提醒，建议随后续小 PR 跟进，可合入 release

## 一、上轮 P2 修复逐项复核

### P2-1 重新识别「保存」静默丢弃内联草稿 — ✅ 已修复

- **修复实现**：`apps/web/src/pages/training/ReRecognizeDialog.tsx` 新增 `draft?: ReRecognizeDialogDraft` 入参；`handleSave` 在有草稿时与内联保存同 payload（`audioUri/startMs/endMs + text`，文本以草稿为起点，仅保存也能保留用户的内联文本修改），非法时间本地拦截「时间输入无效」（校验条件与 `saveEdit` 一致）；`DatasetItemList.tsx` 在该行处于编辑态时透传当前草稿。
- **数据丢失路径闭合验证**：保存返回的 `updated` 含全部字段，`onSaved` 用其重置草稿并退出编辑，不再出现「audioUri/起止时间修改被无声覆盖」。
- **测试**：`DatasetItemList.test.tsx` 原「丢弃草稿」断言已更新为同 payload 语义，新增「submit the inline draft together with the re-recognized text」「submit modified audio uri and time range from the inline draft」2 条用例。

### P2-2 情绪识别链路不校验 `model.Enabled` — ✅ 已修复

- **修复实现**：`TrainingItemToolsController.cs:173`（CreateEmotionTasks）停用模型返回 400 `model disabled`，与重新识别（:65）同口径；`EmotionTaskRunner.cs:52` 执行前同样校验，已入队未执行的任务遇停用置 FAILED `model disabled`（入队校验被绕过时仍兜底）。
- **契约**：`contracts/training.openapi.yaml` emotion-tasks 400 描述补 `model disabled`，`contracts/bilibili-crawler.openapi.yaml` 镜像同步；web codegen `schema-training.d.ts`/`schema.d.ts` 逐字一致。
- **测试**：`EmotionTaskApiTests.CreateEmotionTasks_DisabledModel_Returns400ModelDisabled`、`EmotionTaskRunnerTests.Run_ModelDisabled_MarksFailedWithoutCallingModel`（且断言不调用模型）。

### P2-3 情绪任务状态机并发非原子 — ✅ 已修复（含原建议的两条路径）

- **重复提交幂等**：`TrainingMongoSetup.cs:96-110` 新增 `ItemId_1_Active` **部分唯一索引**（`PartialFilterExpression` 限定 PENDING/RUNNING；infra 与测试容器均为 Mongo 7，`$in` partial filter 受支持）；`TrainingEmotionTaskRepository.cs:71 TryInsertActiveAsync` 捕获唯一键冲突返回 null，控制器计 skipped。`HasActiveAsync` 预检保留作快路径，唯一索引兜住并发（双击/双副本）窗口。
- **Retry 原子 CAS**：`TrainingEmotionTaskRepository.cs:152 RetryFailedAsync` filter 含 `Id + Status==FAILED`，未命中返回 null（控制器返 400 `task not failed`，不再把 RUNNING/DONE 重置为 PENDING）；同条目已有活跃任务时唯一索引冲突由 `TrainingEmotionTasksController.cs:85` 映射 **409 `active task exists`**。契约 409 已补（双契约），`TrainingProxyController.ForwardAsync` 透传上游状态码，网关侧 409 可达前端。
- **测试**：`TrainingEmotionTaskRepositoryTests`（`TryInsertActive_RejectsSecondActiveForSameItem`、`ConcurrentTryInsertActive_OnlyOneSucceeds`、`RetryFailed_OnlyTransitionsFromFailed`、`UpdateRunning_FencesOnStatusAndAttempts`）、`EmotionTaskApiTests`（`CreateEmotionTasks_ConcurrentRepeatSubmit_OnlyOneCreated`、`Retry_WhenAnotherActiveTaskExists_Returns409`）。

### P2-4 锁续期无异常兜底 / 写库 fencing TOCTOU — ✅ 已修复

- **续期健壮性**：`EmotionTaskLock.cs:118 RenewAsync` 捕获 Redis 异常保守置 `Lost` 返回 false（不抛出冲垮续期任务）；续期循环（:160-180）单轮异常记日志后**继续下一轮**而非终止，确认失锁才停止续期。
- **fencing 落到写操作**：`TrainingEmotionTaskRepository.cs:131 UpdateRunningAsync` 以 `Id + Status==RUNNING + Attempts` 为 filter 的 CAS 才应用更新；`EmotionTaskRunner` 先 CAS 到 DONE 写条目、失败路径（`FailAsync`）同样带条件并新增 `handle.Lost` 短路，被接管后不覆盖新持有者状态；`EmotionTaskScanner` 把**领取后（Attempts 已累加）**的记录交 Runner，使 fencing 条件与库中领取者精确匹配，闭合「校验锁 → 写库」TOCTOU。
- **测试**：`EmotionTaskLockTests.RedisUnavailable_RenewTreatsAsLost_WithoutThrowing`、`EmotionTaskRunnerTests.Run_AttemptTakenOverBeforeWrite_DoesNotWriteAndKeepsRunning`。

## 二、随返工处理的 P3/P4 复核

已落实（抽查核实）：

| 项 | 状态 | 证据 |
|---|---|---|
| 失败原因不透传 `ex.Message` | ✅ | `EmotionTaskRunner.MapError` 白名单映射，兜底统一 `emotion task failed` |
| 错误码口径统一 | ✅ | 全仓 `unsupported provider type`（Factory/Discovery/控制器/Runner/ProviderTypeException 映射），含 P4 提过的 discover 502 口径 |
| `SetEmotionAsync` 返回值忽略 | ✅ | 条目写库前被删除改标 FAILED `item not found`（`EmotionTaskRunner.cs:123`） |
| 识别/保存加载文案 | ✅ | 「识别中...」「保存中...」 |
| 防重复提交 | ✅ | 内联保存/批量删除/手动添加模型 busy 守卫（`EmotionSubmitDialog` 原本已有） |
| 任务队列错误码中文化 | ✅ | `apiErrorCodeText` + `EmotionTaskQueue` 错误列映射 |
| 轮询竞态 | ✅ | `loadSeq` 请求序号，慢响应后到不覆盖新数据 |
| SSRF/重定向携带 key | ✅ | LLM named client `SocketsHttpHandler { AllowAutoRedirect = false }`（`Program.cs`） |
| 提案/PRD/Plan/AGENTS 同步 | ✅ | 提案 §6.1 新增 3 行决策（A）、§6.2 安全约束、emotionControlWeight 0~10 钳制回填；PRD 补任务粒度/模型可用性口径；3 份 AGENTS.md 与实现逐条对齐 |

**残留未处理（P3，不阻塞）**：

1. **P3-4 嵌套文件名条目**：含 `/` 的录播文件名条目解析仍恒失败且文案映射为 `audio uri is empty`（`TrainingMapping` 未改）；属既有不可恢复边界，建议区分 empty/unparseable 或导入端拒绝嵌套名。
2. **P3-9 音频全量读入**：`RecordingAudioClient.cs:86` 仍先全量读字节再做大小检查，建议 Content-Length 预检 + 流式限长。
3. 测试缺口小尾巴：「重复识别以最新结果覆盖」显式断言、`EmotionSubmitDialog` busy 防重复专项断言未补（功能已有守卫）。

**P4 提醒（维持上轮登记，不阻塞）**：契约 `limit` 钳制不返 400、import `skipped` 语义宽于契约、模型绑定失败返 ProblemDetails、批量入口无数量上限、OpenAI-like 任意 400 重试一次、ProviderDialog 无撤销 key 入口、提交回执「跳过 N 条」无原因细分等，详见 [[20260929-acceptance-integration-pr146]] 第三节。

## 三、验证结果（AI Review 独立复跑，非仅采信返工自述）

- **构建**: `dotnet build servers/Z.Vtuber.slnx` **0 错误**（21 warnings 与基线一致，均为 xUnit/nullable 分析器既有告警）。
- **测试（dotnet 聚焦，本次差距相关）**: `dotnet test --filter "Training|Emotion|Model|RecordingAudio|CollectionNames"` **433/433 全部通过**（基线 423，+10 新增用例，与返工自述一致；含 Testcontainers Mongo/Redis 集成测试，覆盖并发提交、原子 Retry、fencing、Redis 不可用续期等新场景）。
- **测试（web）**: `npm test -- --run` **325/325 全部通过**（19 个文件；+4 新增用例）。
- **契约一致性**: `npm run codegen` 后 `git diff` **0 diff**，`schema-training.d.ts`/`schema.d.ts` 与双契约变更（emotion-tasks 400 `model disabled`、retry 409 `active task exists`）逐字一致。
- **全量 dotnet test**: 沿用上轮登记的环境问题（本机 Testcontainers Mongo 连接中断批量失败）与预先存在失败（`RecordingFileNameTests`×2、`FfmpegProcessBuilderTests`×2、`WeiboTimelineJobTests`×2，与本差距无关），本轮以聚焦用例集复跑确认，不阻塞验收；建议在稳定环境补一次全量确认。

## 四、结论

上轮 4 项 P2（用户数据丢失、停用模型绕过、状态机并发原子性、锁续期健壮性）**逐项修复到位**：修复方案与上轮建议一致或更优（并发重复提交以部分唯一索引兜底、Retry 原子 CAS + 409、fencing 落到写操作本身、续期异常保守失锁），且每项均有针对性测试锁定；契约/PRD/提案/Plan/文档同步完整，独立复跑构建 0 错误、聚焦测试 433/433、web 325/325、codegen 0 diff。残留 2 项 P3 与少量 P4 为不阻塞改进项，建议随后续小 PR 排期。

**验收结论：通过**，PR #146（`release..integration`）可进入合入 release 流程。
