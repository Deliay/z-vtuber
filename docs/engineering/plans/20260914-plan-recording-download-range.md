---
description: 录播文件下载接口支持 HTTP Range 实现 Plan——基于 PRD v1.0 与技术提案（prd/recording-download-range）的实施阶段划分与任务分解；契约先行（recording/bilibili-crawler 两份 OpenAPI 补 Range/If-Range 参数与 206/416/Accept-Ranges 语义），recording 零代码改动仅补回归测试，api 代理透传 Range/If-Range 请求头与 206/416 状态码及分段响应头，web 无变更
type: Permanent
---

# 录播文件下载支持 Range 分段下载 - 实现 Plan

**功能名称**: 录播文件下载支持 Range
**关联 PRD**: `docs/product/draft/20260914-recording-download-range-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20260914-proposal-recording-download-range.md`
**Plan 版本**: v1.0
**创建日期**: 2026-09-14
**作者**: Workflow Agent（代起草）
**需求总分支**: `prd/recording-download-range`（已推送 origin；需求总 PR：<https://github.com/Deliay/z-vtuber/pull/102>）

## 1. 概述

本 Plan 基于 PRD v1.0 与技术提案编写，指导「录播下载接口支持 HTTP Range」的编码实施。核心结论：recording 服务 `DownloadRecording` 已用 `PhysicalFile(enableRangeProcessing: true)` 原生支持 Range，能力被 api 代理层截断——本需求本质是打通 api 代理透传。改动分三块：① 契约——`contracts/recording.openapi.yaml` 与 `contracts/bilibili-crawler.openapi.yaml` 补 Range/If-Range 请求头参数、200 补 `Accept-Ranges`、新增 206/416 响应（先于实现）；② api——`BilibiliLiveRecordingController.DownloadRecording` 透传 Range/If-Range 请求头、原样透传 200/206 状态码与 `Accept-Ranges`/`Content-Range`/`Content-Length` 响应头、新增 416 透传分支；③ 测试——recording 直连 206/416 回归锁定 + api 代理透传用例。recording 代码零改动，web 无变更。实施全程遵循 `docs/engineering/common-rules.md`（TDD、契约式开发、核心逻辑覆盖率 ≥ 80%）、`backend-rules.md` 及各子项目 AGENTS.md。

### 1.0 上游核实结论（代码现状复查）

对需求总分支 `prd/recording-download-range`（基于 main，含已合入的 `feat/live-recording` 与 `feat/recording-file-list-metadata`）代码核实，提案技术路径**整体可行**，现状细节如下：

- **recording 已原生支持 Range**：`RecordingController.DownloadRecording`（`servers/recording/Controllers/RecordingController.cs:70-99`）顺序为「`IsSafeFileName` 400（:73-76）→ 路径穿越防护（:78-85）→ 录制中 400（:87-91）→ 存在性 404（:93-96）→ `PhysicalFile(..., enableRangeProcessing: true)`（:98）」。`enableRangeProcessing: true` 确实在位，框架自动处理单区间解析、206/416、`Accept-Ranges`、`If-Range` 与多区间回退，recording 侧零代码改动成立。提案标注行号 :70-99 与实际一致。
- **api 代理截断点确认**：`BilibiliLiveRecordingController.DownloadRecording`（`servers/api/Controllers/BilibiliLiveRecordingController.cs:126-182`）以 `client.GetAsync(url, ResponseHeadersRead, ct)` 转发（:133-136）——**不转发 Range/If-Range**；响应成功分支（:169-180）仅透传 `Content-Type` 与 `Content-Disposition`，丢弃 `Accept-Ranges`/`Content-Range`/`Content-Length`；非成功仅特判 404（:150-153）与 400 透传（:155-162，含录制中/非法文件名，上一需求已加），其余归并 502（:164-167）。提案标注行号 :126-182 与实际一致。
- **416 当前会被归并 502**：上游 `PhysicalFile` 产生的 416 落到 :164 的 `!IsSuccessStatusCode` 分支——证实提案「416 单独透传」的必要性。
- **契约锚点确认**：`contracts/recording.openapi.yaml` 下载端点 `GET /api/{roomId}/recordings/{fileName}` 在 :61-97（现有 200/400/404）；`contracts/bilibili-crawler.openapi.yaml` 代理端点 `GET /api/b/live/{roomId}/recordings/{fileName}` 在 :300-333（现有 200/400/404/502）。两份契约均无 Range/If-Range 参数与 206/416 响应，待补。
- **测试设施现状**：`tests/Z.Vtuber.Tests` 已有 `RecordingApiTests`（MongoDbFixture 集成测试，11 个用例）与 `BilibiliLiveRecordingControllerTests`（`StubHandler`/`StubHttpClientFactory` fake 设施，10 个用例），本次在既有文件上增补用例，无需新增测试设施。
- **分支命名说明**：技术提案头部写的 feat-branch 为 `feat/recording-download-range`，实际需求总分支按本工作流约定为 `prd/recording-download-range`（z-git 规范中 prd 为产品文档/需求分支 type），本 Plan 以实际分支为准。

### 1.1 实施范围

- **做**：
  - 契约：两份 OpenAPI 下载端点新增可选 `Range`/`If-Range` 请求头参数；`200` 响应补 `Accept-Ranges: bytes` 头部与「不带 Range 返回完整文件」描述；新增 `206`（`Content-Range`/`Accept-Ranges` 头 + octet-stream 体）与 `416`（`Content-Range: bytes */<total>` 头）响应。
  - api：`DownloadRecording` 改显式构造 `HttpRequestMessage`，`TryAddWithoutValidation` 透传 `Range`/`If-Range`；成功分支透传状态码（200/206）与 `Accept-Ranges`/`Content-Range`/`Content-Length`；新增 416 透传分支；404/400/502 分支不变。
  - recording：零代码改动；补接口测试锁定 `enableRangeProcessing: true` 行为。
  - 测试：后端两个测试文件增补用例，核心逻辑覆盖率 ≥ 80%，无回归。
- **不做**：recording 控制器代码改动；录制流程与状态上报；ETag/If-None-Match 缓存协商；多重区间 multipart 响应（框架回退 200 整文件，透传即可）；web 端（下载 URL 不变，浏览器/播放器自动受益）。

### 1.2 现状基线

- **代理透传先例**：同控制器下载 400 透传分支（:155-162，`Content(json, "application/json")` + 显式 `StatusCode`）——416 透传复用同模式（设 `Response.StatusCode`、透传头、返回空体）。
- **流式转发先例**：成功分支 `CopyToAsync(Response.Body, ct)`（:179）+ `ResponseHeadersRead`——分段响应沿用，内存占用不变。
- **请求头构造先例**：`HttpRequestMessage` + `TryAddWithoutValidation` 为本控制器首次使用，但属 ASP.NET Core 代理标准手法；`Uri.EscapeDataString(fileName)`（:134）保持不变。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash）与 Conventional Commits：

- 本需求开发全程只使用需求总分支 `prd/recording-download-range`：契约、实现、测试、文档全部直接在该分支提交，不切开发子分支（沿用既定做法）。
- 需求总分支已推送 origin，需求总 PR 已存在（#102），提交照常推送更新分支。
- 提交信息使用 Conventional Commits：契约 `feat:`、测试先行 `test:`、实现 `feat:`、文档 `docs:`；测试与实现分开提交。
- 改动面小（一个控制器方法 + 两份契约 + 两个测试文件），全程单线串行即可，无需拆 subagent 并行（无前端改动，无文件写交集）。

## 3. 实施阶段

按「契约先行 → TDD 红-绿 → 收口验证」推进，全部串行。

### Phase 0: 契约先行

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | `contracts/recording.openapi.yaml` `GET /api/{roomId}/recordings/{fileName}`（:61 起）：① `parameters` 新增可选 header 参数 `Range`（描述：字节区间请求如 `bytes=0-1023`，仅支持单区间，多区间回退 200 整文件）与 `If-Range`（条件 Range，校验失败回退 200）；② `200` 响应 headers 补 `Accept-Ranges`（`bytes`），description 补「不带 Range 时返回完整文件」；③ 新增 `206` 响应（headers `Content-Range`: `bytes <start>-<end>/<total>`、`Accept-Ranges`，content `application/octet-stream` binary）与 `416` 响应（header `Content-Range`: `bytes */<total>`） | `contracts/recording.openapi.yaml` | `npx openapi-typescript` 可成功生成类型 |
| 0.2 | `contracts/bilibili-crawler.openapi.yaml` `GET /api/b/live/{roomId}/recordings/{fileName}`（:300 起）同步 0.1 全部变更（Range/If-Range 参数、200 补 `Accept-Ranges`、新增 206/416），description 补「支持 Range 分段下载（206），越界 416」 | `contracts/bilibili-crawler.openapi.yaml` | codegen 通过 |

### Phase 1: 测试先行（红）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | `RecordingApiTests` 增补（recording 直连，锁定框架行为防回退）：① 预置录播文件，`Range: bytes=0-99` → 206，响应体恰为前 100 字节，`Content-Range: bytes 0-99/<total>` 且 total 与文件大小一致；② `Range: bytes=<start>-`（省略结尾）与 `Range: bytes=-<suffix>`（尾部 N 字节）语义正确；③ 起始位置 ≥ 文件大小 → 416 且 `Content-Range: bytes */<total>`；④ 无 Range → 200 完整文件且带 `Accept-Ranges: bytes`；⑤ 回归：录制中 400、不存在 404、非法文件名 400（带 Range 亦同） | `tests/Z.Vtuber.Tests/RecordingApiTests.cs` | 红（若意外全绿说明 `enableRangeProcessing` 行为已被测试覆盖，需确认用例确为新增断言） |
| 1.2 | `BilibiliLiveRecordingControllerTests` 增补（StubHandler 断言上游请求 + 代理响应）：① 带 `Range`/`If-Range` 请求 → 上游请求收到相同头值；② 上游 206 → 代理 206，`Content-Range`/`Accept-Ranges`/`Content-Length` 透传，响应体为片段；③ 上游 416 → 代理 416 且 `Content-Range` 透传（不归并 502）；④ 上游 200 → 200 且 `Accept-Ranges` 透传；⑤ 不带 Range 请求 → 上游请求无 Range 头；⑥ 回归：上游 400 透传、404、不可达 502 行为不变 | `tests/Z.Vtuber.Tests/BilibiliLiveRecordingControllerTests.cs` | 红 |

### Phase 2: 实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | api 代理 Range 透传：`DownloadRecording`（:126-182）改造——① `client.GetAsync(...)` 改为显式构造 `HttpRequestMessage(HttpMethod.Get, url)`，从 `Request.Headers` 读取 `Range`/`If-Range` 并以 `TryAddWithoutValidation` 附加（格式非法由上游框架按标准忽略），`SendAsync(request, ResponseHeadersRead, ct)` 发送；URL 转义与 404/400/502/异常映射分支保持不变；② 成功分支：`Response.StatusCode = (int)response.StatusCode`（200/206 原样），透传 `AcceptRanges`（`response.Headers`）、`ContentRange`、`ContentLength`（`response.Content.Headers`）与既有 `ContentType`/`ContentDisposition`，响应体仍 `CopyToAsync` 流式转发；③ 新增 416 分支（置于 400 分支旁）：`Response.StatusCode = 416`，透传 `Content-Range` 头，返回空体；④ 其余非成功仍归并 502 | `servers/api/Controllers/BilibiliLiveRecordingController.cs` | 1.2 转绿；既有测试无回归 |
| 2.2 | recording 侧确认：零代码改动；仅确认 1.1 用例全部通过（`enableRangeProcessing: true` 行为被测试锁定） | `servers/recording/Controllers/RecordingController.cs`（不改） | 1.1 转绿 |

### Phase 3: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 3.1 | `dotnet test tests/Z.Vtuber.Tests` 全绿（含既有回归）；代理透传逻辑覆盖率 ≥ 80%；grep 确认两份契约的 Range/206/416/Accept-Ranges 语义一致 | 全部通过 |
| 3.2 | 编排手工验证（z-aspire-orchestrator）：本地 Aspire 拉起 → 对已有录播文件 `curl -H "Range: bytes=0-1023"` 经 api 端口下载 → 206 + `Content-Range: bytes 0-1023/<total>` + 体为前 1024 字节；`curl -H "Range: bytes=<total+1>-"` → 416 + `Content-Range: bytes */<total>`；不带 Range → 200 完整文件 + `Accept-Ranges: bytes`；带 Range 请求录制中文件 → 400 | 手工验证记录 |
| 3.3 | AGENTS.md 同步：`servers/api/AGENTS.md`（`BilibiliLiveRecordingController` 下载描述补「透传 Range/If-Range，支持 206/416 透传」、API 面相关行同步）；`servers/recording/AGENTS.md`（下载行补「支持 Range 分段下载 206/416」） | 文档与实现一致 |

## 4. 并行策略

本需求无并行必要：改动集中于 api 单个控制器方法，recording 零代码改动，无前端变更。Phase 0 → 1 → 2 → 3 单线串行；1.1 与 1.2 分属不同测试文件可同批编写。全部提交在需求总分支 `prd/recording-download-range`。

## 5. 验收对照

| PRD 功能点 | 实现任务 | 验证方式 |
|-----------|---------|---------|
| 功能点 1：单区间 Range 下载（206/Content-Range/Accept-Ranges） | 0.1、0.2、2.1①② | 1.1①②④、1.2①②④ + 手工验证（3.2） |
| 功能点 2：越界与非法区间 416 | 0.1③、0.2、2.1③ | 1.1③、1.2③ + 手工验证（3.2） |
| 功能点 3：既有行为不回归（录制中 400/404/非法文件名 400/502） | 2.1（保持既有分支） | 1.1⑤、1.2⑥ |
| 非功能：流式不整文件缓冲、覆盖率 ≥ 80%、全量测试绿 | 2.1②（CopyToAsync 不变）、3.1 | `dotnet test` |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| `Content-Length` 透传与 `CopyToAsync` 字节数不一致 | 上游头值与实际转发字节理论上一致（同一流） | 1.2② 断言头与体一致；若框架层有干扰（如响应压缩中间件）则去掉 `Content-Length` 透传让 Kestrel 自行处理 |
| 非法 Range 头经 `TryAddWithoutValidation` 直达上游 | 上游 `PhysicalFile` 按标准忽略非法头回退 200，不会 5xx | 1.1 覆盖标准语义；1.2⑤ 确认不带 Range 时不附加头 |
| 多区间请求 | ASP.NET Core 仅支持单区间，多区间回退 200 整文件 | PRD 3.5 已明确接受；代理无需特判 |
| `If-Range` 语义 | recording 未设置 ETag 时 `PhysicalFile` 以 Last-Modified 求值，失败回退 200 | 提案 §6.1 决策 3：原样透传由上游框架判定，代理不解释 |
| 依赖 | 无新增 NuGet/npm 依赖；`enableRangeProcessing: true` 已在位（1.0 核实） | 已核实 |

## 7. 相关文档

- PRD：`docs/product/draft/20260914-recording-download-range-prd.md`（v1.0）
- 技术提案：`docs/engineering/proposals/20260914-proposal-recording-download-range.md`
- 契约：`contracts/recording.openapi.yaml`、`contracts/bilibili-crawler.openapi.yaml`
- 录播功能 Plan：`docs/engineering/plans/20260913-plan-live-recording.md`
- 录播列表元信息 Plan：`docs/engineering/plans/20260914-plan-recording-file-list-metadata.md`
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`servers/api/AGENTS.md`、`servers/recording/AGENTS.md`、`servers/common/AGENTS.md`
