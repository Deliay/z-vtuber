---
description: Transcript 识别优化实现 Plan——基于 PRD v1.0 与技术提案（prd/transcript-model-context）的实施阶段划分与任务分解；契约先行（recording-transcript + bilibili-crawler 双契约同步：settings 增必填 modelId、模型增必填 model、移除 isDefault），common 演进（transcript_models +Model -IsDefault、transcript_auto_enqueue +ModelId、LiveGuardRepository 最新一天快照查询、LiveEventTextIndexRepository 窗口查询、去默认位索引/方法），consumer 改造（enqueue/reconciler 改用设置 ModelId、存量模型字段回填迁移、删除 AsrModel 全局兜底、新增 TranscriptContextBuilder 按优先级汇集主播名/舰长/SC 并 400 字符截断、ASR 工厂签名与 FunAsrProtocol input.context 扩展），transcript-api 校验调整，web 设置页模型必填与待完善引导、模型管理去默认标记
type: Permanent
---

# Transcript 识别优化（显式模型 + 识别上下文）- 实现 Plan

**功能名称**: Transcript 识别优化
**关联 PRD**: `docs/product/draft/20260917-transcript-model-context-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20260917-proposal-transcript-model-context.md`（v1.0）
**Plan 版本**: v1.0
**创建日期**: 2026-09-17
**作者**: Workflow Agent（代起草）
**feat-branch**: `prd/transcript-model-context`（需求总分支，已推送 origin；需求总 PR：<https://github.com/Deliay/z-vtuber/pull/108>）

## 1. 概述

本 Plan 基于 PRD v1.0 与技术提案 v1.0 编写，指导「Transcript 识别优化」的编码实施。改动分六块：① 契约——`contracts/recording-transcript.openapi.yaml` 与 `contracts/bilibili-crawler.openapi.yaml` 双份同步（settings 增必填 `modelId`、模型增必填 `model`、移除 `isDefault`）；② common 演进——`transcript_models` 实体 +Model -IsDefault、`transcript_auto_enqueue` +ModelId、`LiveGuardRepository` 新增按 RoomId 取最新一天快照、`LiveEventTextIndexRepository` 新增窗口查询、`TranscriptModelRepository` 删除默认位方法与索引；③ consumer 改造——enqueue/reconciler 改用设置的 ModelId、存量 `transcript_models` 回填 `Model` 字段迁移、删除 `AsrModel` 全局兜底、新增 `TranscriptContextBuilder`、`AliFunAsrServiceFactory` 签名与 `FunAsrProtocol` `input.context` 扩展；④ transcript-api——settings 更新强制 modelId、models CRUD 强制 model 且移除 isDefault、设置列表返回 modelId/modelName 支撑"待完善"展示；⑤ web——设置页模型必填 + 待完善引导、模型管理表单加 model 字段去默认标记；⑥ 收口——配置清理、全量测试与手工验证。实施全程遵循 `docs/engineering/common-rules.md`（TDD、契约式开发、核心逻辑覆盖率 ≥ 80%）、`backend-rules.md`、`frontend-rules.md` 及各子项目 AGENTS.md。

### 1.0 上游核实结论（代码现状复查）

对需求总分支 `prd/transcript-model-context` 代码核实，提案技术路径**整体可行**，现状细节与须注意的偏差如下，处理方式在对应任务中给出：

- **`GetDefaultAsync` 调用共 3 处，提案漏列 Reconciler**：除 `TranscriptEnqueueService.cs:39`（开播）与 `:79`（结束）外，`TranscriptReconciler.cs:73` 也直接调用 `models.GetDefaultAsync()` 并经 `EnsureLiveTaskAsync`/`EnsureFileTaskAsync`（:97/:116）建任务——Reconciler **并非**"沿用同一 enqueue 入口"。**处理**：三处统一改为从 `TranscriptAutoEnqueueRecord.ModelId` 取模型（任务 2.3），Reconciler 对「启用但 ModelId 为空/模型禁用」的 roomId 跳过并记 Warn。
- **ASR 工厂签名改动波及测试与 Fake**：`IStreamingAsrServiceFactory.Create(long offset, string apiKey)`（`AliFunAsrService.cs:11`）改为携带模型记录与上下文后，`AliFunAsrOffsetTests` 每处直构造 `new AliFunAsrService(endpoint, apiKey, offset, model, idleTimeout, logger)`（:22-26）与 `FakeStreamingAsrServiceFactory.Create`（`tests/Z.Vtuber.Tests/TranscriptConsumerFakes.cs:94-106`）均需同步改写。**处理**：签名定为 `Create(TranscriptModelRecord model, long offset, IReadOnlyList<string> context)`；`AliFunAsrService` 构造函数同步追加 `context` 参数（任务 1.1/2.5）。
- **Runner 处 `recording` 可空**：`TranscriptTaskRunner.cs:51` 查 `RecordingRecord`，`:85`/`:229` 均判空。**处理**：上下文组装以 `recording` 非空为前提；为 null 时跳过组装、空上下文继续识别并记 Warn（任务 2.5），不阻断任务。
- **consumer 未注册上下文所需仓储**：`Program.cs:43-48` 仅注册转写 5 仓储 + `RecordingStatusRepository`；`AccountBasicInfoRepository`/`LiveGuardRepository`/`LiveEventTextIndexRepository` 均未注册。**处理**：按既有一行一个 singleton 模式补注册（任务 2.4）。
- **两个上下文数据源缺查询方法，索引已就绪**：`LiveGuardRepository` 无「按 RoomId 取最新一天快照」方法（现有均按 Mid 或显式 dates），但 `RoomId_1_Date_1` 索引已存在（`MongoDbInitializer.cs:221-229`）；`LiveEventTextIndexRepository` 仅 `InsertAsync`/`SearchAsync`，无按 RoomId+Type+时间窗列举方法，`{RoomId:1, OccurredAt:-1, Id:-1}` 索引已存在（:176-189）。**处理**：各新增一个查询方法，无需新增索引（任务 2.1）。
- **`Type` 过滤值为字符串字面量 `"super_chat"`**：散落 `LiveEventTextExtractor.cs:29,68` 与 `MongoDbInitializer.cs:458,565`，无常量。**处理**：在 `LiveEventTextExtractor` 所在处补常量（如 `LiveEventTextTypes.SuperChat`），新查询方法引用常量，既有字面量顺手替换为常量（任务 2.1，改动收敛在 common 内）。
- **uName 解析先例两处同构**：`AnchorNameExtractor.cs:8-31` 与 `TranscriptMapping.cs:49-69`（`JsonNode.Parse` → `obj["uName"]`，异常/缺失返回 null）；`AccountBasicInfoRecord.Data` 为原始 JSON string。**处理**：`TranscriptContextBuilder` 内复刻同款防御式解析（任务 2.4），不上提 common 共享方法（避免跨服务改动面扩大）。
- **`IsDefault` 部分唯一索引需随字段移除**：`MongoDbInitializer.cs:977-987`（`EnsureTranscriptIndexesAsync` 内 `IsDefault_1`，Unique + PartialFilter `IsDefault==true`）。**处理**：代码中删除该索引创建逻辑与 `SetDefaultAsync`/`GetDefaultAsync`；存量 DB 中已存在的旧索引不强制 drop（实体不再写字段后 partial 索引永不命中，无害），部署说明中给出可选清理命令（任务 2.1/3.1）。
- **存量 `transcript_models` 回填 `Model` 需读旧配置值**：迁移目标值为升级时全局配置 `TranscriptConsumer:AsrModel`（运行期实际生效值，默认 `fun-asr-realtime`）；`MongoDbInitializer` 在 common 不感知该配置。**处理**：迁移放 consumer 启动时执行（`IConfiguration` 直读 `TranscriptConsumer:AsrModel`，缺省回退常量 `fun-asr-realtime`），沿用 `zvtuber_meta` marker 幂等模式（`MongoDbInitializer.cs:953-1004` 先例形态，代码落 consumer）；迁移落地后 `AsrModel` 属性从 `TranscriptConsumerOptions` 删除（任务 2.2）。
- **契约需双份同步**：api 服务面 `contracts/bilibili-crawler.openapi.yaml` 有全套转写代理 schema（`RecordingTranscript*` 前缀：SettingsItem :1286-1300、SettingsUpdateRequest :1308-1313、Model :1407-1429、ModelCreateRequest :1437-1460、ModelUpdateRequest :1461-1473）；代理控制器 `RecordingTranscriptProxyController.cs` 纯转发，改 body 字段无需动代理代码，但两份契约必须同步（任务 0.1/0.2）。前端 codegen 只从 `recording-transcript.openapi.yaml` 生成（`apps/web/package.json:12`）。
- **web 默认模型 UI 共 3 处**：行内"设为默认"按钮（`SettingsTab.tsx:330-337`）、添加表单 checkbox（:378-385）、"无默认模型"警告（:266-268，`hasDefaultModel` :75-78）；设置开关 `handleToggleAuto`（:80-95）现仅传 `{enable}`。**处理**：3 处默认 UI 全删；开关交互改为「开启必须已选模型」（任务 2.7）。
- **受「移除 IsDefault」影响的既有测试**：`TranscriptApiTests.cs:369-418`（默认切换原子性、Update DTO 位置参数）、`TranscriptTaskRepositoryTests.cs:256-322`（SetDefault/GetDefault 专项）、`RecordingFinishedSubscriberTests.cs:93`（断言任务归属默认模型）、各测试种子普遍写 `IsDefault`。**处理**：默认位专项测试删除，其余改写为 ModelId 绑定语义（任务 1.1/1.2/1.5）。
- **`GET /api/transcripts/settings` 现状**：仅列表（`TranscriptSettingsController.cs:12-36`），`TranscriptSettingsItemDto`（`Contracts/Dtos.cs:71-75`）无 modelId 字段；更新端点在 `RecordingsArchiveController.cs:66-80`，body 仅 `{enable}`（:77-78），校验全手工。**处理**：DTO/映射/校验均手工扩展，保持既有风格（任务 2.6）。

### 1.1 实施范围

- **做**：
  - 契约：双份 openapi 同步——`TranscriptSettingsUpdateRequest` 增 `modelId`（`enable=true` 时必填）、`TranscriptSettingsItem` 增 `modelId`/`modelName`（nullable）、`TranscriptModel` 增 `model` 移除 `isDefault`、Create/Update 请求同步调整。
  - common：`TranscriptModelRecord` +`Model` -`IsDefault`；`TranscriptAutoEnqueueRecord` +`ModelId`；`TranscriptModelRepository` 删 `GetDefaultAsync`/`SetDefaultAsync` 及 Update 的 isDefault 参数；`TranscriptAutoEnqueueRepository.UpsertAsync` 扩 modelId；`LiveGuardRepository` +按 RoomId 取最新一天快照；`LiveEventTextIndexRepository` +按 RoomId+super_chat+时间窗列举；`MongoDbInitializer` 删 IsDefault 部分唯一索引；`super_chat` 类型常量。
  - consumer：enqueue 三入口（started/finished/reconciler）改用设置 ModelId；`TranscriptContextBuilder` 新增；ASR 工厂/协议/Runner 上下文透传；存量模型 `Model` 字段回填迁移；删 `AsrModel` 配置。
  - transcript-api：settings 更新 modelId 校验（400）、models CRUD model 必填、响应/请求去 isDefault、设置列表补 modelId/modelName。
  - web：设置页模型选择必填 + 待完善提示与引导、模型管理 +model 字段 -默认标记、client/类型同步。
  - 测试：全部新增/改写测试，核心逻辑覆盖率 ≥ 80%，无回归。
- **不做**：预编译热词 `vocabulary_id` 生命周期管理；live 运行中 `continue-task` 增量更新上下文；recording/crawler/notifier/cookie 零改动；存量 DB 旧 `IsDefault` 索引强制 drop（可选清理见 §3 Phase 3）；`transcript_tasks`/`transcript_segments` 结构变更（上下文不入库）。

### 1.2 现状基线

- **仓储新方法范本**：`LiveGuardRepository.GetOnboardGuardEntriesAsync`（聚合管道 + `RoomId_1_Date_1` 索引）；`LiveEventTextIndexRepository.SearchAsync`（Hint 命中索引）。
- **迁移范本**：`MongoDbInitializer` 内 marker + 幂等 + 回滚容错模式（:953-1004 `MigrateCookieInvalidEventTypeSplitAsync`）；本需求迁移落 consumer 启动路径（需读 consumer 配置）。
- **uName 解析范本**：`servers/recording/Live/AnchorNameExtractor.cs:8-31`。
- **校验范本**：transcript-api 全手工校验（`TranscriptModelsController.cs:22-32`），不用 DataAnnotations。
- **协议测试范本**：`AliFunAsrOffsetTests` 本地假 websocket server 契约测试。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash）与 Conventional Commits：

- 本需求开发全程只使用需求总分支 `prd/transcript-model-context`：契约、实现、测试、文档全部直接在该分支提交，不切开发子分支（沿用上期需求既定做法）。
- 需求总分支已推送 origin，需求总 PR 已存在（#108），提交照常推送更新分支。
- 提交信息使用 Conventional Commits：契约 `feat:`、测试先行 `test:`、实现 `feat:`、重构清理 `refactor:`、文档 `docs:`；测试与实现分开提交。
- 并行 subagent 按子项目划分（common+consumer / transcript-api / apps-web），共享文件（契约、common 实体与仓储、测试 fixture/Fakes）归入主串行线先行，避免并行冲突（见第 4 节）。

## 3. 实施阶段

按「契约先行 → TDD 红-绿 → 配置收口 → 收口验证」推进。Phase 0 串行先行；Phase 1 测试先行（1.1 测试基线调整先行，其余按子项目并行）；Phase 2 common（2.1）串行先行，随后 consumer / transcript-api / web 多线并行；Phase 3 与 Phase 2 可部分并行；Phase 4 串行收口。

### Phase 0: 契约先行

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | `contracts/recording-transcript.openapi.yaml` 变更：① `TranscriptSettingsUpdateRequest`（:462-467）增 `modelId`（string；描述注明 `enable=true` 时必填且须指向已启用模型，否则 400；`enable=false` 时可省略）；② `TranscriptSettingsItem`（:440-454）增 `modelId`、`modelName`（均 nullable；未绑定为空，前端据此展示"待完善"）；③ `TranscriptModel`（:561-583）增 `model`（string，实际发给服务商的模型 ID）、**移除 `isDefault`**；④ `TranscriptModelCreateRequest`（:591-614）`required` 改为 `[name, provider, type, apiKey, model]`、移除 `isDefault`；⑤ `TranscriptModelUpdateRequest`（:615-626）增可选 `model`、移除 `isDefault`；⑥ 创建成功响应描述中"isDefault 原子清除"字样删除；⑦ settings 更新端点（:177-201）400 响应描述补 modelId 校验场景 | `contracts/recording-transcript.openapi.yaml` | `npx openapi-typescript` 可成功生成类型；与提案 §3.1 逐字段一致 |
| 0.2 | `contracts/bilibili-crawler.openapi.yaml` 同步上述 ①-⑦ 对应 `RecordingTranscript*` schema（:1286-1313、:1407-1473）与端点描述（:529-654） | `contracts/bilibili-crawler.openapi.yaml` | codegen 通过；字段与 0.1 一致 |

### Phase 1: 测试先行（红）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | 测试基线调整（串行先行）：① `TranscriptConsumerFakes.cs:94-106` `FakeStreamingAsrServiceFactory.Create` 签名改为 `(TranscriptModelRecord model, long offset, IReadOnlyList<string> context)`，记录传入 context 供断言；② 全部测试种子去 `IsDefault`、补 `Model`（如 `fun-asr-realtime`）与 auto-enqueue `ModelId`；③ 删除 `TranscriptTaskRepositoryTests.cs:256-322` 默认位专项（SetDefault/GetDefault/唯一默认位）；④ `RecordingFinishedSubscriberTests.cs:93` 改为断言任务 ModelId == 设置绑定的模型；⑤ `AliFunAsrOffsetTests` 直构造处（:22-26）追加 context 参数 | `tests/Z.Vtuber.Tests/TranscriptConsumerFakes.cs`、`TranscriptTaskRepositoryTests.cs`、`RecordingFinishedSubscriberTests.cs`、`AliFunAsrOffsetTests.cs` 及各种子 | 编译通过（红绿由后续任务决定）；既有无关测试无回归 |
| 1.2 | enqueue/reconciler 测试改写：① `TranscriptEnqueueTests`——auto-enqueue `ModelId` 为空不建任务（started/finished 均不建）；绑定模型被禁用/不存在不建任务且记 Warn；正常绑定建 live/file 任务且 `task.ModelId == setting.ModelId`；手动 enqueue 路径不变回归；② `TranscriptReconcilerTests`——ModelId 为空跳过该 roomId 不补建；正常绑定补建 | `tests/Z.Vtuber.Tests/TranscriptEnqueueTests.cs`（改）、`TranscriptReconcilerTests.cs`（改） | 红 |
| 1.3 | `TranscriptContextBuilderTests`（新，Mongo 集成）：① 四类信息齐备——输出含主播名 + 最新一天舰长名单 + 窗口内 SC 用户名/粉丝牌（交替一行一个）+ SC 内容；② 优先级顺序——主播名在最前、SC 内容在最后；③ HashSet 去重——SC 用户名与舰长同名只出现一次；④ 超 400 字符按优先级整段截断（低优先级整行舍弃，不出现半截名字）；⑤ 单类缺失降级（无舰长快照/无 SC/无主播资料）不影响其余；⑥ 舰长取该 RoomId `Date` 最大快照而非全部；⑦ SC 窗口 `[StartedAt, EndedAt ?? now]` 只含 `super_chat`，弹幕不混入 | `tests/Z.Vtuber.Tests/TranscriptContextBuilderTests.cs`（新） | 红 |
| 1.4 | ASR 协议测试改写：`AliFunAsrOffsetTests`——① context 非空时 run-task payload `input.context` 为 1 条 user 消息、`content[0].text` 为按行拼接文本；② context 为空时 `input` 维持空对象（行为不变回归）；③ 既有 run-task/音频帧/finish-task 顺序与 offset/sentence_end 语义回归 | `tests/Z.Vtuber.Tests/AliFunAsrOffsetTests.cs`（改） | 红 |
| 1.5 | transcript-api 测试改写：① settings 更新——`enable=true` 缺 modelId → 400；modelId 指向不存在/被禁用模型 → 400；`enable=false` 仅传 enable → 200；正常绑定 → 200 且列表返回 modelId/modelName；未绑定设置列表项 modelId/modelName 为 null；② models——Create 缺 `model` → 400；Create/Update 响应含 `model` 不含 `isDefault`/apiKey；删除原"设默认原子切换"断言（:369-396）与 Update 位置参数中的 isDefault（:398-418） | `tests/Z.Vtuber.Tests/TranscriptApiTests.cs`（改） | 红 |
| 1.6 | 前端测试改写：① `client.test.ts`——`setRoomAutoEnqueue` 携带 modelId；模型 create/update 携带 `model`；② `SettingsTab.test.tsx`——模型表单含"识别模型"必填项；开启自动转写未选模型被阻止并提示；未绑定模型的设置项展示"待完善"；"设为默认"按钮/checkbox/无默认警告均不存在 | `apps/web/src/api/client.test.ts`、`apps/web/src/pages/transcripts/SettingsTab.test.tsx`（改） | `npm run test` 红 |

### Phase 2: 实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | common 演进（串行先行）：① `TranscriptModelRecord` +`Model`（string）-`IsDefault`；`TranscriptAutoEnqueueRecord` +`ModelId`（string?，空=待完善）；② `TranscriptModelRepository` 删 `GetDefaultAsync`/`SetDefaultAsync`，`UpdateAsync` 参数 isDefault 换为 model；`TranscriptAutoEnqueueRepository.UpsertAsync` 扩 `modelId` 参数；③ `LiveGuardRepository` +`GetLatestSnapshotByRoomIdAsync(long roomId)`（按 `Date` 字符串倒序取一，`yyyy-MM-dd` 定长格式字典序即时间序，命中既有 `RoomId_1_Date_1` 索引）；④ `LiveEventTextIndexRepository` +按 RoomId + `Type=super_chat` + `OccurredAt ∈ [from, to]` 升序列举（命中既有 `{RoomId:1, OccurredAt:-1}` 索引）；⑤ `super_chat` 等类型字面量上提常量（common 内，既有 :29/:68 与 initializer :458/:565 同步替换）；⑥ `MongoDbInitializer.EnsureTranscriptIndexesAsync` 删 `IsDefault_1` 部分唯一索引创建逻辑（:977-987） | `servers/common/Persistence/Entities/TranscriptModelRecord.cs`、`TranscriptAutoEnqueueRecord.cs`、`servers/common/Persistence/TranscriptModelRepository.cs`、`TranscriptAutoEnqueueRepository.cs`、`LiveGuardRepository.cs`、`LiveEventTextIndexRepository.cs`、`MongoDbInitializer.cs`、`servers/common/Live/LiveEventTextExtractor.cs` | 1.2、1.3 涉及仓储部分转绿；既有测试无回归 |
| 2.2 | consumer 配置与存量迁移：① `TranscriptConsumerOptions` 删 `AsrModel` 属性（:41-43）；② 新增启动迁移（consumer 内，仿 marker 幂等模式）：为 `Model` 缺失/为空的 `transcript_models` 记录补 `Model = configuration["TranscriptConsumer:AsrModel"] ?? "fun-asr-realtime"`，`zvtuber_meta` marker 防重入，统计记日志；③ `Program.cs` 启动时于 `EnsureIndexesAsync` 后执行迁移 | `servers/recording-transcript-consumer/Configuration/TranscriptConsumerOptions.cs`、`servers/recording-transcript-consumer/Services/`（新迁移）、`Program.cs` | 迁移单测（重复执行幂等、已填不覆盖）绿；缺配置不再要求 AsrModel |
| 2.3 | consumer·enqueue 改用显式模型：① `TranscriptEnqueueService.EnqueueForStartedAsync`（:39）与 `EnqueueForFinishedAsync`（:79）删 `GetDefaultAsync`，改为 `setting.ModelId` 为空 → Warn + null；`models.GetByIdAsync` 不存在/未启用 → Warn + null；② `TranscriptReconciler.cs:73` 同样改为按各 roomId 设置的 ModelId 取模型（逐设置处理，跳过未绑定并 Warn），`:97`/`:116` 调用签名不变 | `servers/recording-transcript-consumer/Enqueue/TranscriptEnqueueService.cs`、`Reconcile/TranscriptReconciler.cs` | 1.2 转绿 |
| 2.4 | consumer·TranscriptContextBuilder 新增：`Context/TranscriptContextBuilder.cs`——输入 `RecordingRecord`，输出 `IReadOnlyList<string>`（HashSet 语义去重、按优先级有序）：① 主播名（`AccountBasicInfoRepository.GetLatestByMidAsync(recording.Mid)`，复刻 `AnchorNameExtractor` 防御式解析 `Data.uName`）；② 最新一天舰长快照 `Guards[].UserName`（2.1③）；③ 窗口内 SC（2.1④，`[StartedAt, EndedAt ?? UtcNow]`）逐条 `SenderName`、`SenderMedal` 交替入列；④ 窗口内 SC `Text` 逐条入列；⑤ 逐行拼接 `\n` 预估累计长度，将超 400 字符（常量 `MaxContextChars`）即停止追加当前及后续优先级的行；任一类查询失败/为空跳过并 Warn。`Program.cs` 注册 `AccountBasicInfoRepository`/`LiveGuardRepository`/`LiveEventTextIndexRepository`（照 :43-48 singleton 模式）与 `TranscriptContextBuilder` | `servers/recording-transcript-consumer/Context/TranscriptContextBuilder.cs`（新）、`Program.cs` | 1.3 转绿 |
| 2.5 | consumer·ASR 链路上下文透传：① `IStreamingAsrServiceFactory.Create` 改签名 `(TranscriptModelRecord model, long offset, IReadOnlyList<string> context)`；`AliFunAsrServiceFactory` 内部用 `model.Model`/`model.ApiKey`，options 仅保留 Endpoint/IdleTimeout；`AliFunAsrService` 构造函数追加 `context`；② `FunAsrProtocol.BuildRunTask` 增 context 参数：非空时 `input = { context: [ { role: "user", content: [ { type: "input_text", text: string.Join("\n", context) } ] } ] }`，为空维持 `input = { }`；③ `TranscriptTaskRunner`（:95 调用点）：`recording` 非空 → `contextBuilder.BuildAsync(recording)`，`recording` 为空 → 空列表 + Warn；随后 `asrFactory.Create(model, offset, context)` | `servers/recording-transcript-consumer/Asr/AliFunAsrService.cs`、`Asr/FunAsrProtocol.cs`、`Tasks/TranscriptTaskRunner.cs` | 1.4 转绿；1.1 基线全绿 |
| 2.6 | transcript-api 调整：① `Contracts/Dtos.cs`——`TranscriptSettingsUpdateRequest` +`ModelId`；`TranscriptSettingsItemDto` +`ModelId`/`ModelName`（nullable）；`TranscriptModelDto` +`Model` -`IsDefault`；Create/Update 请求同步；`TranscriptMapping.ToModelDto` 同步；② `RecordingsArchiveController.UpdateSettings`（:66-80）——`enable=true` 时 modelId 必填且 `ObjectId.TryParse` + 查模型存在且启用，否则 400（错误信息明确）；`enable=false` 仅更新开关；`UpsertAsync` 透传 modelId；③ `TranscriptSettingsController.ListSettings` 关联 `TranscriptModelRepository` 补 modelName；④ `TranscriptModelsController`——Create 校验 `model` 非空白（:22-32 同风格）、落库 `Model`；删 `:48-52` SetDefault 调用与 Update `:86-93` isDefault 分支；响应不含 isDefault/apiKey | `servers/recording-transcript-api/Contracts/Dtos.cs`、`Contracts/TranscriptMapping.cs`、`Controllers/RecordingsArchiveController.cs`、`Controllers/TranscriptSettingsController.cs`、`Controllers/TranscriptModelsController.cs` | 1.5 转绿 |
| 2.7 | web 改造（仅依赖 Phase 0 契约，与后端并行）：① codegen 重新生成 `schema-recording-transcript.d.ts`（勿手改）；② `client.ts`——`setRoomAutoEnqueue(roomId, enable, modelId?)`；模型 create/update 携带 `model`；③ `SettingsTab.tsx`——识别设置：每房间开启自动转写必须先选模型（下拉列出启用中模型；未选禁用开关/保存并提示）；列表项无 modelId 展示"待完善"徽标并引导选择；④ 模型设置：表单 +「识别模型」必填输入（如 `fun-asr-realtime`）；列表 +model 列；删"设为默认"按钮（:330-337）、checkbox（:378-385）、无默认警告（:266-268）与 `hasDefaultModel`（:75-78） | `apps/web/src/api/client.ts`、`apps/web/src/api/schema-recording-transcript.d.ts`（生成）、`apps/web/src/pages/transcripts/SettingsTab.tsx` | 1.6 转绿；`npm run lint`/`npm run build` 通过 |

### Phase 3: 配置与部署收口

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 3.1 | 配置与部署说明：① 全仓库 grep 清理 `TranscriptConsumer:AsrModel` 配置残留（appsettings/编排环境变量，迁移代码直读一处除外）；② PR #108 描述补充升级要点——升级后存量自动转写配置进入"待完善"（ModelId 为空，自动转写暂停），需在设置页补选模型；存量模型经启动迁移自动回填 `Model`（取旧 `TranscriptConsumer:AsrModel` 配置值，缺省 `fun-asr-realtime`），回填后该配置项废弃；可选清理存量 DB 的 `transcript_models.IsDefault_1` 旧索引（`db.transcript_models.dropIndex("IsDefault_1")`，不清理无害） | `infra/apphost/AppHost.cs`（如有 AsrModel 环境变量）、PR 描述 | grep 无残留；描述完整 |
| 3.2 | 编排核对：Aspire AppHost 无新增服务/资源；确认 consumer 编排不依赖被删配置项；本地 Aspire 一键拉起全量环境 | `infra/apphost/AppHost.cs` | 拉起成功 |

### Phase 4: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 4.1 | `dotnet test tests/Z.Vtuber.Tests` 全绿（含既有回归）；核心逻辑（ContextBuilder/enqueue/协议）覆盖率 ≥ 80%；`npm run test`/`lint`/`build` 全绿；grep 确认：`IsDefault`/`GetDefaultAsync`/`SetDefaultAsync`/`AsrModel` 在 servers 与 apps/web 源码零残留（迁移直读配置一处除外）、双契约字段一致、apiKey 不出现在任何响应 DTO | 全部通过 |
| 4.2 | 编排手工验证（z-aspire-orchestrator）：本地 Aspire 拉起全量 → 模型管理新建模型（必填识别模型）→ 设置页为房间开启自动转写（不选模型被阻止；选模型成功）→ 开播出现 live 任务且 FunASR run-task payload 含 `input.context`（主播名/舰长/SC，日志或假服务确认）→ 下播任务 DONE；未绑定模型的存量设置不建任务且页面展示"待完善"，补选后恢复 | 手工验证记录 |
| 4.3 | AGENTS.md 同步：`servers/recording-transcript-consumer/AGENTS.md`（Context/ 目录、删 AsrModel 配置说明、迁移说明）、`servers/recording-transcript-api/AGENTS.md`（settings modelId 必填、模型 model 字段、无 isDefault）、`servers/common/AGENTS.md`（实体字段变更、新仓储方法、索引变更）、`apps/web/AGENTS.md`（设置页模型必填/待完善）、`docs/engineering/backend-rules.md` 如涉及结构图描述同步 | 文档与实现一致 |

## 4. 并行策略

- Phase 0（契约 0.1/0.2）串行先行——transcript-api、前端 codegen 均依赖契约。
- Phase 1：1.1（测试基线：Fake 签名、种子、删默认位专项）串行先行——所有后端测试任务依赖它编译通过；随后 1.2-1.5（后端）与 1.6（前端）互不依赖，可拆 subagent 并行。
- Phase 2：2.1（common）串行先行——consumer 与 transcript-api 均依赖实体/仓储变更；随后 consumer 线（2.2 → 2.3 → 2.4 → 2.5 大体串行）、transcript-api 线（2.6）、web 线（2.7）分属不同子项目，按 z-coding 规范拆 subagent 并行，各 subagent 负责「实现 → 提交 → 测试修复 → 提交」闭环。
- Phase 3（3.1/3.2）与 Phase 2 互不依赖可并行。
- Phase 4 串行收口。全部提交在需求总分支 `prd/transcript-model-context`。

## 5. 验收对照

| PRD 功能点 | 实现任务 | 验证方式 |
|-----------|---------|---------|
| 功能点 1：强制显式选择识别模型 | 0.1①③④、2.1①②、2.2①、2.3、2.6②④、2.7③④ | 1.2、1.5、1.6 + 手工验证（4.2）+ 4.1 grep |
| 功能点 2：存量配置迁移（待完善） | 0.1②、2.1①（ModelId 可空）、2.2②（模型字段回填）、2.6③、2.7③ | 1.5（列表 null 形态）、迁移单测（2.2）+ 手工验证（4.2） |
| 功能点 3：识别上下文（周边信息热词） | 2.1③④⑤、2.4、2.5 | 1.3、1.4 + 手工验证 payload（4.2） |
| 非功能：覆盖率 ≥ 80%、全量测试绿、无默认模型残留 | 4.1 | `dotnet test` / `npm run test` / grep |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| Reconciler 绕过统一入口 | Reconciler 直接调 `GetDefaultAsync`（提案漏列），漏改会留下第二处兜底 | 1.2② 测试锁定「未绑定不补建」；4.1 grep `GetDefaultAsync` 零残留兜底 |
| 存量模型 `Model` 回填值错误 | 迁移取旧配置值，若运营曾改过 `AsrModel` 配置需以配置为准 | 迁移直读 `IConfiguration["TranscriptConsumer:AsrModel"]`，缺省回退 `fun-asr-realtime`（与运行期既有生效值一致）；迁移日志输出回填明细便于核对（2.2） |
| 自动转写升级后静默暂停 | 存量设置 ModelId 为空 → 自动转写不生效，用户无感知即漏转写 | PRD 功能点 2 既定行为；设置页"待完善"徽标引导（2.7③）；enqueue/reconciler Warn 日志可检索（2.3） |
| `input.context` 服务商约束不确定 | 字段确切形态/上限以联调为准，可能不生效 | 1.4 假 websocket server 锁定客户端 payload 结构；4.2 手工验证真实 payload；回退方案为预编译热词 `vocabulary_id`（提案 §6.1 决策 2，本 Plan 不实施） |
| live 任务上下文快照偏早 | 开播即启动时当场 SC 尚少，上下文只含主播名/舰长 | PRD §5.2 约束已明确接受；`continue-task` 增量更新留作后续增强 |
| Runner recording 为空 | 极端情况（归档缺失）下无 Mid/时间窗可组装上下文 | 空上下文继续识别 + Warn（2.5③），不阻断任务 |
| 双契约漂移 | api 代理面契约漏改导致前后端类型不一致 | 0.1/0.2 同 Phase 串行完成；4.1 grep 双契约字段一致性 |
| 旧 `IsDefault_1` 索引残留 | 存量 DB 索引不随代码删除 | 实体不写字段后 partial 索引永不命中，无害；部署说明给可选 drop 命令（3.1） |
| 依赖 | 阿里云百炼 fun-asr-realtime `input.context`（≤5 轮、每轮 ≤400 字符）；`bilibili_account_basic_info`/`bilibili_live_guards`/`bilibili_live_event_text_index` 既有采集链路；Testcontainers、openapi-typescript | 版本与现状已核实（§1.0） |

## 7. 相关文档

- PRD：`docs/product/draft/20260917-transcript-model-context-prd.md`（v1.0）
- 技术提案：`docs/engineering/proposals/20260917-proposal-transcript-model-context.md`（v1.0）
- 契约：`contracts/recording-transcript.openapi.yaml`、`contracts/bilibili-crawler.openapi.yaml`
- 前序 Plan：[[20260916-plan-recording-transcript]]（转写功能初版）
- FunASR realtime websocket API：<https://platform.qianwenai.com/docs/api-reference/speech-recognition/fun-asr-realtime/websocket-api.md>
- 阿里云百炼：提升语音识别准确率：<https://www.alibabacloud.com/help/zh/model-studio/improve-asr-accuracy>
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`docs/engineering/frontend-rules.md`、`servers/common/AGENTS.md`、`servers/recording-transcript-consumer/AGENTS.md`、`servers/recording-transcript-api/AGENTS.md`、`apps/web/AGENTS.md`
