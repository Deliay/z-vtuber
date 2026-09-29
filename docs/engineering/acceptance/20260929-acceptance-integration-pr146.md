---
description: integration 分支验收报告——训练数据集工具优化（模型设置 / 重新识别 / 情绪识别 / 列表展示优化）合入 release 前的代码 Review 结果（PR #146）：发现 4 项 P2，结论不通过、打回返工
type: Permanent
---

# Integration 验收报告：训练数据集工具优化（PR #146）

- **验收日期**: 2026-09-29
- **PR**: https://github.com/Deliay/z-vtuber/pull/146 （integration → release）
- **Review 范围**: `release..integration` 代码差距（9 commits，85 files，+14514/-484；即 `feat/training-dataset-emotion` 经 PR #144/#145 合入 main 的「训练-数据集工具优化」特性）
- **关联 PRD**: `docs/product/draft/20260928-training-dataset-emotion-prd.md`
- **关联提案/Plan**: `docs/engineering/proposals/20260928-proposal-training-dataset-emotion.md`、`docs/engineering/plans/20260928-plan-training-dataset-emotion.md`
- **上游说明**: PR #146 无人类 Review 意见（reviews/comments/reviewDecision 均为空），无人类打回项需复核
- **验收结论**: **不通过（打回）**——发现 4 项 P2：①重新识别「保存」静默丢弃内联编辑草稿（用户数据丢失路径）；②情绪识别链路不校验 `model.Enabled`（停用模型可执行）；③情绪任务状态机并发非原子（重复提交可双建任务、Retry 可重置 RUNNING/DONE 任务）；④任务锁续期无异常兜底（长任务失锁双跑）。P1 为零，功能主体完整、测试覆盖良好，修复 P2 后可复审

## 一、变更内容概述

本次 `release..integration` 增量为「AI 训练 - 数据集工具优化」单一特性，四大功能点：

1. **模型设置 Tab**：`servers/common/training` 新增 `TrainingProviderRecord`/`TrainingModelRecord`/`TrainingEmotionTaskRecord` 三实体 + 三仓储 + `training_providers`/`training_models`/`training_emotion_tasks` 三集合索引（`(Name)` 全局唯一、`(ProviderId, Model)` 唯一）；`servers/training` 新增 Providers/Models 控制器共 10 端点（CRUD + `models/discover` 从 Endpoint 同步 + `models/import` 批量导入，`(ProviderId, Model)` 冲突计 skipped）；slug `{providerName}/{model}` 服务端拼好返回；**apiKey 仅写入不回显**（DTO 只有 `apiKeySet: boolean`）。
2. **AI 接入层**（`servers/training/Ai/`，仓库首个 LLM 封装）：`IChatModelClient` + `OpenAiLikeChatClient`（`chat/completions`，audio content，`response_format` 失败自动降级重试一次）+ `ClaudeLikeChatClient`（`/v1/messages`）+ `ModelDiscoveryClient`（OpenAI-like `GET {endpoint}/models` / Claude-like `GET {endpoint}/v1/models`）+ `ModelJsonParser`（围栏/裸 JSON 提取、NaN 钳制）+ `ChatModelClientFactory`（按 provider.Type 分派，未知类型抛 `ProviderTypeException`）；零新增 NuGet 包。
3. **单条重新识别**：`TrainingItemToolsController.Recognize`（服务端 `RecordingAudioClient` 取音频片段转 base64 → 携 `RecognizePromptBuilder` 调模型 → JSON 解析 → 仅返回 `{text}` 不写库，保存复用既有 `PUT .../items/{itemId}`）；400 口径含 `model disabled`/`audio uri is empty`/`audio too large`/`model does not support audio input`，502 含 `audio unavailable`/`model service unavailable`/`invalid model output`。
4. **批量情绪识别 + 任务队列**：提交端点（`itemIds`+`modelId`，重复提交/空文本/非法 id 计 skipped）；`EmotionTaskScanner`（HostedService 周期扫 PENDING 与失锁 RUNNING，信号量全局并发门控）+ `EmotionTaskLock`（Redis `training:emotion-task-lock:{taskId}`，SET NX PX + Lua 续期/释放 + fencing）+ `EmotionTaskRunner`（逐阶段 fencing，失锁放弃写入保留 RUNNING 交接管；十字段情绪 JSON 写入条目 `Extensions.emotion`，最新覆盖）；队列端点（状态/进度/counts/modelSlug 快照）+ `retry` 端点；前端 10s 轮询 + FAILED 重试。
5. **web/契约**：`DatasetsPage` 双 Tab；`ModelSettingsTab`/`ProviderDialog`/`ReRecognizeDialog`/`EmotionSubmitDialog`/`EmotionTaskQueue` 新组件；`DatasetItemList` 增「重新识别」编辑态入口、「情绪识别」批量按钮、「展示类型」勾选（位于分组切换之前，音频信息默认隐藏 / 情绪信息默认展示，情绪列十列、空态「-」）；`contracts/training.openapi.yaml` 新增 14 端点、`contracts/bilibili-crawler.openapi.yaml` 代理镜像、`TrainingProxyController` 14 路由；`infra/apphost` training 增 Redis 依赖与 recording 地址；6 份 AGENTS.md 同步。

## 二、验证结果

- **构建**: `dotnet build servers/Z.Vtuber.slnx` **0 错误**（21 warnings 均为 xUnit/nullable 分析器既有告警）。
- **测试（dotnet，本次差距相关）**: `dotnet test --filter "Training|Emotion|Model|RecordingAudio|CollectionNames"` **423/423 全部通过**（含 Testcontainers Mongo/Redis 集成测试；覆盖 `EmotionTaskApiTests`、`EmotionTaskLockTests`、`EmotionTaskRunnerTests`、`EmotionTaskScannerTests`、`ModelDiscoveryClientTests`、`ModelJsonParserTests`、`ModelPromptTests`、`RecordingAudioClientTests`、`TrainingEmotionTaskRepositoryTests`、`TrainingMappingTests`、`TrainingModelApiTests`、`TrainingModelRepositoryTests`、`TrainingMongoSetupTests`、`TrainingProviderApiTests`、`TrainingProviderRepositoryTests`、`TrainingProxyControllerTests`、`TrainingRecognizeTests` 等）。
- **测试（dotnet 全量）**: 全量跑在本机**因环境故障未能完整收尾**：T+4:26 起 MongoDB 连接中断（`MongoConnectionException: Attempted to read past the end of the stream`，`MongoDbFixture.ResetAsync` DropCollection 失败）导致共享 fixture 的 280+ 用例同一瞬间批量失败（含大量本差距零触及的纯单元测试），判定为 Testcontainers/本机 Docker 环境问题而非代码问题，终止后以聚焦用例集重跑验证（见上条 423/423）。连接中断前可见的 4 例失败为 `RecordingFileNameTests.Render_*` ×2、`FfmpegProcessBuilderTests.Av1TargetBitrate_*` ×2，与 `20260927-acceptance-integration-pr134.md` 问题 1 登记的**预先存在失败**完全相同（非本次引入）；建议返工复审时在稳定环境补一次全量确认。
- **测试（web）**: `npm test -- --run` **321/321 全部通过**（19 个文件；本差距新增 `client.test`、`DatasetItemList.test`、`DatasetsPage.test`、`EmotionSubmitDialog.test`、`EmotionTaskQueue.test`、`ModelSettingsTab.test`、`ReRecognizeDialog.test` 等）。
- **契约一致性**: `npm run codegen` 后 `git diff` 为 **0 diff**，提交的 `schema*.d.ts` 与契约完全一致；双契约（training / bilibili-crawler 网关镜像）`Training*` schema 结构化比对字段名/类型/required/枚举**零漂移**；14 端点的契约 ↔ C# 控制器 ↔ 代理三方路径/方法/状态码/错误体 `{"error":...}`/camelCase 字段逐条一致（DTO 显式 `JsonPropertyName`）。
- **PRD 验收点核对**（详细逐条表存档于本轮 Review 记录，此处摘要）:

| 功能点 | 判定 | 说明 |
|---|---|---|
| 功能点1 模型设置 Tab（Providers/Models CRUD、apiKey 不回显、discover/import、slug 展示、删除级联提示） | ✅ | 7 条验收标准全部落实且有测试（`List_...NeverExposesApiKey`、`Delete_CascadesModels` 等） |
| 功能点2 单条重新识别（入口/下拉/回填/失败保文本/防重复提交） | ⚠️ | 5 条中 4 条落实；「保存与内联编辑保存行为一致」**不满足**，见 P2-1 |
| 功能点3 批量情绪识别与任务队列 | ⚠️ | 按钮门控/确认框/队列可见/自动刷新/十字段/失败隔离/最新覆盖均落实；「重复提交幂等」在并发场景**不成立**，见 P2-3a；停用模型绕过见 P2-2 |
| 功能点4 列表展示优化（展示类型勾选、默认态、即时生效、空态、不影响既有能力） | ✅ | 5 条验收标准全部落实且有 DOM 顺序/空态断言 |
| 3.5 异常边界表 9 行 | ⚠️ | 8 行落实；「未配置模型引导」「删除 Provider 提示」「后台继续执行」等均覆盖；停用模型口径缺口见 P2-2 |

## 三、发现的问题

### P2-1 - 重新识别「保存」静默丢弃内联编辑草稿（用户数据丢失路径），与 PRD「保存行为一致」不符

- **位置**：`apps/web/src/pages/training/ReRecognizeDialog.tsx:90`、`apps/web/src/pages/training/DatasetItemList.tsx:530-548`
- **问题**：PRD 功能点 2 明确「『保存』与内联编辑的保存行为一致（保存文本等字段并关闭对话框）」。内联保存提交 4 字段（`DatasetItemList.tsx:238-243` 提交 `audioUri/text/startMs/endMs`），而对话框保存只提交 `{ text }`。用户在内联编辑态修改了音频地址/起止时间（未保存）→ 打开「重新识别」→ 识别并保存后，`onSaved` 用服务端返回值重置草稿并 `cancelEdit()`，**未保存的 audioUri/起止时间修改无任何提示即被丢弃**。
- **证据**：
  ```ts
  // ReRecognizeDialog.tsx:90 —— 只提交 text
  const updated = await updateTrainingDatasetItem(datasetId, item.id, { text })
  // DatasetItemList.tsx:537-546 —— 重置草稿并退出编辑，草稿中 audioUri/start/end 修改被无声覆盖
  if (editingId === updated.id) {
    setDraft({ audioUri: updated.audioUri, text: updated.text, start: ..., end: ... })
    cancelEdit()
  }
  ```
- **建议**：对话框保存时把当前内联草稿的 `audioUri/startMs/endMs` 一并提交（与内联保存同一 payload 构造），或在丢弃草稿前确认提示「将放弃本行未保存的时间/地址修改」。相关测试（`DatasetItemList.test.tsx`「exit inline editing after a re-recognize save…」）目前把丢弃行为当预期锁定，需同步更新。

### P2-2 - 情绪识别链路不校验 `model.Enabled`，停用模型仍可提交并执行（与重新识别口径矛盾）

- **位置**：`servers/training/Controllers/TrainingItemToolsController.cs`（`CreateEmotionTasks` 模型校验段，约 :165-179，无 `Enabled` 检查）、`servers/training/Emotion/EmotionTaskRunner.cs:42-54`（执行同样不查）；对照 `TrainingItemToolsController.cs:63-66`（重新识别返回 400 `"model disabled"`）
- **问题**：单条重新识别严格校验 `model.Enabled`，情绪任务的创建与执行都不校验——「停用」只对重新识别生效。前端下拉虽只列启用模型（`listTrainingModels(true)`），但 API 层面用停用模型提交情绪任务会一路执行成功；契约 `training.openapi.yaml` emotion-tasks 端点的 400 也没有 `model disabled`，三处口径一致地缺失，属语义缺口。
- **建议**：`CreateEmotionTasks`（或 `EmotionTaskRunner` 执行时）与 `Recognize` 同口径校验 `Enabled`，并同步补契约 400 描述与测试；若有意采用「入队快照、停用不影响在途任务」语义，则须在契约 description 与 PRD 明确写出。

### P2-3 - 情绪任务状态机并发非原子：重复提交可双建任务；Retry 可把 RUNNING/DONE 任务重置为 PENDING

- **位置**：`servers/training/Controllers/TrainingItemToolsController.cs:208` + `servers/common/training/Persistence/TrainingEmotionTaskRepository.cs:68-75`（`HasActiveAsync`）；`servers/training/Controllers/TrainingEmotionTasksController.cs:64-74` + `TrainingEmotionTaskRepository.cs:96-104`（`UpdateAsync`）
- **问题**：
  1. **重复提交幂等为 check-then-act**：`HasActiveAsync` 查询与 `InsertAsync` 插入之间无原子保证（`ItemId_1` 索引非唯一），并发提交（双击/双副本前端）可各建一个 PENDING 任务 → 同条目双任务、重复 LLM 调用，违背 PRD 功能点 3「重复提交幂等」；
  2. **Retry 先读后写、更新无状态条件**：先读 `current.Status==FAILED` 再 `UpdateAsync(id, update)`，而 `UpdateOneAsync(t => t.Id == id, ...)` 只按 Id 匹配。并发双击 retry 或与扫描器交错时，第二次更新可把已被领取（RUNNING）甚至已完成（DONE）的任务重置为 PENDING（`StartedAt/FinishedAt` 被清）→ 重复执行、重复调用模型、覆盖 emotion。
- **证据**：
  ```csharp
  // TrainingItemToolsController.cs:208 —— check-then-act
  if (await tasks.HasActiveAsync(itemObjectId, ct)) { skipped++; continue; }
  await tasks.InsertAsync(new TrainingEmotionTaskRecord { ... Status = Pending ... }, ct);
  // TrainingEmotionTasksController.cs:64-74 —— 先读后写，更新无 status 条件
  if (!string.Equals(current.Status, TrainingEmotionTaskStatus.Failed, ...)) return BadRequest(...);
  var updated = await tasks.UpdateAsync(id, update, ct);   // UpdateOneAsync 仅按 Id 匹配
  ```
- **建议**：Retry 改 `FindOneAndUpdate(filter: Id + Status==FAILED, update)`，未命中返回 409/400；重复提交引入「活跃占位」唯一索引（如 `(ItemId, ActiveFlag)` 部分唯一）或插入 DuplicateKey 计 skipped。补并发场景测试。

### P2-4 - 任务锁续期无异常兜底，续期循环遇异常即终止 → 长任务失锁双跑

- **位置**：`servers/training/Emotion/EmotionTaskLock.cs:118-130`（`RenewAsync` 无 try/catch）、`EmotionTaskLock.cs:142-164`（续期循环 `catch (Exception)` 记日志后**退出循环**）
- **问题**：默认 `LockTtlSeconds=30` 而 LLM 调用超时 120s（`TrainingOptions.cs` `AiOptions.RequestTimeoutSeconds`）。`RenewAsync` 的 Redis 抖动异常直接冲垮续期循环且续期永久停止，锁 30s 过期后他节点接管同一任务 → **双跑、重复 LLM 调用、双写 emotion**。写库前的 fencing（`EmotionTaskRunner.cs:73` → :91-98）是 check-then-write，存在 TOCTOU 窗口（两次写之间失锁则两节点都会写）。该实现逐条仿 `TranscriptTaskLock`（同款弱点），但本期任务时长（可达 120s）显著放大双跑窗口。
- **证据**：
  ```csharp
  // EmotionTaskLock.cs:121 —— Redis 异常直接抛出
  var result = (long)await db.ScriptEvaluateAsync(RenewScript, [key], [Token, ttlMs]);
  // EmotionTaskLock.cs:160-163 —— 循环退出，续期停止
  catch (Exception ex) { logger.LogError(ex, "Emotion task lock renewal loop failed for {Key}", key); }
  ```
- **建议**：`RenewAsync` 内捕获 Redis 异常并置 `Lost`（或循环 catch 后继续下轮而非退出）；写库改带条件的更新（filter 含 `Id + Status==RUNNING + Attempts`），把 fencing 落到写操作本身。

### P3 - 建议（不阻塞，可随后续小 PR 跟进）

1. **失败原因兜底透传 `ex.Message`**（`EmotionTaskRunner.cs:171`）：Mongo/网络异常原文写入 `task.Error` 并经接口回传，可能泄露内部细节；建议白名单映射 + 统一兜底文案。
2. **错误码/文案口径不统一**：控制器 `unsupported provider type` vs runner `provider type not supported` 两个码同义；队列错误列直接展示英文错误码（`EmotionTaskQueue.tsx:110`）未走 `apiErrorMessage` 映射（`item text is empty`、`emotion task failed` 未收录 `client.ts:53-88`）。
3. **`SetEmotionAsync` 返回值被忽略**（`EmotionTaskRunner.cs:91-98`）：条目执行期间被删除时未写入仍标 DONE 并带 Emotion 快照；建议判空改标 FAILED。
4. **含 `/` 的录播文件名条目重新识别恒失败且文案误导**（`TrainingMapping.cs:90-99` 解析失败 vs `TrainingItemToolsController.cs:78-81` 映射为 `audio uri is empty`）：导入可产生嵌套名条目但解析口径不支持，属不可恢复失败；建议区分 empty/unparseable 或导入端拒绝嵌套名。
5. **重新识别「识别」按钮无显式加载文案**（`ReRecognizeDialog.tsx:169-172` 仅 disabled）：与 PRD「展示加载状态」及同 PR 内 `加载中...` 做法不一致。
6. **手动添加模型弹窗无 busy 守卫**（`ModelSettingsTab.tsx:176-191,372,414`）：可双击重复提交（第二次 409 误报为错误）、请求途中遮罩关闭；内联「保存」/批量删除同样无防重复提交（`DatasetItemList.tsx:227-249,251-266`）。
7. **任务队列轮询竞态**（`EmotionTaskQueue.tsx:40-59`）：`load` 无 in-flight 守卫/序号比对，慢响应后到覆盖新数据（如「重试」后显示旧 failed 状态）。
8. **任务粒度与 PRD 措辞偏差**：PRD 功能点 3 描述「每个任务的进度/成功失败数量」，实现为逐条目任务 + 全局 counts chips，批次视角仅提交回执一次性提示；建议 PRD 注明口径或补 batchId 分组。
9. **音频字节先全量读入再做大小检查**（`RecordingAudioClient.cs:86-101`）：建议 Content-Length 预检 + 流式限长。
10. **测试缺口**：并发竞态（P2-3 场景）、锁过期双跑、「重复识别以最新结果覆盖」显式断言、`EmotionSubmitDialog` busy 防重复、队列刷新集成断言均缺失。

### P4 - 提醒

- **SSRF 面**：Provider Endpoint 为任意 http(s) URL 且 HttpClient 默认跟随重定向（携带存储的 API key 请求任意地址，`ModelDiscoveryClient`/两个 ChatClient 均未设 `AllowAutoRedirect=false`）。项目为内网自用无鉴权口径，可接受，但建议禁用自动重定向（或重定向剥离认证头）并在提案安全小节显式登记该风险。
- 契约 `limit` 声明 min/max 但实现静默钳制不返 400（`TrainingEmotionTasksController.cs:227`）；import `skipped` 语义宽于契约描述（空白项并入）；discover 把 `ProviderTypeException` 归入 502 `invalid provider response` 不贴合。
- 提案 §3.1 schema 写 `emotionControlWeight 0~1`，实现/契约按 0~10 钳制（提案决策 A）；接口 11/12 的 400 与 skipped 口径较提案正文有增补——建议回填提案。
- 模型绑定失败（`?limit=abc`）由 `[ApiController]` 返回 ProblemDetails 而非 `{"error":...}`（既有口径）；队列 `datasetId` 非法静默降级为不过滤；批量入口无数量上限；OpenAI-like 任意 400 均重试一次（含重复上传音频 base64）；`DisposeAsync` 不等待续期任务退出；扫描器 detached 执行停机靠下次接管（符合设计）。
- ProviderDialog 留空=不改 apiKey，无撤销 key 入口；创建 Provider 时 `apiKey` 不 trim（`ProviderDialog.tsx:68`）；提交回执「跳过 N 条」无原因细分。

## 四、结论

本次 `release..integration` 差距（训练-数据集工具优化）**功能主体完整**：PRD 四大功能点均有落地，14 端点契约/实现/代理三方一致、codegen 0 diff、`apiKey` 只进不出落实到位、TDD 提交链完整（PRD → 提案/Plan → 契约 → 测试红 → 实现绿 → 文档）、web 321/321 全绿。但存在 **4 项 P2 必须修复**（用户数据丢失路径、停用模型绕过、任务状态机并发原子性、锁续期健壮性），按节点规则**判定不通过、打回返工**，要求逐项修复并补对应测试后重新提交验收。P3/P4 列表建议随返工一并处理或排期独立跟进。


