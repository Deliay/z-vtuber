---
description: AI 训练「对外提供绘图接口」实现 Plan：training 新增 POST /api/training/images/generate（[Consumes multipart/form-data] + [FromForm] text/image/modelId，校验顺序 text→image 存在→类型白名单→大小上限），模型选择显式 modelId（404/400/能力 Matches 校验）优先、缺省走功能点 image-generate（输入 text,image / 输出 image，指定→默认→无 409 no image model available），新增 IImageModelClient 族（OpenAiLikeImageClient POST {endpoint}/images/edits multipart → data[0].b64_json 解析；Claude-like 抛 ModelImageNotSupportedException → 400），成功 200 image/png 二进制透传、失败统一 {"error":"..."}，契约先行（training.openapi.yaml +1 端点 + bilibili-crawler 镜像 + codegen）+ api 代理 +1 镜像 action（ForwardAsync 零改动透传），TDD 假模型服务契约测试覆盖全错误矩阵，覆盖率 ≥ 80%
type: Permanent
---

# AI 训练 - 对外提供绘图接口 - 实现 Plan

**功能名称**: 训练 - 对外提供绘图接口（文本 + 图片 → 图片）
**关联 PRD**: `docs/product/draft/20261001-training-image-generate-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20261001-proposal-training-image-generate.md`（v1.0）
**Plan 版本**: v1.0
**创建日期**: 2026-10-02
**作者**: Workflow Agent（代起草）
**需求总分支**: `prd/training-image-generate`（需求总 PR #171）
**feat-branch**: `feat/training-image-generate`（实现分支，基于需求总分支切出）

## 1. 概述

依据 PRD 与技术提案落地四件事：① training 新增绘图端点 `POST /api/training/images/generate`（multipart 输入 text + image 文件 + 可选 modelId，成功返回图片二进制）；② 模型选择（显式 modelId 校验优先，缺省按功能点 `image-generate` 指定→默认解析，无可用模型 409）；③ 新增 `IImageModelClient` 图片客户端族（OpenAI-like `/images/edits` multipart → `b64_json` 解析，Claude-like 抛不支持）；④ 契约先行 + `servers/api` 代理 +1 镜像 action。实施遵循 `docs/engineering/common-rules.md`（契约先行、TDD、覆盖率 ≥ 80%、单用例 ≤ 5s）、`backend-rules.md`、z-coding 流程与相关模块 `AGENTS.md`。

### 1.0 现状核实结论（读码确认）

- **契约**：`contracts/training.openapi.yaml` 现 30 个 operationId（health + 29 业务）；`contracts/bilibili-crawler.openapi.yaml` 现 `/api/training/**` 29 条路径镜像；二进制响应先例 synthesize `audio/wav: type: string, format: binary`，错误体统一 `$ref ErrorResponse`，无任何 `multipart/form-data` 先例（新增端点是首个 inbound multipart 契约）。
- **api 代理**（`servers/api/Controllers/TrainingProxyController.cs`）：29 个镜像 action + 私有 `ForwardAsync`——POST/PUT 以 `StreamContent(Request.Body)` 透传请求体并原样附加 `Content-Type`（multipart boundary 保留），响应透传状态码/`Content-Type`/`Cache-Control`/`Content-Disposition` + `CopyToAsync` 二进制流，上游不可达/超时 502 `{"error":"training service unavailable"}`；超时 `TrainingService:RequestTimeoutSeconds`（`DefaultTimeoutSeconds = 180`），由 `TrainingProxyTimeoutConsistencyTests` 守护「代理 180s > training 上游预算 120s」。**multipart 请求与 `image/*` 响应零改动透传，仅需 +1 镜像 action**（提案 ③ 成立）。
- **inbound multipart**：training 无消费先例（outbound 先例 `Clients/IndexTtsClient.BuildForm` 手写 `MultipartFormDataContent`）；按 ASP.NET 标准 `[Consumes("multipart/form-data")]` + `[FromForm] IFormFile` 接入。
- **模型/功能点基建**（提案 ① 成立）：`TrainingModelCapabilities`（`servers/common/training/Contracts/`，四值 `audio`/`video`/`text`/`image` + `Normalize`/`Matches`（模型须覆盖所需集合））；`ITrainingModelResolver.ResolveAsync(slug, name, input, output)` → `ModelResolution(Slug, ModelId, ModelSlug, Source)`（`specified`/`default`/`none`）+ `ListCandidatesAsync`（`Enabled` + 能力匹配，`CreatedAt` 升序），登记 upsert 由 `TrainingFeaturePointRepository.RegisterAsync` 自动完成；`FeaturePointIds` 现 3 个功能点定义（`dataset-clean`/`emotion-from-audio`/`emotion-from-text`），+1 个常量与定义即可。
- **显式模型校验先例**：`TrainingItemToolsController.Recognize`——`ObjectId.TryParse` 失败按不存在 404 `model not found` → 400 `model disabled` → Provider 404；但其「`model does not support audio input`」实为**派发层** `ModelAudioNotSupportedException` 兜底映射，编排层显式 `Matches` 能力校验先例在 `TrainingFeaturePointsController`（400 `model not qualified`）。
- **Ai 层**（提案 ② 成立）：`IChatModelClientFactory`/`ChatModelClientFactory`（`ClientName = "AiModel"` named client，挂 `ModelSessionHeaderHandler`（`x-opencode-session`）+ `SocketsHttpHandler.AllowAutoRedirect = false`）；`OpenAiLikeChatClient` 构造时设 `HttpClient.Timeout = AiOptions.RequestTimeoutSeconds`（默认 120s）、非 2xx 抛 `HttpRequestException`；`ModelExceptions` 已有 `ModelAudioNotSupportedException`（→400）、`ModelOutputException`（→502）、`ProviderTypeException`、`AudioTooLargeException` 先例。
- **错误体与二进制返回**：`Contracts/Dtos.cs` `ErrorResponse` → `{"error":"..."}`；`TrainingSynthesizeController` 成功 `File(result.Audio, "audio/wav")` 先例；409 先例 `no speaker reference`。
- **配置**：`Configuration/TrainingOptions.cs` 内 `AiOptions`（节 `Ai`）等均「`AddOptions<T>().Bind().ValidateOnStart()` + 单例快照」注册模式；无 `Image` 节（本期新增）。
- **测试设施**：`tests/Z.Vtuber.Tests` 有 `TrainingSynthesizeApiTests`（`MongoDbFixture` + 控制器直构 + Stub client）、`IndexTtsClientTests`/`ModelDiscoveryClientTests`（假 `HttpMessageHandler` 契约测试，不打外网）、`TrainingProxyControllerTests`（代理透传）、`TrainingSynthesizeProxyTests`（二进制响应代理透传）、`TrainingProxyTimeoutConsistencyTests`（超时口径）先例。
- **流水线**：`.github/workflows/build-training-image.yml` paths 已含 `servers/training/**` + `servers/common/{core,training,transcript}/**`，本期无需调整。

### 1.1 口径澄清（规划期裁定，随实现评审）

1. **成功响应 Content-Type 实际固定 `image/png`**：OpenAI-like `b64_json` 不携带 mime，`GeneratedImage.MimeType` 缺省 `image/png`；契约 200 按 `image/png` 描述（属于 PRD `image/*` 口径），验收按 PRD 核对。
2. **上传图片类型判定按 multipart part 的 `Content-Type` 白名单**（`image/png`/`image/jpeg`/`image/webp`），不做魔数嗅探；转发上游时保留原文件名与 `Content-Type`。
3. **能力校验双层**：编排层显式 `Matches` 校验（显式 modelId：输入须覆盖 `text,image` → 400 `model does not support image input`；输出须含 `image` → 400 `model does not support image output`）；派发层 `ModelImageNotSupportedException` 为 Claude-like 兜底（同 400 `model does not support image output` 文案）。
4. **`modelId` 非 ObjectId 一律按不存在 → 404 `model not found`**（与 recognize 同口径）。
5. **大小校验双保险**：`IFormFile.Length` 前置拦截（> `Image:MaxImageBytes` → 400 `image too large`），读取后按实际字节数复核；所有输入校验失败均**不发起上游模型调用**。
6. **超时口径**：`Image:RequestTimeoutSeconds`（默认 120s）设在 `OpenAiLikeImageClient` 实例（`HttpClient.Timeout`，同 `OpenAiLikeChatClient` 手法）；`TaskCanceledException`（非客户端取消）→ 502 `model service unavailable`；代理 180s 不变。
7. **上游结果仅认 `b64_json`**：响应缺 `data[0].b64_json`、base64 解码失败、`url` 形态 → `ModelOutputException` → 502 `invalid model output`（本期不回拉，提案 §6.1 决策）。
8. **本期无 web/UI 改动**；`npm run codegen` 仅随契约再生成 `apps/web/src/api/schema.d.ts`/`schema-training.d.ts`（勿手改）。

### 1.2 实施范围

- **做**：`contracts/`（training +1 端点 +1 schema；bilibili-crawler +1 镜像；web codegen 生成物）；`servers/training`（`Ai/IImageModelClient.cs` + `Ai/ImageModelClientFactory.cs` + `Ai/OpenAiLikeImageClient.cs`、`Ai/ModelExceptions.cs` +1 异常、`ImageGeneration/ImageGenerateService.cs` + `NoImageModelAvailableException`、`Controllers/TrainingImageGenerateController.cs`、`FeaturePoints/FeaturePointIds.cs` +1 定义、`Configuration/ImageOptions.cs`、`Program.cs` 注册）；`servers/api/Controllers/TrainingProxyController.cs`（+1 action）；`tests/Z.Vtuber.Tests`（3 个测试文件新增/变更）；`AGENTS.md` 同步（training/api）。
- **不做**：生成结果入库/缓存/生成记录；批量生成；Web 绘图界面；`url` 形态结果回拉；多图输入（`image[]`）；尺寸/数量等上游参数透传；Claude-like 图片输出；绘图后台任务化；新增 NuGet、环境变量、流水线、除 `Image` 节外的配置。

## 2. 分支与提交流程

- 需求总分支 `prd/training-image-generate`（基于 main @ `515cb11`，PRD 与技术提案已随该 commit 提交，需求总 PR #171）：本 Plan 提交到该分支随 PR 评审。
- 实现分支 `feat/training-image-generate`（基于需求总分支切出，提案 §0 口径）：实现按 z-coding「编写实现」流程推进（子项目为单位委托 subagent、TDD 红-绿、测试与实现分开提交），实现 PR 回需求总分支评审。
- **实现记录（已完成）**：实现分支 `feat/training-image-generate`，实现 PR https://github.com/Deliay/z-vtuber/pull/172（目标分支 `prd/training-image-generate`）。契约 + codegen、training Ai 图片客户端/编排/端点/配置/注册、api 代理镜像、测试与文档同步均已落地；训练相关回归 490/490 通过，新增核心逻辑覆盖率 ≥ 80%（`OpenAiLikeImageClient`/`ImageModelClientFactory`/`ImageGenerateService`/`FeaturePointIds` 100%，控制器 ≥ 88%）。
- 提交顺序（Conventional Commits）：
  1. `docs(training):` 本实现 Plan（需求总分支）；
  2. `feat(contracts):` 契约变更 + `npm run codegen`；
  3. `feat(training):` Ai 图片客户端 + 编排 + 控制器 + 功能点定义 + 配置 + 注册（含 `test(training):` 配套）；
  4. `feat(api):` 代理镜像 action（含 `test(api):` 配套）；
  5. `docs(training):` `AGENTS.md` / PRD 验收勾选 / 文档互链同步。

## 3. 实施阶段

### Phase 0: 技术文档（先行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | PRD（上游节点产出） | `docs/product/draft/20261001-training-image-generate-prd.md` | 已完成（`515cb11`） |
| 0.2 | 技术提案（上游节点产出） | `docs/engineering/proposals/20261001-proposal-training-image-generate.md` | 已完成（`515cb11`） |
| 0.3 | 实现 Plan（本文档） | `docs/engineering/plans/20261002-plan-training-image-generate.md` | 任务/文件/验收明确，与提案口径一致 |

### Phase 1: 契约先行

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | +1 端点 `POST /api/training/images/generate`（operationId `generateTrainingImage`；`multipart/form-data` requestBody：`text`（必填）/`image`（binary，必填）/`modelId`（可选）→ 新增 schema `TrainingImageGenerateRequest`）；响应 200 `image/png`（binary）、400（`text is required`/`image is required`/`unsupported image type`/`image too large`/`model disabled`/`model does not support image input`/`model does not support image output`）、404 `model not found`、409 `no image model available`、502（`model service unavailable`/`invalid model output`），错误体统一 `ErrorResponse` | `contracts/training.openapi.yaml` | 契约自洽，错误口径与提案 §3.1 逐字一致 |
| 1.2 | `/api/training/images/generate` 1 条路径镜像（multipart 请求体与 `image/png` 响应透传；400/404/409/502 透传；上游不可达 502 `{"error":"training service unavailable"}`） | `contracts/bilibili-crawler.openapi.yaml` | 与 1.1 一致 |
| 1.3 | `npm run codegen` 再生成 `schema.d.ts`/`schema-training.d.ts`（勿手改） | `apps/web/src/api/` | 生成物与契约一致 |

### Phase 2: training Ai 图片客户端层（TDD：每任务测试先行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | `ModelExceptions` + `ModelImageNotSupportedException`（消息 `model does not support image output`，映射 400） | `servers/training/Ai/ModelExceptions.cs` | 与 `ModelAudioNotSupportedException` 同构 |
| 2.2 | 图片客户端抽象：`GenerateImageInput(Bytes, MimeType, FileName)` / `GeneratedImage(Bytes, MimeType)`；`IImageModelClient.GenerateImageAsync(provider, model, prompt, image, ct)`（传输失败/超时/非 2xx 抛 `HttpRequestException`、输出不可解析抛 `ModelOutputException`、Provider 不支持图片输出抛 `ModelImageNotSupportedException`）+ `IImageModelClientFactory.Create(providerType)` | `servers/training/Ai/IImageModelClient.cs` | 与提案 §3.2 签名一致 |
| 2.3 | `OpenAiLikeImageClient`：`POST {provider.Endpoint}/images/edits`（Bearer）+ `MultipartFormDataContent`（`model`/`prompt`/`image`（`ByteArrayContent`，带原文件名与 Content-Type））；解析 `data[0].b64_json` → base64 解码；缺字段/解码失败/`url` 形态 → `ModelOutputException`；非 2xx/超时/传输失败 → `HttpRequestException`；`HttpClient.Timeout = Image:RequestTimeoutSeconds`；复用 named client `AiModel`（`x-opencode-session` 头、`AllowAutoRedirect=false` 一致） | `servers/training/Ai/OpenAiLikeImageClient.cs` | `OpenAiLikeImageClientTests`（假 `HttpMessageHandler`）转绿 |
| 2.4 | `ImageModelClientFactory`：按 `provider.Type` 派发（`openai` → `OpenAiLikeImageClient`；`claude` 及未知类型 → 抛 `ModelImageNotSupportedException`） | `servers/training/Ai/ImageModelClientFactory.cs` | 派发用例转绿 |

### Phase 3: training 编排与端点（TDD：每任务测试先行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 3.1 | `ImageOptions`（节 `Image`，`ValidateOnStart`）：`MaxImageBytes`（默认 10485760 = 10MB）、`RequestTimeoutSeconds`（默认 120），`[Range]` 校验 | `servers/training/Configuration/ImageOptions.cs` | 与 `AiOptions` 同构；代理 180s > 120s 口径不变 |
| 3.2 | 功能点定义 +1：`ImageGenerate = "image-generate"` 与 `ImageGenerateDefinition = new(ImageGenerate, "绘图生成", [Text, Image], [Image])` | `servers/training/FeaturePoints/FeaturePointIds.cs` | 与 PRD §3.2 功能点 2 能力表一一对应 |
| 3.3 | `ImageGenerateService.GenerateAsync(text, modelId?, image, ct)`：显式 modelId（`ObjectId.TryParse`+`GetByIdAsync` 404 `model not found` → `Enabled` 400 `model disabled` → `Matches` 输入覆盖 `text,image`/输出含 `image`，否则 400 `model does not support image input`/`model does not support image output`）；缺省 `ResolveAsync(ImageGenerateDefinition)`（`none` → `NoImageModelAvailableException`，映射 409）；读 Provider → `IImageModelClientFactory.Create(provider.Type)` → `GenerateImageAsync`；Info 日志 `training image-generate: modelSlug={} imageBytes={} elapsedMs={}`（不打文本/图片内容/apiKey） | `servers/training/ImageGeneration/ImageGenerateService.cs`、`ImageGeneration/NoImageModelAvailableException.cs` | `ImageGenerateServiceTests`/API 测试转绿（指定优先/默认首候选/无 409） |
| 3.4 | `TrainingImageGenerateController`：`[Route("api/training/images")]` + `[HttpPost("generate")]` + `[Consumes("multipart/form-data")]`；签名 `Generate([FromForm] string? text, [FromForm] IFormFile? image, [FromForm] string? modelId, ct)`；校验顺序：text trim 非空（400 `text is required`）→ image 存在（400 `image is required`）→ 类型白名单（400 `unsupported image type`）→ 大小 ≤ `Image:MaxImageBytes`（400 `image too large`）；异常映射：`NoImageModelAvailableException` → 409、`ModelImageNotSupportedException` → 400、`ModelOutputException` → 502 `invalid model output`、`HttpRequestException`/`TaskCanceledException`（非客户端取消）→ 502 `model service unavailable`；成功 `File(result.Bytes, result.MimeType)` | `servers/training/Controllers/TrainingImageGenerateController.cs` | `TrainingImageGenerateApiTests` 转绿（全错误矩阵 + 校验失败不发起上游调用） |
| 3.5 | DI 注册：`ImageOptions`（`AddOptions` + `ValidateOnStart` + 单例快照）、`IImageModelClientFactory`、`ImageGenerateService` | `servers/training/Program.cs` | 编译通过、启动幂等、既有注册不回退 |

### Phase 4: api 代理镜像

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 4.1 | +1 镜像 action：`[HttpPost("training/images/generate")]` → `ForwardAsync(HttpMethod.Post, "api/training/images/generate", ct)`；通用 `ForwardAsync` 已透传 multipart 请求体流与 `Content-Type`（boundary 保留）及 `image/png` 二进制响应，零透传逻辑改动 | `servers/api/Controllers/TrainingProxyController.cs` | `TrainingProxyControllerTests` 补用例（multipart 请求体含 boundary 透传 + 图片二进制响应透传 + 上游不可达 502） |
| 4.2 | 超时口径回归（`TrainingService:RequestTimeoutSeconds` 180s > `Image:RequestTimeoutSeconds` 120s 预算，不变） | tests | `TrainingProxyTimeoutConsistencyTests` 全绿 |

### Phase 5: 测试收口与文档同步

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 5.1 | 全量回归：`dotnet build` + podman 容器 UT（Mongo）；既有 `Training*`/`EmotionTask*`/`Model*`/`Synthesize*`/`TrainingProxy*` 测试不回退 | 全部 | 全绿 |
| 5.2 | 跑测口径：trx + `tests/Z.Vtuber.TestReport --check`（单用例 ≤ 5s，新增慢用例须登记 `TimeoutExceptions.json`） | 全部 | 通过 |
| 5.3 | 覆盖率：核心逻辑（能力 `Matches` 校验、类型/大小白名单判定、multipart 组装、`b64_json` 解析、错误映射）≥ 80% | 全部 | 达标 |
| 5.4 | `npm run codegen` 复核 0 diff（生成物勿手改） | `apps/web/src/api/` | 一致 |
| 5.5 | 文档同步：`servers/training/AGENTS.md`（API 面 +1 端点、`Ai/` 新增 `IImageModelClient` 族、`Configuration/` + `Image` 节、模块结构与上游依赖补 OpenAI-like 图片生成）、`servers/api/AGENTS.md`（代理 29 → 30 action）；PRD 验收项勾选、提案/Plan 互链 | 文档 | 与实现一致 |

## 4. 并行策略

- Phase 1 契约先行（1.1 → 1.2 → 1.3）；1.x 与 Phase 2 可并行（契约文件与 Ai 代码无冲突）。
- Phase 3 依赖 Phase 2（2.2/2.3 接口定稿）；3.1/3.2 独立可先行；3.3 依赖 3.1/3.2 与 2.2；3.4 依赖 3.3；3.5 随 3.3/3.4 收口。
- Phase 4 依赖 1.2 契约定稿与 3.4 路由（路径/状态码）。
- Phase 5 收口最后；5.5 文档同步在实现定稿后。

## 5. 验收对照

| PRD/提案目标 | 实现任务 | 验证 |
|--------------|---------|------|
| 功能点 1：multipart text+image 返回图片二进制；代理同口径可访问 | 1.1、3.4、4.1 | `TrainingImageGenerateApiTests`、`TrainingProxyControllerTests`；PRD 功能点 1 |
| 功能点 2：显式 modelId 404/400/能力校验；缺省指定→默认解析；无可用模型明确错误 | 3.2、3.3 | 3.3 测试（含 409 `no image model available`）；PRD 功能点 2 |
| 功能点 3：类型白名单/大小上限前置拦截，不发起上游调用 | 3.4 | 3.4 测试（`image/gif` 400、超限 400、零上游调用断言）；PRD 功能点 3 |
| 功能点 4：200 图片二进制；502 `model service unavailable`/`invalid model output`；Claude-like 400 | 2.3、2.4、3.4 | 2.3、2.4、3.4 测试；PRD 功能点 4 |
| 模型口径：输入 文本+图片 / 输出 图片（能力白名单零改动） | 3.2、3.3 | 能力校验用例；PRD §5.2 约束 |
| 契约先行 + api 镜像一致 | 1.1–1.3、4.1 | 5.4 codegen 0 diff；契约评审 |
| 超时口径：接口 120s、代理 180s 预算更高 | 3.1、4.2 | `TrainingProxyTimeoutConsistencyTests` |
| 回归不回退、覆盖率 ≥ 80%、单用例 ≤ 5s | 5.1–5.3 | 5.1–5.3 |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|----|------|------|
| 上游契约差异 | OpenAI-like 实现差异（不支持 `/images/edits`、返回 `url` 形态、字段缺失） | 本期 `url`/缺字段 → 502 `invalid model output`（提案 §6.1 决策）；假 `HttpMessageHandler` 契约测试锁定请求组装与解析；Endpoint 由 Providers 管理可换 |
| 同步长耗时 | 生成最长 120s 同步等待，占用连接 | `Image:RequestTimeoutSeconds` 超时兜底 502；代理 180s 预算更高；后台任务化留待后续（提案 §5.3） |
| 双层能力校验 | 编排层 `Matches` 显式校验 + 派发层 `ModelImageNotSupportedException` 兜底 | 文案同为 400 `model does not support image output`；测试覆盖两层（claude Provider 直构用例） |
| multipart 表单限额 | ASP.NET 默认 multipart 限额高于 `MaxImageBytes` | 业务层前置拦截（`IFormFile.Length` + 读取后复核），不依赖框架限额 |
| 部署顺序 | 先 training（新端点/客户端），再 api 代理 | 纯新增面，回滚旧版本即摘除能力、无残留风险（提案 §9.2） |
| 无新增依赖 | 不新增 NuGet/环境变量/流水线/引用（`ProjectReferenceGuardTests`、`WorkflowPathsGuardTests` 口径不变）；唯一新增配置为 `Image` 节（`ValidateOnStart`，缺省即默认值） | - |

## 7. 相关文档

- PRD：`docs/product/draft/20261001-training-image-generate-prd.md`
- 技术提案：`docs/engineering/proposals/20261001-proposal-training-image-generate.md`
- 关联 PRD/提案：`20261001-dataset-model-capability-prd.md`、`20261001-proposal-dataset-model-capability.md`（能力标注与功能点）、`20260929-training-audio-synthesize-prd.md`、`20260929-proposal-training-audio-synthesize.md`（生成接口 + 代理链路）
- 参考 Plan：`docs/engineering/plans/20261001-plan-dataset-model-capability.md`
- 模块规范：`servers/training/AGENTS.md`、`servers/api/AGENTS.md`、`servers/common/AGENTS.md`
- 规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`
- 契约：`contracts/training.openapi.yaml`、`contracts/bilibili-crawler.openapi.yaml`
