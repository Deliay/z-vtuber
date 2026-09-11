---
description: 通知服务接入 Milky 协议（替换 Satori）实现 Plan——基于 PRD v1.0 与技术提案 v1.0 的实施阶段划分、任务分解与验收对照；契约升级 v2.0.0 先行，common 订阅实体字段直接群维度化（旧需求未上线，无存量数据迁移），notifier 删除 Satori/ 并以 Milky.Net.Client 封装 Milky/（登录探活 + 群列表 + 群消息），配置 API 群维度化，前端「通知」模块适配，Aspire 参数切换，含全部测试改写
type: Permanent
---

# 通知服务接入 Milky 协议（替换 Satori）- 实现 Plan

**功能名称**: 通知服务接入 Milky 协议（替换 Satori）
**关联 PRD**: `docs/product/draft/20260911-notifier-milky-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20260911-proposal-notifier-milky.md`（v1.0）
**Plan 版本**: v1.1
**创建日期**: 2026-09-11
**更新日期**: 2026-09-11（v1.1：按下游打回意见修订——旧 Satori 通知需求未上线，无生产存量数据，取消全部数据迁移与兼容处理：订阅实体字段直接替换、唯一索引直接按新模型创建，不做旧索引检测删除与存量清空；上游提案第 4 节迁移策略相应不适用，以本 Plan 为准）
**作者**: Workflow Agent（代起草）
**feat-branch**: `feat/milky-notification`（需求总分支）

## 1. 概述

本 Plan 基于 PRD v1.0 与技术提案 v1.0 编写，指导「Satori → Milky」整体替换的编码实施。改动分五块：① 契约 `contracts/notifier.openapi.yaml` 升级 v2.0.0（`/channels` → `/groups`、订阅字段群维度化）；② common 订阅实体字段直接群维度化（`group_id/group_name`）+ 唯一索引直接按新模型创建（旧需求未上线，无存量数据，不做迁移）；③ notifier 删除 `Satori/`、新增 `Milky/`（`Milky.Net.Client` 封装，纯 HTTP API 面），配置 API 与投递服务群维度化，配置切换 `Z_VTUBER_MILKY_*`（fail-fast 不变）；④ `apps/web`「通知」模块群维度适配与契约代码生成；⑤ Aspire 参数切换与文档同步。crawler 事件发布链路零改动。实施全程遵循 `docs/engineering/common-rules.md`（TDD、契约式开发、核心逻辑覆盖率 ≥ 80%）、`backend-rules.md`、`frontend-rules.md` 及各子项目 AGENTS.md。

### 1.0 上游核实结论（代码现状复查）

对当前需求总分支 `feat/milky-notification` 代码核实，提案技术路径**整体可行**，几处现状细节在对应任务中给出处理：

- **notifier 现状与提案一致**：`SatoriApiClient`（`servers/notifier/Satori/SatoriApiClient.cs`）四端点 `LoginGetAsync`/`GetGuildListAsync`/`GetChannelListAsync`/`SendMessageAsync`（含登录发现缓存与 `EscapeXml`，:29/:77/:107/:143/:199）；`Program.cs:18-44` 显式映射 `Z_VTUBER_SATORI_ENDPOINT/TOKEN/PLATFORM/SELF_ID` 四个环境变量 + `ValidateOnStart`，`AddSingleton<ISatoriApiClient>` 工厂 + `AddHttpClient(nameof(SatoriApiClient))`；`NotifyConfigController.cs:65` 503 文案「Satori 不可达或登录发现失败」；`NotificationDispatchService` 依赖 `ISatoriApiClient`。删除面与替换点明确。
- **Milky.Net.Client 包可用性已核实**：NuGet 上最新稳定版 **3.0.0**（另有 4.0.0-preview.21），nuspec 含 `net10.0` target——与本项目 net10.0 完全兼容，提案「目标框架兼容性首先验证」风险已排除。**处理**：csproj 固定引用 `Milky.Net.Client` **3.0.0**（稳定版优先）；生成 API 的具体方法签名（分类属性形如 `milky.System.*`/`milky.Message.*`）以实现时 3.0.0 包为准，差异收敛在 `MilkyApiClient` 一个类内（见任务 2.2）。
- **项目未启用 `packages.lock.json`**：全仓库无 lock 文件、无 `Directory.Packages.props`，包版本以 csproj 内联精确版本管理（如 `MongoDB.Driver` 3.9.0）。**处理**：提案「锁版本（packages.lock.json）」按现状修正为「csproj 固定精确版本 3.0.0」，不新引入 lock 文件机制（见任务 2.2）。
- **launchSettings 与 Dockerfile 无 Satori 残留**：`servers/notifier/Properties/launchSettings.json` 仅含 `ASPNETCORE_ENVIRONMENT`；`servers/notifier/Dockerfile` 无 `Z_VTUBER_SATORI_*` 引用。**处理**：提案「launchSettings、Dockerfile 旧环境变量同步清理」实际无需改动，Phase 3 仅保留 AppHost 切换。
- **实体与索引现状**：`NotificationSubscriptionRecord`（`servers/common/Persistence/Entities/NotificationSubscriptionRecord.cs:11-29`）以 `[BsonElement]` 显式 snake_case 落库（`platform`/`guild_id`/`guild_name`/`channel_id`/`channel_name`）；`MongoDbInitializer.EnsureNotificationSubscriptionIndexesAsync`（`MongoDbInitializer.cs:708-716`）创建 `(event_type, platform, channel_id)` 唯一索引并挂入 `EnsureIndexesAsync`（:43）。**无迁移需求**：旧 Satori 通知需求未上线、无生产存量数据，实体字段直接替换、索引直接按新模型创建即可；开发环境数据库中的旧集合可直接 drop（见任务 2.1）。
- **测试设施现状**：`tests/Z.Vtuber.Tests` 已引用 notifier 项目（csproj 无需改动）；`MongoDbFixture` 集合清单已含 `notification_subscriptions`（无需改动）；`Fakes.cs:137` 有 `FakeSatoriApiClient` 待替换为 `FakeMilkyApiClient`；`SatoriApiClientTests.cs` 待删除并由 `MilkyApiClientTests.cs` 取代；`StartupDiTests.cs` 为 `ValidateOnStart` 宿主构建失败用例的既有先例。
- **前端现状**：`client.ts:134` `listNotifyChannels` → `/notify/channels`，类型来自 `schema-notify.d.ts`（codegen 生成）；`package.json:12` codegen 已含 notifier 契约命令（无需改脚本）；`NotificationPage.tsx` 与测试已存在，群维度化为字段级改造而非新建页面。
- **工程接入现状**：`infra/apphost/AppHost.cs:30-31` 现有 `satori-endpoint`/`satori-token` 两个 `AddParameter`（本需求改为 `milky-endpoint`/`milky-access-token`）；`.github/workflows/build-notifier-image.yml` 已存在且无需改动；slnx 已含 notifier（`servers/Z.Vtuber.slnx:7`）。
- **文档缺口**：`servers/notifier/AGENTS.md` 不存在（前序需求遗留未建），本次收口时按 api/crawler 格式新建（见任务 4.3）。

### 1.1 实施范围

- **做**：
  - 契约：`contracts/notifier.openapi.yaml` 升级 v2.0.0（先于实现）。
  - common：`NotificationSubscriptionRecord` 字段群维度化、`NotificationSubscriptionRepository` 查询/幂等键适配、`MongoDbInitializer` 唯一索引直接改为 `(event_type, group_id)`（无迁移逻辑）。
  - notifier：删除 `Satori/` 整目录；新增 `Milky/`（`MilkyOptions`/`IMilkyApiClient`/`MilkyApiClient`）；`Program.cs` 配置与注册切换；`NotifyConfigController` v2（`/groups` + 群维度 DTO）；`NotificationDispatchService` 投递适配。
  - web：codegen 重新生成 `schema-notify.d.ts`；`client.ts` `listNotifyGroups` 等适配；`NotificationPage.tsx` 群维度化。
  - 工程：`infra/apphost/AppHost.cs` 参数切换。
  - 测试：Satori 相关测试全部替换，单元/集成/前端测试覆盖达标且无回归。
- **不做**：Milky 事件流消费（SSE/WS）；私聊/@人/富媒体消息段；**存量订阅数据的任何迁移、清理与兼容处理**（旧需求未上线，无生产数据；上游 PRD/提案中「存量清理 + 索引重建迁移」相关表述随之不适用）；配置 API 鉴权；crawler 与 Redis 事件链路任何改动；`build-notifier-image.yml`、Dockerfile、launchSettings、slnx、codegen 脚本改动（均已核实无需变更）。

### 1.2 现状基线

- **接入封装先例**：`SatoriApiClient` 为「接口 + 单实现 + Singleton 工厂注册 + `IHttpClientFactory` 命名客户端」模式，`MilkyApiClient` 沿用同一形态（`MilkyClient` 在工厂内构造，对外不泄露生成模型）。
- **索引初始化先例**：`MongoDbInitializer` 唯一索引写法参照既有 `EnsureNotificationSubscriptionIndexesAsync`（`MongoDbInitializer.cs:708-716`），本次仅替换索引键定义，初始化路径不变。
- **契约与前端先例**：契约 v1.0.0（`contracts/notifier.openapi.yaml`）的 4 端点结构沿用，仅 `/channels` → `/groups` 与字段群维度化；`NotificationPage` 的列表/弹窗/两阶段删除/错误重试交互全部保留，仅数据字段与下拉来源变更。
- **配置先例**：`Program.cs` 的「显式环境变量映射 + `Validate(非空).ValidateOnStart()`」fail-fast 形态原样平移到 `MilkyOptions`。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash）与 Conventional Commits：

- 本需求开发全程只使用需求总分支 `feat/milky-notification`：契约、实现、测试、文档全部直接在该分支提交，不切开发子分支（沿用近两期需求的既定做法；提案头部「实现分支另行切出」以本 Plan 的既定做法为准）。
- 需求总分支已推送 origin，需求总 PR 已存在：`https://github.com/Deliay/z-vtuber/pull/76`，提交照常推送更新分支。
- 提交信息使用 Conventional Commits：契约 `feat!:`（不兼容）、测试先行 `test:`、实现 `feat!:`、工程接入 `chore:`、文档 `docs:`；测试与实现分开提交。
- 并行 subagent 按子项目划分（common+notifier / apps-web），每个 subagent 在自己完成后于需求总分支提交；共享文件（`Fakes.cs` 等测试设施）归入主串行线先行，避免并行冲突（见第 4 节）。

## 3. 实施阶段

按「契约先行 → TDD 红-绿 → 工程接入 → 收口验证」推进。Phase 0 串行先行；Phase 1 测试先行（设施 1.1 先行，其余按子项目并行）；Phase 2 common 先行串行，notifier 与 web 随后并行；Phase 3 与实现可部分并行；Phase 4 串行收口。

### Phase 0: 契约先行

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | 升级 `contracts/notifier.openapi.yaml` 至 **2.0.0**：① `GET /api/notify/channels` → `GET /api/notify/groups`（operationId `listNotifyChannels` → `listNotifyGroups`），响应元素 `{groupId, groupName?}`（groupId string），Milky 不可达/未登录返回 503 + 明确消息；② 订阅 schema 字段 `platform/guildId/guildName/channelId/channelName` → `groupId/groupName`，订阅记录含 `id/eventType/groupId/groupName?/createdAt`；③ 创建请求 `eventType` 枚举 `["cookie_invalid"]` 不变，`groupId` 必填；④ 错误响应 schema（400 非法 eventType/非法 groupId / 404 / 503）与提案 3.1 逐字段一致 | `contracts/notifier.openapi.yaml` | `npx openapi-typescript` 可成功生成类型；与提案 3.1 逐字段一致 |

### Phase 1: 测试先行（红）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | 测试设施替换（串行先行）：`Fakes.cs` 删除 `FakeSatoriApiClient`、新增 `FakeMilkyApiClient`（记录 `SendGroupMessageAsync` 的 groupId 与文案、可注入 `GetGroupListAsync` 结果与失败）；stub `HttpMessageHandler` 辅助沿用既有模式。csproj 与 `MongoDbFixture` 无需改动（1.0 已核实） | `tests/Z.Vtuber.Tests/Fakes.cs` | 编译通过（引用点随后续任务切换） |
| 1.2 | 新建 `MilkyApiClientTests`（stub `HttpMessageHandler`），同时删除 `SatoriApiClientTests.cs`：① 三端点请求方法与路径正确（POST `/api/get_login_info`、`/api/get_group_list`、`/api/send_group_message`）；② `Authorization: Bearer {token}` 头（token 为空则不携带）；③ `send_group_message` 请求体 snake_case：`group_id` 为 int64、`message` 为单元素 text 消息段数组、**无 XML 转义**；④ `get_group_list` 响应映射为群号 + 群名列表；⑤ 错误映射：`get_login_info` 失败/非成功结果 → 明确异常（上层转 503），5xx/网络异常抛出由调用方处理 | `tests/Z.Vtuber.Tests/MilkyApiClientTests.cs`（新）、`tests/Z.Vtuber.Tests/SatoriApiClientTests.cs`（删） | 红 |
| 1.3 | `NotificationSubscriptionRepositoryTests` 改写：① 增删查（群维度字段）；② 按 `event_type` 过滤查询不变；③ `(event_type, group_id)` 唯一索引生效（重复插入抛 `MongoWriteException`）；④ `group_id`/`group_name` 以 snake_case 落库（抽查 BSON）；旧字段断言全部移除 | `tests/Z.Vtuber.Tests/NotificationSubscriptionRepositoryTests.cs` | 红 |
| 1.4 | `MongoDbInitializerTests` 增补：初始化后 `notification_subscriptions` 上存在唯一索引 `(event_type, group_id)`，重复执行幂等安全（无旧索引删除/存量清空用例——无迁移逻辑） | `tests/Z.Vtuber.Tests/MongoDbInitializerTests.cs` | 红 |
| 1.5 | `NotifyConfigControllerTests` 改写：① 订阅增删查、删除不存在 404；② 非法 `eventType` 400、非法 `groupId`（不可解析为 int64）400；③ `(eventType, groupId)` 重复创建幂等返回已有；④ `GET /groups` 返回 fake 群列表（groupId/groupName）；⑤ Milky 不可达或未登录时 503 + 明确错误消息（不静默空列表）；⑥ 缺 `Z_VTUBER_MILKY_ENDPOINT` 时宿主构建因 `ValidateOnStart` 失败（参照 `StartupDiTests` 先例） | `tests/Z.Vtuber.Tests/NotifyConfigControllerTests.cs` | 红 |
| 1.6 | `NotificationDispatchServiceTests` 改写：① 无订阅时忽略不发送；② 多订阅逐群发送（`FakeMilkyApiClient` 记录 groupId 与文案）；③ 单条发送失败不影响其余订阅；④ 文案不变（含昵称/MID/时间，不含敏感信息，账号信息缺失降级 CookieId 尾段）；⑤ Milky 调用异常时跳过该条记日志 | `tests/Z.Vtuber.Tests/NotificationDispatchServiceTests.cs` | 红 |
| 1.7 | 前端测试改写：① `client.test.ts`：`listNotifyChannels` → `listNotifyGroups`（路径 `/notify/groups`）、创建绑定请求体群维度化；② `NotificationPage.test.tsx`：绑定列表目标群列（群名/群号）渲染与空态、新增绑定弹窗群下拉加载（label `{groupName ?? groupId}（{groupId}）`）/错误重试/提交刷新、重复绑定提示、删除两阶段确认 | `apps/web/src/api/client.test.ts`、`apps/web/src/pages/notification/NotificationPage.test.tsx` | `npm run test` 红 |

注：`CookieInvalidEventSubscriberTests`（Redis 泵）与全部 crawler 侧测试**不改**——事件链路零改动。

### Phase 2: 实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | common 改造（串行先行）：① `NotificationSubscriptionRecord` 删除 `Platform`/`GuildId`/`GuildName`/`ChannelId`/`ChannelName`，新增 `[BsonElement("group_id")] GroupId` 与 `[BsonElement("group_name")] GroupName?`；② `NotificationSubscriptionRepository` 幂等查询改为 `(event_type, group_id)`；③ `MongoDbInitializer.EnsureNotificationSubscriptionIndexesAsync` 的索引键直接改为 `(event_type, group_id)` 唯一索引，其余初始化逻辑不变——**不做旧索引检测删除、不清空存量**（旧需求未上线，无生产数据；开发环境数据库直接 drop `notification_subscriptions` 集合即可） | `servers/common/Persistence/Entities/NotificationSubscriptionRecord.cs`、`servers/common/Persistence/NotificationSubscriptionRepository.cs`、`servers/common/Persistence/MongoDbInitializer.cs` | 1.3/1.4 转绿；既有测试无回归 |
| 2.2 | notifier Milky 接入：① `Z.Vtuber.Notifier.csproj` 新增 `PackageReference Milky.Net.Client Version="3.0.0"`（固定精确版本，1.0 已核实 net10.0 兼容）；② 新增 `Milky/MilkyOptions.cs`（`Endpoint`/`AccessToken`）、`Milky/IMilkyApiClient.cs`（`GetLoginInfoAsync`/`GetGroupListAsync`/`SendGroupMessageAsync(long groupId, string text, CancellationToken)`，群实体等对外模型自定义、不泄露生成类型）、`Milky/MilkyApiClient.cs`（内部构造 `Milky.Net.Client.MilkyClient`，`HttpClient` 预配置 `BaseAddress` + `Authorization: Bearer`；不调用 SSE/WS 事件接收）；③ `Program.cs`：`SatoriOptions` 注册替换为 `MilkyOptions`（显式映射 `Z_VTUBER_MILKY_ENDPOINT`/`Z_VTUBER_MILKY_ACCESS_TOKEN`，endpoint 非空 `ValidateOnStart`），`ISatoriApiClient` 工厂注册替换为 `IMilkyApiClient` 同形态 Singleton 工厂 | `servers/notifier/Z.Vtuber.Notifier.csproj`、`servers/notifier/Milky/*`（新）、`servers/notifier/Program.cs` | 1.2 转绿；1.5⑥ 转绿 |
| 2.3 | 配置 API v2：`NotifyConfigController` 的 `GetChannels` 改为 `GetGroups`（路由 `/api/notify/groups`）：先 `GetLoginInfoAsync` 探活（失败 503「Milky 不可达或机器人未登录」）再 `GetGroupListAsync` 单次返回全量群列表（替代原 guild 循环 + channel 翻页两段聚合）；订阅增删查 DTO 字段群维度化；创建时校验 `eventType == "cookie_invalid"`（否则 400）与 `groupId` 可解析为 int64（否则 400），`(eventType, groupId)` 重复按幂等返回已有 | `servers/notifier/Controllers/NotifyConfigController.cs` | 1.5①-⑤ 转绿 |
| 2.4 | 投递适配：`NotificationDispatchService` 依赖换为 `IMilkyApiClient`，循环内 `SendGroupMessageAsync(long.Parse(subscription.GroupId), 文案, ct)`；单条失败仅记日志继续下一条；`FormatMessage` 文案不变 | `servers/notifier/Dispatch/NotificationDispatchService.cs` | 1.6 转绿 |
| 2.5 | 删除 Satori 实现：`servers/notifier/Satori/` 整目录删除（含 `EscapeXml` 与登录发现逻辑），全仓库 `Satori`/`Z_VTUBER_SATORI_` 引用清零（以 grep 验证） | `servers/notifier/Satori/`（删） | 2.2-2.4 完成后编译通过；grep 无残留 |
| 2.6 | 前端适配（与 2.2-2.5 并行，仅依赖 Phase 0 契约）：① `npm run codegen` 重新生成 `schema-notify.d.ts`（自动生成勿手改）；② `client.ts`：`listNotifyChannels` → `listNotifyGroups`（`/notify/groups`），创建绑定请求体 `groupId/groupName`；③ `NotificationPage.tsx`：绑定列表列「平台/群组/频道」→「目标群」（群名/群号），弹窗下拉选项 label `{groupName ?? groupId}（{groupId}）`、value 携带 groupId/groupName；错误态/重试/两阶段删除等交互不变 | `apps/web/src/api/schema-notify.d.ts`（生成）、`apps/web/src/api/client.ts`、`apps/web/src/pages/notification/NotificationPage.tsx` | 1.7 转绿；`npm run lint`/`npm run build` 通过 |

### Phase 3: 工程接入

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 3.1 | Aspire 参数切换：`AppHost.cs` notifier 编排的 `satori-endpoint`/`satori-token` 两个 `AddParameter` 与 `WithEnvironment` 替换为 `milky-endpoint`（→ `Z_VTUBER_MILKY_ENDPOINT`）/`milky-access-token`（→ `Z_VTUBER_MILKY_ACCESS_TOKEN`），其余编排（mongo/messaging 引用、健康检查、web `VITE_NOTIFY_URL`）不变 | `infra/apphost/AppHost.cs` | Aspire 编排可一键启动 notifier 与既有服务 |

注：Dockerfile、launchSettings、slnx、`build-notifier-image.yml`、codegen 脚本经 1.0 核实均无需改动。

### Phase 4: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 4.1 | `dotnet test tests/Z.Vtuber.Tests` 全绿（含 crawler 侧既有回归）；`MilkyApiClient`/配置 API/投递/仓储核心路径覆盖率 ≥ 80%；`npm run test` / `npm run lint` / `npm run build` 全绿；grep 确认 `Satori`/`Z_VTUBER_SATORI_` 零残留（代码、配置、测试） | 全部通过 |
| 4.2 | 编排手工验证（z-aspire-orchestrator）：本地 Aspire 拉起全量服务 → 缺 `Z_VTUBER_MILKY_ENDPOINT` 时 notifier 启动失败且日志明确 → 注入可用 Milky 后经 web「通知」模块查询 QQ 群、新增绑定 → 将测试 cookie 置失效，观察目标 QQ 群秒级收到通知（文案不变，无敏感信息）→ 无订阅时静默 → 删除绑定二次确认生效 → 重启后订阅仍在 | 手工验证记录 |
| 4.3 | AGENTS.md 同步：新建 `servers/notifier/AGENTS.md`（技术栈/模块结构——`Milky/` 封装与 `Milky.Net.Client` 3.0.0 依赖、事件泵与投递、配置 API v2/裸部署配置——`Z_VTUBER_MILKY_*` 配置表/开发流程，对齐 api/crawler 格式，补齐前序需求缺口）；`servers/common/AGENTS.md`（`Persistence/` 订阅实体字段与索引变更说明更新）；`apps/web/AGENTS.md`（「通知」模块群维度说明更新） | 文档与实现一致 |

## 4. 并行策略

- Phase 0（契约）串行先行——前端 codegen 与后端控制器均依赖契约。
- Phase 1（测试）：1.1（`Fakes.cs` 替换）串行先行；随后 1.2-1.6（后端测试）与 1.7（前端测试）互不依赖，可拆 subagent 并行编写。
- Phase 2：2.1（common）串行先行——2.2-2.5（notifier）依赖 common 实体与索引；2.1 完成后 notifier 线与 web 线（2.6）分属不同子项目，按 z-coding 规范拆 subagent 并行实现，各 subagent 负责「代码实现 → 提交 → 单元测试修复 → 提交」闭环。
- Phase 3（3.1 AppHost）与 Phase 2 互不依赖可并行。
- Phase 4 串行收口。全部提交在需求总分支 `feat/milky-notification`。

## 5. 验收对照

| PRD 功能点 | 实现任务 | 验证方式 |
|-----------|---------|---------|
| 功能点 1：通知服务接入 Milky（endpoint 注入 fail-fast、Satori 零残留、Aspire） | 2.2、2.5、3.1 | 1.2、1.5⑥ + 编排验证（4.2）+ grep 零残留（4.1） |
| 功能点 2：可用 QQ 群查询（群号/群名，不可用明确报错） | 0.1、2.2、2.3 | 1.2①④⑤、1.5④⑤ + 编排验证（4.2） |
| 功能点 3：通知订阅绑定（群维度增删查、幂等、持久化） | 0.1、2.1、2.3 | 1.3、1.4、1.5①-③ + 重启后订阅仍在（4.2） |
| 功能点 4：Cookie 失效通知投递到 QQ 群（多群、无订阅静默、单条失败隔离、文案不变、无敏感信息） | 2.4 | 1.6 + 失效 cookie 端到端验证（4.2） |
| 功能点 5：后台「通知」模块群维度适配（列表/弹窗下拉/删除确认/错误态） | 2.6 | 1.7 + 界面手工验证（4.2） |
| 非功能：crawler 与 Redis 事件链路不变 | —（零改动） | 既有测试全绿（4.1） |
| 非功能：核心逻辑覆盖率 ≥ 80%、全量测试绿 | 4.1 | `dotnet test` / `npm run test` |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| Milky.Net 生成 API 签名差异 | 包为代码生成，分类属性与方法签名随 Milky IR 演进（提案 5.2 实现点 1） | 固定 3.0.0（1.0 已核实 net10.0 兼容）；签名差异收敛在 `MilkyApiClient` 一个类内；1.2 用例以 stub handler 锁定 HTTP 面行为，与生成模型解耦 |
| 契约破坏式变更 | 字段与路由同时变更，前端需一次性改齐（提案 5.2 实现点 5） | Phase 0 契约先行；2.6 一次性改齐 `client.ts`/页面/测试；4.1 grep 与全量测试兜底 |
| Milky 服务器可用性 | 外部依赖，本地验证需真实实例且机器人已入群 | 单元/集成测试以 stub `HttpMessageHandler` 与 `FakeMilkyApiClient` 覆盖（1.2/1.5/1.6）；手工验证依赖可用 Milky（4.2） |
| 部署环境变量未同步 | 生产部署清单（仓库外）需从 `Z_VTUBER_SATORI_*` 切换为 `Z_VTUBER_MILKY_*` | PRD 不兼容声明 + PR #76 描述显式列出；缺失时按预期 fail-fast 启动失败并给出明确提示 |
| 依赖 | `Milky.Net.Client` 3.0.0（NuGet，仅 notifier 引用，csproj 固定精确版本）；Milky 协议 `get_login_info`/`get_group_list`/`send_group_message`；既有 Redis 事件链路与仓储模式；Testcontainers mongo/redis 夹具 | 已核实（1.0） |

## 7. 相关文档

- PRD：`docs/product/draft/20260911-notifier-milky-prd.md`（v1.0）
- 技术提案：`docs/engineering/proposals/20260911-proposal-notifier-milky.md`（v1.0）
- 契约：`contracts/notifier.openapi.yaml`（Phase 0 升级为 v2.0.0）
- 前序 Plan（通知服务首版）：[[20260905-plan-cookie-invalid-notification]]
- Milky.Net: https://github.com/frg2089/Milky.Net （NuGet `Milky.Net.Client` 3.0.0，含 net10.0 target）
- Milky 协议: https://milky.ntqqrev.org/ （系统 API / 消息 API / 群聊 API）
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`docs/engineering/frontend-rules.md`、`servers/common/AGENTS.md`、`apps/web/AGENTS.md`
