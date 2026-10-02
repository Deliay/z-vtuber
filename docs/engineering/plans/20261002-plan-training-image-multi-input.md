---
description: AI 训练「绘图接口支持多张图片输入」实现 Plan：POST /api/training/images/generate 的 image 字段由 [FromForm] IFormFile? 升级为 IFormFileCollection（同名 part 重复提交 1..N 张，0 字节 part 过滤后计数），校验顺序 text → 张数≥1（400 image is required）→ 张数 ≤ Image:MaxImageCount（新增，默认 10，400 too many images）→ 逐张类型白名单 → 逐张大小上限（双保险 Length 前置 + 读后复核），全部校验失败不发起模型调用；IImageModelClient.GenerateImageAsync 签名升级 IReadOnlyList<GenerateImageInput>，OpenAiLikeImageClient.BuildForm 单图仍发 image 字段（与现网逐字节一致零回归）、多图发 image[] 重复字段，文件名空则 image-{i}；ImageGenerateService 仅签名列表化（模型解析/错误口径零改动）+ 日志 imageCount/totalImageBytes；契约先行（training image 字段数组化 minItems 1 + 400 too many images、bilibili-crawler 镜像同步 + npm run codegen）、servers/api 代理零代码改动（同名多 part 透传天然支持，补透传用例）；TDD 补多图成功/超限/逐张校验失败/单图零回归/字段名分支用例，覆盖率 ≥ 80%
type: Permanent
---

# AI 训练 - 绘图接口支持多张图片输入 - 实现 Plan

**功能名称**: 训练 - 绘图接口支持多张图片输入（文本 + 多图片 → 图片）
**关联 PRD**: `docs/product/draft/20261002-training-image-multi-input-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20261002-proposal-training-image-multi-input.md`（v1.0）
**Plan 版本**: v1.0
**创建日期**: 2026-10-02
**作者**: Workflow Agent（代起草）
**需求总分支**: `prd/training-image-multi-input`（需求总 PR #174）
**feat-branch**: `feat/training-image-multi-input`（实现分支，基于需求总分支切出）

## 1. 概述

依据 PRD 与技术提案落地四件事：① `POST /api/training/images/generate` 的 `image` 输入由「恰好 1 张」放宽为「1..N 张」（multipart 同名字段重复提交，`IFormFileCollection` 绑定），新增张数上限 `Image:MaxImageCount`（默认 10，超限 400 `too many images`）并逐张做类型/大小校验；② `IImageModelClient`/`ImageGenerateService` 签名升级为图片列表（模型解析与错误口径零改动）；③ `OpenAiLikeImageClient.BuildForm` 按张数分支：单图仍发 `image` 字段（与现网逐字节一致、零回归），多图按 OpenAI 契约发 `image[]` 重复字段；④ 契约先行（`image` 字段数组化 + 新增 1 个 400 错误码 + api 镜像同步 + `npm run codegen`），`servers/api` 代理零代码改动。实施遵循 `docs/engineering/common-rules.md`（契约先行、TDD、覆盖率 ≥ 80%、单用例 ≤ 5s）、`backend-rules.md`、z-coding 流程与相关模块 `AGENTS.md`（`servers/training`、`servers/api`、`servers/common`、`apps/web`）。

### 1.0 现状核实结论（读码确认，均在需求总分支 @ `a9aac37` 复核）

- **inbound 消费点唯一**：`servers/training/Controllers/TrainingImageGenerateController.cs`——`Generate([FromForm] string? text, [FromForm] IFormFile? image, [FromForm] string? modelId, ct)`；校验顺序 text trim 非空（400 `text is required`）→ `image is null || image.Length == 0`（400 `image is required`）→ `IsSupportedImageType`（`SupportedImageTypes = ["image/png","image/jpeg","image/webp"]`，按 part `Content-Type` 分号参数归一后比对，不做魔数嗅探）→ `image.Length > options.MaxImageBytes`（400 `image too large`）→ 读后按实际字节复核（双保险）→ `NormalizeContentType` + `GenerateImageInput(bytes, mime, fileName)` 单值传编排；异常映射 10 个 catch：`NoImageModelAvailableException` 409、`ImageModelNotFoundException` 404、`ImageModelDisabledException`/`ImageModelInputNotSupportedException`/`ImageModelOutputNotSupportedException`/`ModelImageNotSupportedException` 400、`ModelOutputException` 502 `invalid model output`、`HttpRequestException` 与 `TaskCanceledException`（非客户端取消）502 `model service unavailable`。
- **客户端单图假设集中在两处**：`Ai/IImageModelClient.cs`——`GenerateImageAsync(provider, model, prompt, GenerateImageInput image, ct)`（`GenerateImageInput(Bytes, MimeType, FileName)`/`GeneratedImage(Bytes, MimeType)` 记录无需变更）；`Ai/OpenAiLikeImageClient.cs`——`BuildForm(model, prompt, image)` 挂 `model`/`prompt`/`response_format=b64_json` + **单个** `image`（`ByteArrayContent` + 原 `Content-Type`，文件名空则 `"image"`），`ParseB64` 认 `data[0].b64_json`；构造时 `HttpClient.Timeout = Image:RequestTimeoutSeconds`；`Ai/ImageModelClientFactory.cs` 按 `provider.Type` 派发（openai → OpenAI-like；claude 及未知 → `ModelImageNotSupportedException`），签名升级后**逻辑零改动**。
- **编排/模型选择零改动可复用**：`ImageGeneration/ImageGenerateService.cs`——`GenerateAsync(text, modelId, GenerateImageInput image, ct)` → `ResolveModelAsync`（显式 `ObjectId.TryParse`+`GetByIdAsync` 404 `model not found` → `Enabled` 400 `model disabled` → `TrainingModelCapabilities.Matches` 输入覆盖 `text,image`/输出含 `image`，否则 400 `model does not support image input`/`model does not support image output`；缺省 `FeaturePointIds.ImageGenerateDefinition` 经 `ITrainingModelResolver.ResolveAsync` 指定→默认，无则 `NoImageModelAvailableException` 409）与图片张数无关，仅透传列表；Info 日志现为 `modelSlug/imageBytes/elapsedMs`。
- **配置基建现成**：`Configuration/ImageOptions.cs`（节 `Image`，`[Range]` + 注册侧 `ValidateOnStart`）已有 `MaxImageBytes`（默认 10MiB）/`RequestTimeoutSeconds`（默认 120s），新增 `MaxImageCount` 同模式扩展即可，`Program.cs` 注册零改动。
- **契约现状**：`contracts/training.openapi.yaml` `/api/training/images/generate`（operationId `generateTrainingImage`，requestBody `$ref TrainingImageGenerateRequest`；200 `image/png` binary、400/404/409/502 统一 `$ref ErrorResponse`）；schema `TrainingImageGenerateRequest.required [text, image]`、`image: { type: string, format: binary }`（本期数组化）；`contracts/bilibili-crawler.openapi.yaml:1657` 同路径镜像（描述同步即可）。
- **api 代理零改动成立**（提案 ③ 成立）：`servers/api/Controllers/TrainingProxyController.cs` `[HttpPost("training/images/generate")]` → `ForwardAsync(HttpMethod.Post, "api/training/images/generate", ct)`（30 个镜像 action 之一）——POST 以 `StreamContent(Request.Body)` 原样透传并附加原 `Content-Type`（multipart boundary 保留，同名多 part 天然支持），响应透传状态码/`Content-Type` + 二进制流；`TrainingService:RequestTimeoutSeconds` 180s > `Image:RequestTimeoutSeconds` 120s 由 `TrainingProxyTimeoutConsistencyTests` 守护，本期不变。
- **测试设施可直接扩展**：`tests/Z.Vtuber.Tests/TrainingImageGenerateApiTests.cs`（`MongoDbFixture` + 控制器直构 + `StubImageClient`/`StubImageClientFactory`，覆盖全错误矩阵与「校验失败不发起上游调用」断言）、`OpenAiLikeImageClientTests.cs`（假 `HttpMessageHandler`：`BuildForm` 表单断言、`b64_json` 解析、工厂派发）、`TrainingProxyControllerTests.cs`（`GenerateTrainingImage_ForwardsPostMultipartBodyAndContentType`/`_UpstreamImageResponse_Passthrough`）。
- **codegen 与流水线**：`apps/web/package.json` `npm run codegen`（openapi-typescript）从 7 个契约生成 `src/api/schema*.d.ts`（勿手改，须复核 0 diff）；`.github/workflows/build-training-image.yml` paths 已含 `servers/training/**` + `servers/common/{core,training,transcript}/**`，本期无需调整；`ProjectReferenceGuardTests`/`WorkflowPathsGuardTests` 口径不变。

### 1.1 口径澄清（规划期裁定，随实现评审）

1. **单图零回归是红线**：`images.Count == 1` 时上游 multipart 仍只挂一个字段名恰为 `image` 的文件 part（文件名缺省仍为 `"image"`、`Content-Type` 处理与现状一致），`model`/`prompt`/`response_format=b64_json` 不变——客户端测试以「字段名恰为 `image`（非 `image[]`）」断言兜底；多图（`Count > 1`）才发 `image[]` 重复字段。
2. **张数口径**：计数 = **过滤 0 字节 part 后**的图片张数；≥1 否则 400 `image is required`（与现状 `image.Length == 0` 口径一致）；> `Image:MaxImageCount`（默认 10）→ 400 `too many images`（不发起模型调用）。张数上限判定先于逐张类型/大小校验（提案 §5.2 实现点 1 顺序）。
3. **0 字节 part 一律剔除**，不参与计数与后续校验（提案 §6.1 决策：过滤后计入「无图片」400）；混合提交（合法 + 0 字节）时 0 字节 part 视为杂散 part 忽略，按剩余张数走正常流程。
4. **逐张校验两趟、任一非法整体拒绝**：第一趟全量类型白名单（任一非法 → 400 `unsupported image type`），第二趟全量大小上限（任一超限 → 400 `image too large`），错误码按趟次决定、文案与单图一致；均在模型调用前完成。
5. **大小校验双保险沿用**：每张 `IFormFile.Length` 前置拦截 + 读取后按实际字节数复核；读取按提交顺序逐张封装 `GenerateImageInput`，**顺序保序透传**（无角色/权重语义，仅按提交顺序）。
6. **多图文件名兜底**：`FileName` 空 → `image-{i}`（i 为提交序号，仅多图分支）；单图分支保持现状 `"image"`，不改可观察行为。
7. **多图上游能力前提**：OpenAI-like `/images/edits` 须支持 `image[]` 多图；不支持的上游在 >1 张时按其自身报错落 502（`model service unavailable`/`invalid model output`），单图不受影响（提案 §6.2/§9.2）。
8. **超时与错误口径不变**：`Image:RequestTimeoutSeconds` 120s（`HttpClient.Timeout`）、代理 180s；模型选择（404/400/409/502）与 `{"error":"..."}` 错误体、`image/png` 成功响应形态零改动。
9. **本期无 web/UI 改动**；`npm run codegen` 仅随契约再生成 `apps/web/src/api/schema.d.ts`/`schema-training.d.ts`（勿手改）。

### 1.2 实施范围

- **做**：`contracts/`（`training.openapi.yaml` `TrainingImageGenerateRequest.image` 数组化 + `minItems: 1` + 400 描述 +`too many images`；`bilibili-crawler.openapi.yaml` 镜像描述同步；web codegen 生成物）；`servers/training`（`Controllers/TrainingImageGenerateController.cs` `IFormFileCollection` + 张数上限 + 逐张校验、`ImageGeneration/ImageGenerateService.cs` 签名列表化 + 日志字段、`Ai/IImageModelClient.cs` 签名列表化、`Ai/OpenAiLikeImageClient.cs` `BuildForm` 单图 `image`/多图 `image[]` 分支、`Configuration/ImageOptions.cs` +`MaxImageCount`）；`tests/Z.Vtuber.Tests`（3 个测试文件增改）；文档同步（`servers/training/AGENTS.md`、`servers/api/AGENTS.md`、PRD 验收勾选、提案/Plan 互链）。
- **不做**：模型能力口径与功能点 `image-generate` 语义、Providers/Models 管理、`servers/api` 代理代码（通用透传零改动）、上游模型服务、生成结果入库/缓存/批量生成、Web 绘图界面、每图独立角色/权重标注、`url` 形态结果回拉、Claude-like 图片输出、新增 NuGet/环境变量/流水线/除 `Image:MaxImageCount` 外的配置。

## 2. 分支与提交流程

- 需求总分支 `prd/training-image-multi-input`（基于 origin/main @ `df48853` 切出，PRD 与技术提案已随 `a9aac37` 提交，需求总 PR #174）：本 Plan 提交到该分支随 PR 评审。
- 实现分支 `feat/training-image-multi-input`（基于需求总分支切出，提案 §0 口径）：实现按 z-coding「编写实现」流程推进（子项目为单位委托 subagent、TDD 红-绿、测试与实现分开提交），实现 PR 回需求总分支评审。
- 提交顺序（Conventional Commits）：
  1. `docs(training):` 本实现 Plan（需求总分支）；
  2. `feat(contracts):` 契约变更（training + api 镜像）+ `npm run codegen`；
  3. `feat(training):` 客户端签名列表化 + `BuildForm` 字段名分支 + 编排 + 控制器多图校验 + `ImageOptions.MaxImageCount`（含 `test(training):` 配套）；
  4. `test(api):` 代理同名多 part multipart 透传用例（代理代码零改动）；
  5. `docs(training):` `AGENTS.md`（training/api）/ PRD 验收勾选 / 文档互链同步。

## 3. 实施阶段

### Phase 0: 技术文档（先行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | PRD（上游节点产出） | `docs/product/draft/20261002-training-image-multi-input-prd.md` | 已完成（`a9aac37`） |
| 0.2 | 技术提案（上游节点产出） | `docs/engineering/proposals/20261002-proposal-training-image-multi-input.md` | 已完成（`a9aac37`） |
| 0.3 | 实现 Plan（本文档） | `docs/engineering/plans/20261002-plan-training-image-multi-input.md` | 任务/文件/验收明确，与提案口径一致 |

### Phase 1: 契约先行

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | `TrainingImageGenerateRequest.image` 由单文件改文件数组（`type: array` + `minItems: 1` + `items: {type: string, format: binary}`），description 更新（1..N 张、同名重复提交、每张 ≤ `Image:MaxImageBytes`、张数 ≤ `Image:MaxImageCount` 默认 10）；端点 400 描述 +`too many images`；端点路径/operationId/200 与其余错误码全部不变 | `contracts/training.openapi.yaml` | 契约自洽，错误口径与提案 §3.1 逐字一致 |
| 1.2 | 镜像端点描述同步（`image` 多文件 + `too many images`；透传语义不变） | `contracts/bilibili-crawler.openapi.yaml` | 与 1.1 一致 |
| 1.3 | `npm run codegen` 再生成 `schema.d.ts`/`schema-training.d.ts`（勿手改） | `apps/web/src/api/` | 生成物与契约一致 |

### Phase 2: training Ai 图片客户端（TDD：每任务测试先行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | `IImageModelClient.GenerateImageAsync` 签名改 `IReadOnlyList<GenerateImageInput> images`（注释补「images 至少 1 张；顺序按提交顺序透传」）；`GenerateImageInput`/`GeneratedImage`/`IImageModelClientFactory` 不变 | `servers/training/Ai/IImageModelClient.cs` | 与提案 §3.2 签名一致 |
| 2.2 | `OpenAiLikeImageClient.BuildForm(model, prompt, images)`：`images.Count == 1` → 挂 `image` 字段（`ByteArrayContent` + 原 `Content-Type`、文件名缺省 `"image"`，与现状逐字节一致）；`images.Count > 1` → 逐张挂 `image[]` 重复字段（文件名取原 `FileName`，空则 `image-{i}`）；`model`/`prompt`/`response_format=b64_json` 与 `ParseB64`、超时/异常口径零改动 | `servers/training/Ai/OpenAiLikeImageClient.cs` | `OpenAiLikeImageClientTests` 转绿：单图字段名恰为 `image`（零回归断言）、多图 N 个 `image[]` part（顺序/文件名/Content-Type 断言） |
| 2.3 | `ImageModelClientFactory` 随签名编译通过，派发逻辑零改动 | `servers/training/Ai/ImageModelClientFactory.cs` | 既有派发用例不回退 |

### Phase 3: training 编排与端点（TDD：每任务测试先行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 3.1 | `ImageOptions` +`MaxImageCount`（默认 10，`[Range(1, int.MaxValue)]`）；`MaxImageBytes`/`RequestTimeoutSeconds` 不变 | `servers/training/Configuration/ImageOptions.cs` | 与既有属性同构；`Program.cs` 注册零改动 |
| 3.2 | `ImageGenerateService.GenerateAsync(text, modelId?, IReadOnlyList<GenerateImageInput> images, ct)`：`ResolveModelAsync` 零改动仅透传列表；Info 日志改 `training image-generate: modelSlug={} imageCount={} totalImageBytes={} elapsedMs={}`（不打文本/图片内容/apiKey） | `servers/training/ImageGeneration/ImageGenerateService.cs` | 既有模型选择用例（404/400/能力/409/默认首候选）不回退 |
| 3.3 | `TrainingImageGenerateController.Generate` 签名改 `[FromForm] IFormFileCollection? image`（字段名仍为 `image`，同名多 part 自动收集、单 part 兼容绑定）；校验顺序：text trim 非空（400 `text is required`）→ 过滤 `Length == 0` 后张数 ≥1（400 `image is required`）→ 张数 ≤ `options.MaxImageCount`（400 `too many images`）→ 第一趟逐张类型白名单（400 `unsupported image type`）→ 第二趟逐张大小（`Length` 前置 + 读后复核，400 `image too large`）→ 逐张封装 `GenerateImageInput` 列表（保序）→ `GenerateAsync`；异常映射表完全不变 | `servers/training/Controllers/TrainingImageGenerateController.cs` | `TrainingImageGenerateApiTests` 转绿（多图成功/超限/逐张校验失败 + 校验失败零上游调用断言） |
| 3.4 | DI/启动零改动复核（`ImageOptions` 同节扩展生效、启动幂等） | `servers/training/Program.cs` | 编译通过、既有注册不回退 |

### Phase 4: api 代理镜像（零代码改动）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 4.1 | 同名多 part multipart 请求体（含 boundary，2 个 `image` part）原样透传 + `image/png` 二进制响应透传用例 | `tests/Z.Vtuber.Tests/TrainingProxyControllerTests.cs` | 新用例转绿；`TrainingProxyController.cs` 零 diff |
| 4.2 | 超时口径回归（`TrainingService:RequestTimeoutSeconds` 180s > `Image:RequestTimeoutSeconds` 120s，不变） | tests | `TrainingProxyTimeoutConsistencyTests` 全绿 |

### Phase 5: 测试收口与文档同步

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 5.1 | 全量回归：`dotnet build` + podman 容器 UT（Mongo）；既有 `Training*`/`Image*`/`Model*`/`Synthesize*`/`TrainingProxy*` 测试不回退 | 全部 | 全绿 |
| 5.2 | 跑测口径：trx + `tests/Z.Vtuber.TestReport --check`（单用例 ≤ 5s，新增慢用例须登记 `TimeoutExceptions.json`） | 全部 | 通过 |
| 5.3 | 覆盖率：核心逻辑（张数上限判定、逐张类型/大小校验、multipart 字段名分支、列表透传）≥ 80% | 全部 | 达标 |
| 5.4 | `npm run codegen` 复核 0 diff（生成物勿手改） | `apps/web/src/api/` | 一致 |
| 5.5 | 文档同步：`servers/training/AGENTS.md`（`TrainingImageGenerateController` 多图校验顺序 + `MaxImageCount`、`IImageModelClient`/`OpenAiLikeImageClient` 列表签名与 `image`/`image[]` 分支、`ImageGeneration/` 签名、`Configuration/` `Image` 节 +`MaxImageCount`、API 面与配置小节同步）、`servers/api/AGENTS.md`（绘图镜像描述补「多 part 同名透传」）、PRD 验收项勾选、提案/Plan 互链 | 文档 | 与实现一致 |

## 4. 并行策略

- Phase 1 契约先行（1.1 → 1.2 → 1.3）；1.x 与 Phase 2 可并行（契约文件与 Ai 代码无冲突）。
- Phase 2 内部顺序 2.1 → 2.2（2.3 随签名编译）；Phase 3 依赖 2.1 接口定稿：3.1 可先行，3.2 依赖 2.1，3.3 依赖 3.1/3.2，3.4 随 3.3 收口。
- Phase 4 依赖 1.2 契约定稿（仅测试，无代码依赖）；4.2 可随时回归。
- Phase 5 收口最后；5.5 文档同步在实现定稿后。

## 5. 验收对照

| PRD/提案目标 | 实现任务 | 验证 |
|--------------|---------|------|
| 功能点 1：一次提交 ≥2 张合法图片可生成；单图行为与升级前完全一致；超限 400 `too many images`；未提交图片仍 400 `image is required` | 1.1、2.2、3.3 | `TrainingImageGenerateApiTests`（多图成功/11 张超限/0 张）、`OpenAiLikeImageClientTests`（单图字段名 `image` 零回归）；PRD 功能点 1 |
| 功能点 2：任一图片类型/大小非法 400，且不发起模型调用 | 3.3 | 逐张校验用例（第 2 张 `image/gif` 400、第 2 张超限 400、零上游调用断言）；PRD 功能点 2 |
| 功能点 3：模型选择与错误口径（404/400/409/502）、代理暴露不变 | 3.2、3.3、4.1 | 既有模型选择/失败用例多图下口径不变；`TrainingProxyControllerTests` 多 part 透传；PRD 功能点 3 |
| 边界：0 字节空文件按 `image is required` 口径拒绝；多图耗时超时沿用 120s/180s | 1.1、3.3、4.2 | 0 字节用例；`TrainingProxyTimeoutConsistencyTests`；PRD §3.5 |
| 单图零回归（上游请求形态逐字节一致） | 2.2 | 客户端字段名/文件名/`response_format` 断言；既有单图用例不回退 |
| 契约先行 + api 镜像一致 | 1.1–1.3、4.1 | 5.4 codegen 0 diff；契约评审 |
| 回归不回退、覆盖率 ≥ 80%、单用例 ≤ 5s | 5.1–5.3 | 5.1–5.3 |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|----|------|------|
| 上游不支持 `image[]` 多图 | OpenAI-like 实现差异，>1 张可能失败 | 单图分支保持现网形态零回归；多图失败落 502 可诊断（提案 §6.2/§9.2）；假 `HttpMessageHandler` 契约测试锁定字段名分支 |
| 单图零回归被破坏 | 恒发 `image[]` 会改变现网单图请求形态 | 按张数分支出最小差异；客户端测试断言「单图字段名恰为 `image`」兜底（提案 §6.1 决策） |
| 请求体随张数增长 | 默认上限 10 × 10MB | 张数上限 + 逐张大小上限双控（业务层前置拦截，不依赖框架 multipart 限额）；不做全量冗余拷贝 |
| 校验顺序/口径漂移 | 多图下错误定位更困难 | 校验顺序固定（text → 张数 → 逐张类型 → 逐张大小）、错误文案与单图逐字一致、失败前置零上游调用断言 |
| multipart 绑定差异 | `IFormFileCollection` 对单 part/同名多 part 的绑定 | ASP.NET 标准绑定；单图用例保持 + 多 part 用例覆盖（含 0 字节过滤） |
| 部署顺序 | 先 training（签名/校验），再 api 代理（零代码改动） | 无 DB 迁移、无配置强制变更（`MaxImageCount` 缺省即 10）；回滚旧版本即恢复单图行为，无残留风险 |
| 无新增依赖 | 不新增 NuGet/环境变量/流水线/项目引用（`ProjectReferenceGuardTests`、`WorkflowPathsGuardTests` 口径不变）；唯一配置增量为 `Image:MaxImageCount`（`ValidateOnStart`，缺省即默认值） | - |

## 7. 相关文档

- PRD：`docs/product/draft/20261002-training-image-multi-input-prd.md`
- 技术提案：`docs/engineering/proposals/20261002-proposal-training-image-multi-input.md`
- 关联 PRD/提案/Plan：`20261001-training-image-generate-prd.md`、`20261001-proposal-training-image-generate.md`、`20261002-plan-training-image-generate.md`（绘图接口初版）；`20261001-dataset-model-capability-prd.md`、`20261001-proposal-dataset-model-capability.md`（能力标注与功能点）
- 模块规范：`servers/training/AGENTS.md`、`servers/api/AGENTS.md`、`servers/common/AGENTS.md`、`apps/web/AGENTS.md`
- 规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`
- 契约：`contracts/training.openapi.yaml`、`contracts/bilibili-crawler.openapi.yaml`
