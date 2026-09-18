---
description: 转写三方识别报错可观测性优化实现 Plan——基于 PRD v1.0 与技术提案（prd/transcript-asr-error-observability）的实施阶段划分与任务分解；纯可观测性增强，改动收敛于 recording-transcript-consumer 的 AliFunAsrService/FunAsrProtocol 与 tests：FunAsrProtocol 新增 TryParseTaskFailed 结构化解析，AliFunAsrService 引入 AsrPhase 阶段跟踪 + RunStats 运行统计 + 统一 LogFailure，task-failed 记录原始报文（≤2KB 截断），连接失败/启动超时补 Warn 日志，异常消息带阶段前缀使任务 Error 落库可读；测试基于既有 FakeFunAsrServer 扩展 + ListLogger 捕获断言
type: Permanent
---

# 转写三方识别报错可观测性优化 - 实现 Plan

**功能名称**: 转写三方识别报错可观测性优化
**关联 PRD**: `docs/product/draft/20260918-transcript-asr-error-observability-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20260918-proposal-transcript-asr-error-observability.md`（v1.0）
**Plan 版本**: v1.0
**创建日期**: 2026-09-18
**作者**: Workflow Agent（代起草）
**feat-branch**: `prd/transcript-asr-error-observability`（需求总分支，已推送 origin；需求总 PR：<https://github.com/Deliay/z-vtuber/pull/126>）

## 1. 概述

本 Plan 基于 PRD v1.0 与技术提案 v1.0 编写，指导「转写三方识别报错可观测性优化」的编码实施。改动分三块：① `FunAsrProtocol`——新增 `TryParseTaskFailed` 结构化解析（code/message 分离返回），`ParseErrorMessage` 改由其复用实现保持行为兼容；② `AliFunAsrService`——引入私有 `AsrPhase` 阶段枚举（Connecting/Starting/Streaming/Finishing）与共享 `RunStats`（计时/已发字节/已产句数），统一 `LogFailure` 失败日志辅助方法，task-failed 分支记录三方原始报文（≤2KB 截断带标记），连接失败与启动超时补齐 Warn 日志，ASR 侧抛出的异常消息统一带阶段前缀（落库 `Error` 无需改 Runner 即获阶段信息）；③ 测试——基于既有 `FakeFunAsrServer` 扩展「连接后不回 task-started」能力，新增 `AliFunAsrErrorLogTests` 对四类失败做日志断言。实施全程遵循 `docs/engineering/common-rules.md`（TDD、核心逻辑覆盖率 ≥ 80%）、`backend-rules.md` 及 `servers/recording-transcript-consumer/AGENTS.md`。

### 1.0 上游核实结论（代码现状复查）

对需求总分支 `prd/transcript-asr-error-observability` 代码逐行核实，提案技术路径**可行**，引用行号全部属实；须注意的偏差点如下，处理方式在对应任务中给出：

- **提案行号引用核实无误**：task-failed 分支 `AliFunAsrService.cs:224-228`、`ConnectAsync` :77、`started.WaitAsync(StartTimeout, ct)` :275、`ParseErrorMessage` `FunAsrProtocol.cs:118-136`、Runner 落库 `Error=ex.Message`（`TranscriptTaskRunner.cs:164` 经 `WriteTerminalAsync`）。现状描述（原始报文丢弃、连接/启动失败无日志、无运行统计、阶段不可区分）均与代码一致。
- **`StartTimeout` 为 `private static readonly` 常量 15s（`AliFunAsrService.cs:39`）**：启动超时测试若走真实常量需等待 15s，不可接受；且既有假服务端只有「收到 finish-task 后沉默」（`SilentOnFinish`，`AliFunAsrOffsetTests.cs:339-342`），没有「连接后不回 task-started」的能力。**处理**：`AliFunAsrService` 构造函数追加可选 `startTimeout` 参数（缺省 15s，工厂不传保持现状）；`FakeFunAsrConnection` 新增 `NeverStart` 开关（收到 run-task 不回 task-started 亦不关闭连接）（任务 1.1/2.2）。
- **测试基础设施已齐备，无需新建**：`FakeFunAsrServer`/`FakeFunAsrConnection`（`AliFunAsrOffsetTests.cs:190-420`）已有 `FailOnRunTaskJson`（run-task 即回 task-failed）、`AbortAfterFirstBinary`（首帧后 RST 断开）、`SilentOnFinish`（停滞超时）三种失败模拟；捕获式 `ListLogger<T>` 已存在（`RecordingTranscriptFakes.cs:131-148`，记录 Level + 格式化 Message + Exception），断言走格式化后的消息文本即可，无需扩展结构化字段捕获。连接失败场景不需假服务端（指向未监听端口即可）。
- **`ParseErrorMessage` 的「固定串」仅在非 JSON/无 header 时触发**：header 存在但缺 `error_code`/`error_message` 时返回 `FunASR task failed (): `（空值插值）而非固定串（`FunAsrProtocol.cs:123-128`）。**处理**：`TryParseTaskFailed` 三分支（完整/缺字段/非 JSON）由单测锁定；`ParseErrorMessage` 保留签名与既有输出格式，内部委托 `TryParseTaskFailed` 后按原格式拼装，既有测试 `ServerTaskFailed_SurfacesErrorCodeAndMessage`（`AliFunAsrOffsetTests.cs:143-158`）作为行为兼容回归不得改动断言。
- **taskId 即报障关联标识，自 run-task 起可用**：`taskId = Guid.NewGuid().ToString("N")`（`AliFunAsrService.cs:80`）客户端生成并随 run-task header 下发，连接阶段尚无 taskId——连接失败日志以 endpoint/model/offset/elapsedMs 为上下文，不伪造 taskId（PRD §3.5「连接阶段即失败：不带误导性的运行统计」）。
- **异常经 `GetBaseException()` 传播，阶段前缀不会丢失**：send/receive 任务异常经 `channel.Writer.TryComplete(t.Exception.GetBaseException())`（`AliFunAsrService.cs:98-108`）进主枚举，最终由 Runner `catch (Exception ex)` 落库 `ex.Message`（`TranscriptTaskRunner.cs:159-164`）。**处理**：阶段前缀直接加在 ASR 侧构造的异常消息上（`[Connecting]/[Starting]/[Streaming]/[Finishing]`），Runner 零改动。
- **receive 循环兜底 catch 已有日志但缺阶段与统计**：`AliFunAsrService.cs:254-257`「FunASR websocket receive loop failed」补 phase 与运行统计字段；消息体同步带阶段前缀。
- **连接异常断开两处文案需区分**：`Close` 帧路径（:172-182，带 closeStatus/description）与「循环自然退出而未 task-finished」路径（:235-238）是两条日志，均补 phase/stats 与异常消息阶段前缀，互不合并。

### 1.1 实施范围

- **做**：
  - `servers/recording-transcript-consumer/Asr/FunAsrProtocol.cs`：+`TryParseTaskFailed(json, out errorCode, out errorMessage)`；`ParseErrorMessage` 内部复用、行为兼容。
  - `servers/recording-transcript-consumer/Asr/AliFunAsrService.cs`：`AsrPhase` 枚举 + `RunStats` 共享统计 + `LogFailure` 统一失败日志；task-failed 记原始报文（≤2KB 截断 + `...(truncated)` 标记）；ConnectAsync try/catch 补 Warn 后 rethrow；启动超时补 Warn 且新异常消息含 taskId/超时值；idle timeout/连接断开/兜底 catch 三处既有 Warn 补 phase 与统计；ASR 侧异常消息统一 `[Phase]` 前缀；构造函数 +可选 `startTimeout`。
  - `tests/Z.Vtuber.Tests`：`FakeFunAsrConnection` +`NeverStart` 能力；新增 `AliFunAsrErrorLogTests.cs`；`FunAsrProtocol` 解析单测。
- **不做**：不改变识别重试、状态流转、并发门控等任何既有行为；不改 `IStreamingAsrService`/`IStreamingAsrServiceFactory` 签名；无契约/OpenAPI 变更、无数据模型变更、无配置项变更；不涉及其他服务的三方调用；不引入 OpenTelemetry 等新基础设施。

### 1.2 现状基线

- **协议测试范本**：`AliFunAsrOffsetTests`（本地假 websocket server 契约测试，`FakeFunAsrServer` 基于 `TcpListener` + `WebSocket.CreateFromStream`）。
- **日志断言范本**：`ServerTaskFailed_SurfacesErrorCodeAndMessage`（`AliFunAsrOffsetTests.cs:143-158`，`ListLogger` + 消息包含断言）与 `KeyLifecycleNodes_AreLogged`（:173-188）。
- **捕获 logger**：`ListLogger<T>`（`RecordingTranscriptFakes.cs:131-148`）。
- **异常落库链路**：ASR 异常 → channel → 主枚举抛出 → `TranscriptTaskRunner` catch → `WriteTerminalAsync(Failed, ex.Message)`。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash）与 Conventional Commits：

- 本需求开发全程只使用需求总分支 `prd/transcript-asr-error-observability`：实现、测试、文档全部直接在该分支提交，不切开发子分支（沿用上期需求既定做法）。
- 需求总分支已推送 origin，需求总 PR 已存在（#126），提交照常推送更新分支。
- 提交信息使用 Conventional Commits：测试先行 `test:`、实现 `feat:`、文档 `docs:`；测试与实现分开提交。
- 改动仅涉及 `servers/recording-transcript-consumer` 与 `tests/Z.Vtuber.Tests` 单一子项目线，不拆并行 subagent（见第 4 节）。

## 3. 实施阶段

按「TDD 测试先行（红）→ 实现（绿）→ 收口验证」推进。Phase 1、Phase 2 各自内部按给定顺序串行；Phase 3 串行收口。

### Phase 1: 测试先行（红）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | 测试基线扩展（串行先行）：① `FakeFunAsrConnection` 新增 `NeverStart` 开关——收到 run-task 后既不回 task-started 也不关闭连接（与 `SilentOnFinish` 同模式，挂在 `action == "run-task"` 分支）；② `AliFunAsrOffsetTests.CreateService` 辅助方法追加可选 `startTimeout` 参数透传构造函数（既有调用点不传，行为不变）；③ 编译通过，`dotnet test --filter AliFunAsrOffsetTests` 既有用例全绿（回归基线） | `tests/Z.Vtuber.Tests/AliFunAsrOffsetTests.cs` | 编译通过；既有 ASR 测试无回归 |
| 1.2 | `FunAsrProtocol.TryParseTaskFailed` 单测（新）：① 完整报文——`error_code`/`error_message` 正确解析返回 true；② header 缺字段——code/message 为 null 仍返回 true（header 存在即视为可解析）；③ 非 JSON/无 header——返回 false 且 out 参数为 null；④ `ParseErrorMessage` 行为兼容回归——完整报文输出 `FunASR task failed (code): message`、非 JSON 输出固定串 `FunASR task failed` | `tests/Z.Vtuber.Tests/AliFunAsrErrorLogTests.cs`（新） | 红（方法不存在，编译失败即红） |
| 1.3 | task-failed 日志断言（新）：假 server `FailOnRunTaskJson` 返回含 `error_code=InvalidParameter`、`error_message` 及额外未知字段的报文 → 断言 Warn 日志含：错误码、错误描述、原始 JSON 原文、`phase=Streaming`（run-task 已发即视为进入识别链路，按实现定义断言为提案约定的阶段值）、taskId、运行统计字段（elapsedMs/bytesSent/segmentsYielded）；异常消息含 `[Streaming]` 前缀与错误码（落库 Error 可读性）；超长报文（>2KB）断言日志含截断标记 `...(truncated)` 且不含完整原文 | `tests/Z.Vtuber.Tests/AliFunAsrErrorLogTests.cs` | 红 |
| 1.4 | 连接失败日志断言（新）：端点指向未监听端口（`ws://127.0.0.1:<空闲端口>/`）→ 断言异常向上传播类型不变（`HttpRequestException`/`WebSocketException` 系）、Warn 日志含 `phase=Connecting`、endpoint、model、offset；日志不含 API Key | `tests/Z.Vtuber.Tests/AliFunAsrErrorLogTests.cs` | 红 |
| 1.5 | 启动超时日志断言（新）：假 server `NeverStart` + 短 `startTimeout`（如 300ms）→ 断言抛 `TimeoutException` 且消息含 taskId 与超时值、`[Starting]` 前缀；Warn 日志含 `phase=Starting`、taskId、timeoutSeconds | `tests/Z.Vtuber.Tests/AliFunAsrErrorLogTests.cs` | 红 |
| 1.6 | 连接异常断开与停滞超时日志断言（新）：① `AbortAfterFirstBinary` 中途 RST → 断言 Warn 日志含 phase 与运行统计（bytesSent>0）；② `SilentOnFinish` 停滞超时 → 断言 Warn 日志含 `phase=Finishing`（finish-task 已发）、idle 超时值与运行统计，异常消息带阶段前缀（既有 `IdleTimeout_..._ThrowsTimeout` 仅断言异常类型，不冲突） | `tests/Z.Vtuber.Tests/AliFunAsrErrorLogTests.cs` | 红 |

### Phase 2: 实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | `FunAsrProtocol` 解析增强：① 新增 `TryParseTaskFailed(string json, out string? errorCode, out string? errorMessage)`——header 存在即返回 true，`error_code`（`ToString()`）/`error_message`（`GetString()`）缺则为 null；非 JSON/无 header 返回 false；② `ParseErrorMessage` 改为调用 `TryParseTaskFailed` 后按原格式拼装（`FunASR task failed ({code}): {message}`；false 时返回固定串），签名与输出不变 | `servers/recording-transcript-consumer/Asr/FunAsrProtocol.cs` | 1.2 转绿 |
| 2.2 | `AliFunAsrService` 阶段跟踪与运行统计：① 私有枚举 `AsrPhase { Connecting, Starting, Streaming, Finishing }`，`TranscribeAsync` 在 connect 前、run-task 发送后、task-started 后（经 `started` 完成回调或 send 循环推进点）、finish-task 发送后推进，receive/send/主枚举共享读取（`Volatile` 或简单字段——单写多读，失败日志读取允许少量竞态，不做锁）；② 私有 `sealed class RunStats`：`Stopwatch`（connect 前启动）、`long BytesSent`（send 循环累加，`Interlocked.Add`）、`int SegmentsYielded`（主枚举累加）——替代 `totalBytes`/`segmentCount` 局部变量；③ 私有 `LogFailure(phase, taskId?, stats, ...)` 统一 Warn 格式：固定输出 phase、taskId（可空）、model、elapsedMs、bytesSent、segmentsYielded，外加各场景特有字段；④ 构造函数追加可选 `TimeSpan? startTimeout = null`（缺省沿用 15s 常量），工厂不传 | `servers/recording-transcript-consumer/Asr/AliFunAsrService.cs` | 编译通过；既有测试无回归 |
| 2.3 | task-failed 原始报文记录：`ReceiveLoopAsync` 的 `EventTaskFailed` 分支（:224-228）改为——`TryParseTaskFailed` 取 code/message；`LogFailure` 记录 phase/taskId/errorCode/errorMessage/raw（raw 超 2KB 截断 + `...(truncated)`）；异常消息 `[Streaming] FunASR task failed ({code}): {message}`（解析失败时 code/message 为空但 raw 已入日志） | `servers/recording-transcript-consumer/Asr/AliFunAsrService.cs` | 1.3 转绿 |
| 2.4 | 连接失败与启动超时补日志：① `ConnectAsync`（:77）包 try/catch——`LogFailure(Connecting, ...)` 含 endpoint/model/offset/elapsedMs 后 rethrow，异常类型与传播不变；② `SendAudioLoopAsync` 的 `started.WaitAsync(startTimeout, ct)`（:275）捕获 `TimeoutException` → `LogFailure(Starting, ...)` 含 taskId/timeoutSeconds → 抛新 `TimeoutException($"[Starting] FunASR task-started not received within {timeout}s: taskId={taskId}")` | `servers/recording-transcript-consumer/Asr/AliFunAsrService.cs` | 1.4、1.5 转绿 |
| 2.5 | 既有失败日志补阶段与统计：① 连接异常断开 Close 帧（:172-182）与循环退出（:235-238）两处 Warn 经 `LogFailure` 补 phase/统计，IOException 消息加 `[{Phase}]` 前缀；② idle 超时（:248-252）Warn 补 phase/统计，TimeoutException 消息加 `[{Phase}]` 前缀；③ receive 兜底 catch（:254-257）Warn 补 phase/统计 | `servers/recording-transcript-consumer/Asr/AliFunAsrService.cs` | 1.6 转绿；`AliFunAsrOffsetTests` 全量回归绿 |

### Phase 3: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 3.1 | 全量测试与覆盖率：`dotnet test tests/Z.Vtuber.Tests` 全绿（含既有回归）；`AliFunAsrService`/`FunAsrProtocol` 核心逻辑覆盖率 ≥ 80%（common-rules） | 全部通过 |
| 3.2 | 安全与红线 grep：确认日志/异常消息不含 API Key——`Authorization`/`_apiKey` 不出现在任何 `LogFailure`/异常消息拼装处；双格式（日志与落库 Error）错误码一致性抽查 | grep 无违规 |
| 3.3 | 文档同步：`servers/recording-transcript-consumer/AGENTS.md` 的 `Asr/` 条目补充「分阶段失败日志（Connecting/Starting/Streaming/Finishing）+ 原始报文截断记录 + 运行统计」描述；本 Plan 与提案/PRD 链接互相有效 | 文档与实现一致 |
| 3.4 | 手工验证（z-aspire-orchestrator，可选但推荐）：本地 Aspire 拉起 → 故意配置无效模型名触发一次真实 task-failed → 确认服务日志含错误码/原始报文/阶段/统计、任务详情失败原因含 `[Streaming]` 前缀与错误码 | 手工验证记录 |

## 4. 并行策略

- 本需求改动收敛于单一子项目线（`servers/recording-transcript-consumer` + `tests/Z.Vtuber.Tests`），文件面小（2 个源文件 + 1 个新测试文件 + 1 个测试基线文件），**不拆并行 subagent**，单线串行实施即可，避免 Fake/测试基线的共享文件冲突。
- Phase 1 内 1.1（测试基线）必须先行——1.3/1.5/1.6 依赖 `NeverStart` 能力与 `startTimeout` 透传；1.2-1.6 之间无相互依赖，但同文件编写按序进行。
- Phase 2 内 2.1 → 2.2 →（2.3/2.4/2.5 大体串行，同文件）；2.3 依赖 2.1 的 `TryParseTaskFailed`，2.4/2.5 依赖 2.2 的 `AsrPhase`/`RunStats`/`LogFailure`。
- 全部提交在需求总分支 `prd/transcript-asr-error-observability`。

## 5. 验收对照

| PRD 功能点 | 实现任务 | 验证方式 |
|-----------|---------|---------|
| 功能点 1：三方失败错误完整记录 | 2.1、2.3 | 1.2、1.3 + 3.2 grep |
| 功能点 2：识别各阶段失败可区分 | 2.2①③、2.4、2.5 | 1.3、1.4、1.5、1.6（phase 断言） |
| 功能点 3：失败日志携带运行上下文 | 2.2②③、2.3、2.4、2.5 | 1.3、1.6（elapsedMs/bytesSent/segmentsYielded 断言） |
| 功能点 4：任务详情失败原因可读 | 2.3、2.4、2.5（异常消息阶段前缀，Runner 零改动落库） | 1.3、1.5、1.6（异常消息断言）+ 3.4 手工验证 |
| 非功能：安全红线（无密钥）、覆盖率 ≥ 80%、无回归 | 3.1、3.2 | `dotnet test` / grep |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| 阶段推进点语义偏差 | task-started 由 receive 任务异步置位，主枚举与 send 循环对「Streaming 起点」的观测存在天然竞态 | 阶段只用于日志标注，允许近似：run-task 发送后置 Starting，`started` 完成后置 Streaming，finish-task 发送后置 Finishing；测试按提案约定值断言（1.3） |
| RunStats 跨任务读写竞态 | send/receive/主枚举三任务共享统计，读取发生在失败路径 | 各任务只写各自字段（BytesSent 用 `Interlocked.Add`），失败日志读取容忍少量滞后，不引入锁（提案 §6.1 决策 3） |
| `ParseErrorMessage` 行为漂移 | 内部改由 `TryParseTaskFailed` 实现可能改变空值插值细节 | 1.2④ 锁定两种输出形态；既有 `ServerTaskFailed_SurfacesErrorCodeAndMessage` 回归不动 |
| 启动超时测试时长 | `StartTimeout` 常量 15s 不可用于测试 | 构造函数可选 `startTimeout`（2.2④），测试传 300ms（1.5）；生产路径不传保持 15s |
| 原始报文含敏感信息 | task-failed 为服务端→客户端方向报文，协议上不含客户端凭据 | 提案 §5.5 已论证；3.2 grep 兜底确认 `_apiKey`/`Authorization` 不入日志 |
| 依赖 | FunASR websocket task-failed 报文约定（`header.error_code`/`error_message`）；既有 `FakeFunAsrServer`/`ListLogger` 测试设施 | 解析失败降级仅记 raw（2.3）；设施已核实存在（§1.0） |

## 7. 相关文档

- PRD：`docs/product/draft/20260918-transcript-asr-error-observability-prd.md`（v1.0）
- 技术提案：`docs/engineering/proposals/20260918-proposal-transcript-asr-error-observability.md`（v1.0）
- 前序 Plan：[[20260916-plan-recording-transcript]]、[[20260917-plan-transcript-model-context]]
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`servers/recording-transcript-consumer/AGENTS.md`
- FunASR realtime websocket API：<https://platform.qianwenai.com/docs/api-reference/speech-recognition/fun-asr-realtime/websocket-api.md>
