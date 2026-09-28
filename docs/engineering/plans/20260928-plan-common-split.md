---
description: Common 公共模型按领域拆分实现 Plan——基于 PRD v1.0 与技术提案 v1.0 的任务分解：Z.Vtuber.Common 拆为 Core + 7 个领域项目（纯搬运）、MongoDbInitializer/集合名常量按域切分、8 服务按矩阵收窄引用与启动初始化、Dockerfile 与 8 条镜像流水线 paths 与真实依赖对齐；TDD 守护测试先行（引用矩阵/流水线 paths/常量等价/按域初始化），含上游核实结论与 4 处偏差收敛（crawler 死注册删除、契约命名空间迁移面、LiveMongoSetup 方法数、既有巨测调用点改指）
type: Permanent
---

# Common 公共模型按领域拆分 - 实现 Plan

**功能名称**: Common 公共模型按领域拆分
**关联 PRD**: `docs/product/draft/20260928-common-split-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20260928-proposal-common-split.md`（v1.0）
**Plan 版本**: v1.0
**创建日期**: 2026-09-28
**作者**: Workflow Agent（代起草）
**feat-branch**: `feat/common-split`（需求总分支，已推送 origin；需求总 PR：<https://github.com/Deliay/z-vtuber/pull/140>）

## 1. 概述

本 Plan 基于 PRD v1.0 与技术提案 v1.0 编写，指导「Common 公共模型按领域拆分」的编码实施。这是一次**纯结构性拆分**：`servers/common`（`Z.Vtuber.Common.csproj`，1161 行 `MongoDbInitializer` + 25 集合名常量 + 全部实体/仓储/共享服务）拆为 **共享内核 `Z.Vtuber.Common.Core` + 7 个领域项目**（Live/Cookie/Weibo/Recording/Transcript/Training/Notification，星型依赖 Core、域间零引用）；8 个服务的 `*.csproj` 按提案 §2.3 引用矩阵收窄、`Program.cs` 只初始化自己引用领域的索引/迁移（保持原调用顺序）；8 个 Dockerfile 的 csproj 拷贝行与 8 条镜像流水线 `paths` 与真实依赖对齐（**改 `servers/common/transcript/**` 只触发 transcript-api / transcript-consumer / training 3 条，cookie/crawler 不触发**）。对外 API 契约、MongoDB 集合/索引/文档结构、既有数据零变化，`contracts/`、`apps/web/`、`infra/apphost/` 零改动。实施全程遵循 `docs/engineering/common-rules.md`（TDD、核心逻辑覆盖率 ≥ 80%）、`backend-rules.md`、z-coding 开发流程与各服务 `AGENTS.md`。

### 1.0 上游核实结论（代码现状复查）与偏差

对需求总分支 `feat/common-split` 代码逐项复查，提案技术路径**可行**，现状描述绝大多数属实（`MongoDbInitializer.cs` 1161 行/25 常量/19 个 `Ensure*`/5 个 `Backfill*`/2 公开 `Migrate*`+2 订阅索引遗留方法/1 个 `Cleanup*`/尾部 `MetaRecord`；8 条 `build-*-image.yml` `paths` 均含 `servers/common/**`、`build-web-image.yml` 仅 `apps/web/**`；8 个服务 `Program.cs` 均 `AddSingleton<MongoDbInitializer>()` 且启动序列与提案 §3.2 行号一致；8 个 Dockerfile 均 `COPY servers/common/Z.Vtuber.Common.csproj` + `COPY servers/common/ servers/common/`；slnx/tests csproj/apphost csproj 与提案 §7 一致）。引用矩阵逐格以「类型名逐服务 grep」复核：api→Transcript、notifier→Live 等疑似命中均为误报（代理控制器方法名、`NotificationSubscriptionRecord` 子串），矩阵本身正确。下列要点在对应任务中直接使用：

- **契约类型现状**：10 个跨服务协议类型现分布于 `Z.Vtuber.Common.Live`（`Live/` 目录，含装错归属的 `RecordingEventChannels`/`CookieInvalidEvent` 等）与 `Z.Vtuber.Common.Services`（`NoAvailableCookieException` 定义在 `Services/CookiePoolService.cs`）；`SubscriptionType` 被 `SubscriptionEvent`（上移 Core）与 recording/api/crawler 多方使用，上移 Core 合理；
- **过期描述确认**：`ICookieNavProbeService`/`CookieProbeOutcome` 在代码中已不存在（仅 `servers/common/AGENTS.md`、`docs/engineering/backend-rules.md` 残留）；`ICookiePoolService.NextAsync` 已删除，但 `tests/Z.Vtuber.Tests/Fakes.cs` 三处 fake 仍留 `NextAsync` 死方法（顺带清理）；
- **`MongoCollectionNames.Meta` 的服务侧消费**：`servers/recording/Recording/RecordingBackfillMigration.cs`、`servers/recording-transcript-consumer/Services/TranscriptModelMigration.cs` 直接以 `MetaRecord` + `MongoCollectionNames.Meta` 自管迁移 marker，拆分后改用 `CoreCollectionNames.Meta`（`MetaRecord`/`MigrationMarker` 归 Core，公共 API 覆盖此二处既有语义即可，勿改写其并发逻辑）；
- **既有测试面**：`MongoDbInitializerTests`（804 行/44 用例）之外另有 6 个测试文件引用 `MongoDbInitializer`（`MongoDbFixture`/`RecordingStatusRepositoryTests`/`SubscriptionTypeSplitMigrationTests`/`CookieInvalidEventTypeSplitMigrationTests`/`LiveEventPayloadBackfillTests`/`LiveEventSenderBackfillTests`），`MongoDbFixture.ResetAsync` 的清理清单用 25 个 `MongoCollectionNames` 常量。

**偏差与提案遗漏（4 处，均在本 Plan 收敛）**：

1. **crawler 的 Cookie 域残留死注册（偏差关键项）**：提案 §1.1④ 称 crawler「对 Cookie 域的唯一使用是捕获 `NoAvailableCookieException`」，漏了 `servers/crawler/Program.cs:51` 的 `AddBackstageDataAggregation()`——该扩展（Cookie 域 `BackstageDataAggregationExtensions`）注册 `ICookieAccountInfoRepository`，但 crawler 内**无任何解析点**（`AccountInfoCrawler` 用的是 Live 域 `AccountBasicInfoRepository`/`LiveSessionRepository`），属无人消费的死注册。若照提案矩阵改造，crawler 编译即失败。本 Plan 收敛方式：**拆分时删除该死注册**（连带修正 `BackstageDataAggregationExtensions` 注释里已过期的 `CookieNavProbeService` 描述），提案 §2.3 矩阵（crawler = Core+Live+Weibo）与 §9.2 paths 清单原样成立，PRD 场景「改 Cookie 模型不牵连抓取」成立。这是本期唯一非纯搬运改动（另一处为初始化范围差异）。**回退点**：若评审要求保留注册，则 crawler 增引 Cookie 项目、`build-crawler-image.yml` paths 增 `servers/common/cookie/**`（cookie 域变更触发达 3 条），与 PRD 场景表「抓取不受 Cookie 模型影响」冲突，需产品确认。
2. **契约类型命名空间迁移面被低估**：提案「同步修正约 10 处消费方 using」偏小——10 个契约类型的引用文件在 servers/tests 实际命中约 70 个（`NoAvailableCookieException|CookieInvalidEvent|RecordingStartedEvent|RecordingFinishedEvent|TranscriptSegmentEvent|SubscriptionEvent|*EventChannels`），每文件机械补一行 `using Z.Vtuber.Common.Contracts;` 即可（以编译错误清零为准，见 2.1）。**回退点**：保留原命名空间（纯程序集搬移、零 using 改动），仅失去命名空间归位收益。
3. **`LiveMongoSetup` 方法数**：提案 §3.2 记「8 个 `Ensure*`」，实际 Live 域为 **9 个方法/8 组集合**（`live_guards` 有 `EnsureLiveGuardIndexesAsync` + `EnsureLiveGuardRoomDateIndexAsync`）；搬运按提案 §3.2 方法清单逐个执行，不按集合数裁剪。
4. **既有巨测的适配方式**：`MongoDbInitializer` 拆基座后，`MongoDbInitializerTests` 及上述 6 个测试文件原调用点全部失效。提案 §5.3 已明确巨测「不彻底肢解」，本 Plan 明确做法：**文件与 44 个用例全部保留，仅把调用目标从 `MongoDbInitializer` 改指对应 `XxxMongoSetup`**（含 `EnsureIndexes*`/`Backfill*`/`Migrate*`/`Cleanup*` 各用例），再按 §8.2 补齐按域测试（见 Phase 1）。

### 1.1 实施范围

- **做**：
  - `servers/common/`：拆为 `core/`+`live/`+`cookie/`+`weibo/`+`recording/`+`transcript/`+`training/`+`notification/` 8 个项目（csproj 配置与现 `Z.Vtuber.Common.csproj` 同：net10.0 + `FrameworkReference Microsoft.AspNetCore.App` + `MongoDB.Driver 3.9.0`），按提案 §2.3 归属表搬运全部类型；`MongoCollectionNames` 拆为 `<域>CollectionNames`（值逐字节不变）+ `CoreCollectionNames.Meta`；`MongoDbInitializer` 拆为 Core 基座（`DatabaseName`/`MigrationMarker`/`DropIndexIfExistsAsync`）+ 7 个 `XxxMongoSetup`（按提案 §3.2 内部接口表逐字平移）；契约 10 类型入 `Z.Vtuber.Common.Contracts` 命名空间（偏差 2）；每项目复制 `AssemblyInfo.cs`（`InternalsVisibleTo("Z.Vtuber.Tests")`）与 `GlobalUsings.cs`。
  - 8 个服务：`*.csproj` ProjectReference 按矩阵收窄；`Program.cs` 初始化面按提案 §3.2 调用表替换（同序）；删除 crawler 死注册（偏差 1）；`MetaRecord` 两处服务侧消费改 `CoreCollectionNames.Meta`。
  - 构建面：8 个 Dockerfile csproj 拷贝行按引用收窄；8 条 `build-*-image.yml` `paths` 按提案 §9.2 清单收窄（`build-web-image.yml` 不动）；`servers/Z.Vtuber.slnx` 换 8 个项目条目。
  - 测试面：`tests/Z.Vtuber.Tests` csproj 显式补 8 个领域项目引用；`MongoDbFixture` 清单改各域常量；巨测与 6 个引用文件调用点改指（偏差 4）；新增守护测试（引用矩阵/流水线 paths/常量等价）与 8 个按域初始化测试。
  - 文档面：重写 `servers/common/AGENTS.md`；同步 `docs/engineering/backend-rules.md` 与 8 个服务 `AGENTS.md`（路径与引用图）。
- **不做**（提案 §5.3）：命名空间全量对齐 `Z.Vtuber.Common.<域>.*`；`BackfillLiveEventTextIndexAsync`/`BackfillLiveEventSenderAsync` 近重复合并；`MongoDbInitializerTests` 彻底肢解；Training/Notification 下沉到服务内部；任何索引优化、集合结构调整、业务功能变更；`contracts/`、`apps/web/`、`infra/apphost/`、MongoDB 数据零改动零迁移。

### 1.2 现状基线（范本）

- 类库范本：`servers/common/Z.Vtuber.Common.csproj`（csproj 配置逐字段复制到 8 个新项目）。
- 搬运范本：`MongoDbInitializer.EnsureTranscriptIndexesAsync`（幂等 `CreateOneAsync`，拆 Recording/Transcript 两域的样板）、`EnsureWeiboCookieIndexesAsync`（跨集合复用文档模型、归 Cookie 域的样板）。
- 服务装订范本：`servers/api/Program.cs:68-85`（迁移→索引→回填的既有顺序，拆分后逐段换成 `XxxMongoSetup` 同序调用）。
- 测试范本：`MongoDbInitializerTests`（既有断言即「行为等价」基线）、`MongoDbFixture`（Testcontainers MongoDB 共享容器）、`BackstageDataAggregationDiTests`（DI 注册断言）。
- 构建范本：`servers/recording-transcript-api/Dockerfile` 与 `.github/workflows/build-recording-transcript-api-image.yml`（paths 含自身 workflow 文件的样板）。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash、分支不复用）与 Conventional Commits：

- 本需求全部提交直接落在需求总分支 `feat/common-split`（需求总 PR #140 已存在，照常推送更新），不切开发子分支（沿用既定做法）。
- 提交信息：测试先行 `test:`、搬运/重构 `refactor(common):`（按域 scope）、构建面 `chore(ci):`/`chore(docker):`、文档 `docs:`；测试与实现分开提交。
- **小步提交且每步可编译**：域项目搬运期间（2.1–2.2）允许「旧 `Z.Vtuber.Common.csproj` 临时 `ProjectReference` 新项目」的过渡态（靠 SDK 项目引用传递性保持消费方编译，见 2.2），该临时引用与旧项目一并在 2.3 删除，评审时以最终态为准；每个提交 `dotnet build servers/Z.Vtuber.slnx` 必须通过、**既有回归测试必须全绿**（Phase 0/1 新增测试允许红到对应实现转绿）。
- 编码实现按 z-coding 流程以 subagent 按子项目并行（§4 线划分即子项目划分）。

## 3. 实施阶段

按「基线固化与守护测试先行（0，红）→ 按域测试先行（1，红）→ 分线搬运实现（2，绿）→ 收口验证（3）」推进。Phase 0 串行先行；Phase 1 可与 2.1 并行起草；Phase 2 按线推进（线内串行）；Phase 3 串行收口。

### Phase 0: 基线固化与守护测试先行（红，串行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | 基线固化：`dotnet build servers/Z.Vtuber.slnx` + `dotnet test tests/Z.Vtuber.Tests` 记录全绿基线；从 `MongoDbInitializer.cs` + `MongoDbInitializerTests` 提取**等价基线表**：25 个集合名常量值（逐字节）、19 个 `Ensure*` 的索引清单（集合/键/唯一性/搜索索引）、3 个迁移 marker key（`cookie_invalid_platform_split_v1`、`transcript_model_isdefault_cleanup_v1`、订阅拆分 key）与各 `Backfill*`/`Cleanup*` 行为断言 | 记录于 0.2/1.x 测试的硬编码期望值 | 基线表与源码逐项对得上 |
| 0.2 | **边界守护测试（红）**：`ProjectReferenceGuardTests`——解析 8 个服务 csproj 的 `ProjectReference` 闭包，断言等于提案 §2.3 矩阵；断言 7 个领域项目仅引用 `Z.Vtuber.Common.Core`（无域间引用、无环） | `tests/Z.Vtuber.Tests/ProjectReferenceGuardTests.cs` | 红 |
| 0.3 | **流水线守护测试（红）**：读 `.github/workflows/build-*-image.yml` 的 `paths`，断言 = 服务目录 + 该服务引用的 `servers/common/<域>/**` + 自身 workflow 文件（清单见提案 §9.2，与 0.2 矩阵同源） | `tests/Z.Vtuber.Tests/WorkflowPathsGuardTests.cs` | 红 |
| 0.4 | **常量等价测试（红）**：`CollectionNamesEquivalenceTests`——8 个 `<域>CollectionNames` + `CoreCollectionNames` 的 25 个常量值逐一等于 0.1 硬编码期望字符串（防「连错库」） | `tests/Z.Vtuber.Tests/CollectionNamesEquivalenceTests.cs` | 红 |

### Phase 1: 测试先行（红）——按域初始化与迁移测试

沿用 `MongoDbFixture`（每个测试类独立库），断言基线与 0.1 等价基线表逐项对照（「行为等价断言」）：

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | `CoreMongoSetupTests`：`zvtuber_meta` marker 读写幂等（`MigrationMarker.IsAppliedAsync`/`MarkAppliedAsync` 并发不抛 DuplicateKey）、`DropIndexIfExistsAsync` 删不存在索引不抛 | `tests/Z.Vtuber.Tests/CoreMongoSetupTests.cs` | 红 |
| 1.2 | `LiveMongoSetupTests`：8 组集合索引（含 `live_guards` 两个 Ensure 方法产出）；`MigrateAsync` 保持 `DropOldSubscriptionIndex → MigrateSubscriptionTypeSplit → EnsureSubscriptionCompositeIndex` 顺序语义；`BackfillAsync` 5 项逐个可独立调用且幂等（对照巨测 `Backfill_*` 用例） | `tests/Z.Vtuber.Tests/LiveMongoSetupTests.cs` | 红 |
| 1.3 | `CookieMongoSetupTests`：cookies / cookie_sessions / cookie_account_info + **weibo_cookies / weibo_cookie_sessions**（决策 9 归 Cookie 域）索引与幂等 | `tests/Z.Vtuber.Tests/CookieMongoSetupTests.cs` | 红 |
| 1.4 | `WeiboMongoSetupTests`：weibo_subscriptions / weibo_user_timeline 索引与幂等 | `tests/Z.Vtuber.Tests/WeiboMongoSetupTests.cs` | 红 |
| 1.5 | `RecordingMongoSetupTests`：recording_status（`RoomId` 唯一）+ **recordings**（`RoomId_1_StartedAt_-1`，从原 `EnsureTranscriptIndexesAsync` 拆出段，决策 8 归 Recording） | `tests/Z.Vtuber.Tests/RecordingMongoSetupTests.cs` | 红 |
| 1.6 | `TranscriptMongoSetupTests`：transcript_tasks/segments/models/auto_enqueue 四集合索引（`transcript_models` 不建 `IsDefault_1`）+ `CleanupAsync`（对照 `CleanupTranscriptModelIsDefault_*` 5 用例：`$unset` 幂等、marker 防重跑、并发安全） | `tests/Z.Vtuber.Tests/TranscriptMongoSetupTests.cs` | 红 |
| 1.7 | `TrainingMongoSetupTests`：training_datasets（唯一 `Type_1_Name_1`）/ training_dataset_items（唯一 `DatasetId_1_AudioKey_1` + 两过滤索引） | `tests/Z.Vtuber.Tests/TrainingMongoSetupTests.cs` | 红 |
| 1.8 | `NotificationMongoSetupTests`：notification_subscriptions（唯一 `(EventType, GroupId)`）+ `MigrateAsync`（对照 `CookieInvalidEventTypeSplit_*` 用例：marker `cookie_invalid_platform_split_v1`、存量 `cookie_invalid` → `bilibili_cookie_invalid` 幂等） | `tests/Z.Vtuber.Tests/NotificationMongoSetupTests.cs` | 红 |

### Phase 2: 实现（绿）——分线搬运

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | **线 A：Core 项目**：新建 8 个 csproj 空壳（配置逐字段复制）+ `slnx` 注册 + 旧 common 临时 `ProjectReference` Core；搬运 Core 归属（提案 §2.3）：`Contracts/` 10 类型（命名空间 `Z.Vtuber.Common.Contracts`，偏差 2）、`SubscriptionType`、`MetaRecord`/`BsonConvert`/`StringTolerantBsonDocumentSerializer`/`LiveEventPayloadParser`/`Locking` 2 接口/`CoreCollectionNames.Meta`、`MongoDbInitializer` 基座（`DatabaseName`/`MigrationMarker`/`DropIndexIfExistsAsync`）；消费方逐文件补 `using Z.Vtuber.Common.Contracts;`（以 CS0246/CS0234 清零为准，约 70 文件） | `servers/common/core/`、全部消费方 | `dotnet build` 过、既有测试绿 |
| 2.2 | **线 B：7 个领域项目搬运**（每域一个 commit，旧 common 临时引用已搬项目保持全绿）：按提案 §2.3 归属表搬实体/仓储/服务/工具，`MongoCollectionNames` 拆 `<域>CollectionNames`（值不变），`MongoDbInitializer` 对应方法平移为 `XxxMongoSetup`（提案 §3.2 内部接口表，**逐字搬运不改写不合并**；`EnsureTranscriptIndexesAsync` 的 recordings 段归 RecordingMongoSetup、其余归 TranscriptMongoSetup；`LiveEventTextIndexRepository.SearchIndexName` 随 Live 移动供 `LiveMongoSetup` 引用）；顺序 Core 已完 → Live → Cookie（含 weibo cookie 两集合，决策 9）→ Weibo → Recording → Transcript → Training → Notification | `servers/common/{live,cookie,weibo,recording,transcript,training,notification}/` | 每 commit `dotnet build` 过、既有测试绿 |
| 2.3 | **切换消费方并退役旧项目**：8 个服务 csproj 换成矩阵引用（无多余引用）；`tests/Z.Vtuber.Tests` csproj 显式补 8 个领域项目引用；删除 `servers/common/Z.Vtuber.Common.csproj` 与过渡期临时引用；`slnx` 的 common 一行换 8 个项目条目 | `servers/*/Z.Vtuber*.csproj`、`tests/.../Z.Vtuber.Tests.csproj`、`servers/Z.Vtuber.slnx` | 0.2/0.4 转绿（除 paths 项） |
| 2.4 | **线 C：服务启动面**：8 个 `Program.cs` 按提案 §3.2 调用表替换（**保持原顺序**，api/crawler 的 `DropOldSubscriptionIndex → MigrateSubscriptionTypeSplit → EnsureSubscriptionCompositeIndex → EnsureIndexes → Backfill*` 不可乱）；`MongoDbInitializer` 基座仍注册（供 `DatabaseName`/`MigrationMarker`）；`RecordingBackfillMigration`/`TranscriptModelMigration` 改 `CoreCollectionNames.Meta`；**偏差 1：删除 `servers/crawler/Program.cs` 的 `AddBackstageDataAggregation()` 死注册**（并修正 `BackstageDataAggregationExtensions` 过期注释） | `servers/*/Program.cs`、`servers/recording/Recording/RecordingBackfillMigration.cs`、`servers/recording-transcript-consumer/Services/TranscriptModelMigration.cs` | 8 服务启动序列与拆分前同序；crawler 不再引用 Cookie 域 |
| 2.5 | **线 E：既有测试适配（偏差 4）**：`MongoDbInitializerTests` 804 行/44 用例保留不肢解、调用点改指对应 `XxxMongoSetup`；`MongoDbFixture.ResetAsync` 清单改各域常量；`RecordingStatusRepositoryTests`/`SubscriptionTypeSplitMigrationTests`/`CookieInvalidEventTypeSplitMigrationTests`/`LiveEventPayloadBackfillTests`/`LiveEventSenderBackfillTests` 同步改指；`Fakes.cs` 三处 `NextAsync` 死方法清理 | `tests/Z.Vtuber.Tests/` | `dotnet test` 全绿 |
| 2.6 | **线 D：构建面**：8 个 Dockerfile csproj 拷贝行按引用收窄（照提案 §5.2 示例，逐项目保留路径防 COPY 拍平；源码 `COPY servers/common/ servers/common/` 不变）；8 条 workflow `paths` 换提案 §9.2 清单（api 6 项 / crawler 4 项 / cookie 3 项 / notifier 3 项 / recording 4 项 / transcript-api·consumer 各 6 项 / training 4 项，含自身 workflow 文件的三条保留该项） | `servers/*/Dockerfile`、`.github/workflows/build-*-image.yml` | 0.3 转绿；逐服务 `docker build` 通过 |
| 2.7 | 全量验证：`dotnet build` + `dotnet test` 全绿（含 0.x/1.x 新测试）；`dotnet test` 覆盖率核对（新增守护/按域测试覆盖核心逻辑 ≥ 80%） | 全部 | 构建零告警、测试全绿 |

### Phase 3: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 3.1 | 兼容性回归：`git diff` 确认 `contracts/`、`apps/web/`、`infra/apphost/`、MongoDB 集合/索引定义与文档结构**零改动**；对外 API 全量接口行为回归通过（提案 §10） | diff/grep 无违规 |
| 3.2 | 文档同步（实现点 6）：重写 `servers/common/AGENTS.md`（8 项目归属清单 + 归属约定；修正 `RecordingRecord`/`RecordingRepository` 归 Recording 域（决策 8，与现记述不一致）与过期契约描述 `ICookieNavProbeService`/`CookieProbeOutcome`/`NextAsync`）；`docs/engineering/backend-rules.md` 项目结构图与归属约定同步；8 个服务 `AGENTS.md` 中 `servers/common/Persistence/...` 等路径按新目录修正、引用图（`Z.Vtuber.Common` → 领域项目）更新 | 文档与实现逐条一致 |
| 3.3 | **验收演练（提案 §10）**：在 `release`/`release/**` 演练一次 `servers/common/transcript/**` 改动（如给 `TranscriptTaskRecord` 加字段），`gh run list` 确认**仅 3 条**流水线触发（transcript-api/transcript-consumer/training），`build-cookie-image`/`build-crawler-image` 等 5 条不触发；再演练 `servers/common/core/**` 改动确认 8 条全触发 | 演练记录 |
| 3.4 | 手工验证（z-aspire-orchestrator）：Aspire 拉起全部 8 服务 → 既有核心流程冒烟（直播事件/录播/转写/训练/通知/后台聚合）→ 逐服务镜像构建发布路径验证 | 手工验证记录 |

## 4. 并行策略

- **Phase 0 串行先行**（0.1→0.2→0.3→0.4）：0.1 基线表是全部等价断言与守护测试的数据源。
- **Phase 1 按域并行**（1.1–1.8 互不依赖，亦为 subagent 划分）；可与 2.1 并行起草。
- **Phase 2 线内串行、线间按依赖**：
  - **线 A（Core）** 2.1 必须最先完成；
  - **线 B（7 域）** 2.2 依赖 2.1，域与域之间无依赖可拆 7 个 subagent 并行（文件零交集），但按 Live→Cookie→Weibo→Recording→Transcript→Training→Notification 顺序合入以便逐 commit 绿；
  - **线 C（服务面）** 2.4 依赖 2.3；**线 E（测试适配）** 2.5 依赖 2.2 全部完成；**线 D（构建面）** 2.6 依赖 2.3（paths 与最终引用一致），可与 2.4/2.5 并行；
- **文件冲突规避**：`servers/common/` 各子目录互不相交归线 B 各 subagent；`Program.cs`/csproj 归线 C；Dockerfile/workflow 归线 D；`tests/Z.Vtuber.Tests` 中巨测与 fixture 归线 E，新增测试文件归 Phase 0/1 起草者。
- 全部提交在需求总分支 `feat/common-split`。

## 5. 验收对照

| PRD 功能点 / 成功标准（及提案 §10） | 实现任务 | 验证方式 |
|-----------|---------|---------|
| 功能点 1：公共模型按领域分组（唯一归属、无循环依赖、无模糊模型） | 2.1、2.2 | 0.2 边界守护、3.2 归属清单 |
| 功能点 2：服务按需引用领域（无多余引用、无全量依赖公共单元） | 2.3、2.4 | 0.2 引用矩阵断言、提案 §10①② |
| 功能点 3：自动化构建触发范围收窄（transcript 改动只触发 3 条；公共基础改动不遗漏） | 2.6 | 0.3 paths 守护、3.3 演练（提案 §10⑥⑦） |
| 功能点 4：拆分对使用者无感（接口/数据/本地编排不变） | 全程纯搬运约束、3.1 | 接口回归、diff 确认、3.4 手工验证 |
| 成功标准：改 Transcript 模型不触发 Cookie/Crawler | 2.6、3.3 | 演练记录（提案 §10⑥） |
| 成功标准：全量单测通过、镜像可构建发布 | 2.7、2.6、3.4 | `dotnet test` 全绿、`docker build` ×8 |
| 提案 §10③：常量值/索引定义与拆分前一致 | 2.2（逐字搬运） | 0.4 常量等价 + 1.2–1.8 按域等价断言 |
| 提案 §10④：8 服务启动序列同序、build/test 全绿 | 2.4、2.7 | code review 对照 + 测试 |
| 提案 §10⑧：对外契约/行为/MongoDB 数据零变化 | 范围约束 | 3.1 |
| 提案 §10⑤⑨：镜像可构建、文档同步（含 RecordingRecord 归属与过期描述修正） | 2.6、3.2 | `docker build`、文档逐条核对 |
| 规范门槛（TDD、覆盖率 ≥ 80%、Conventional Commits） | 0/1 测试先行、2.7、§2 | 红-绿提交记录、覆盖率报告 |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| CS0433 同名类型歧义 | 拆分后 `MongoCollectionNames` 若同名共存会被多引用方撞歧义 | 已按域改名 `<域>CollectionNames`（实现点 2）；0.4 常量等价锁定值不变 |
| crawler 死注册（偏差 1） | 漏删则编译失败；误判「有真实使用」则多引 Cookie 项目、paths 多一项，与 PRD 场景冲突 | 2.4 显式删除 + 0.2 断言 crawler 无 Cookie 引用；回退点见 §1.0 偏差 1 |
| 契约 using 迁移面（偏差 2） | 实际约 70 文件、面大于提案估计 | 机械补 using + 编译错误清零（2.1）；回退点：保留原命名空间 |
| 初始化范围差异（本期唯一行为差异） | 拆分前任一服务启动初始化全部领域，拆分后只初始化自己引用的领域 | 幂等初始化 + 每领域有常驻服务 + 部署顺序无关（提案 §5.2 实现点 3 分析）；1.2–1.8 按域等价测试兜底 |
| 过渡期临时引用（§2） | 旧 common 暂引新项目，靠 ProjectReference 传递性保编译，易被误留在最终态 | 2.3 一次性删除；0.2 守护测试断言最终矩阵（临时引用会让 0.2 红，倒逼清零） |
| Dockerfile 拷贝路径 vs ProjectReference 相对路径不一致 | restore 失败 | 2.6 逐服务 `docker build` 验证；拷贝行逐项目保留路径防拍平 |
| paths 清单漏改 | 「改了却没重建」，与 PRD 目标直接相反 | 0.3 守护测试（paths 与引用闭包同源断言）+ 3.3 演练 |
| 巨测/6 个测试文件调用点改指遗漏 | 编译期暴露（方法已不在 `MongoDbInitializer`） | 2.5 以编译错误清零为准；用例不删不合并，行为等价由既有断言保证 |
| `MetaRecord` 服务侧消费（recording/consumer 自管 marker） | 漏改引用会编译失败；改写其并发语义会引入行为差异 | 2.4 仅改常量引用不改逻辑 |
| 依赖 | .NET 10、Testcontainers MongoDB（既有 fixture）、8 服务/8 Dockerfile/8 条 workflow 齐备（§1.0 已核实）；无新增 NuGet/npm 依赖 | 已核实存在；TDD、覆盖率 ≥ 80%、仅 merge 为既有规范 |

## 7. 相关文档

- PRD：`docs/product/draft/20260928-common-split-prd.md`（v1.0）
- 技术提案：`docs/engineering/proposals/20260928-proposal-common-split.md`（v1.0）
- 前序 Plan：[[20260901-plan-api-crawler-split]]、[[20260927-plan-training-audio-dataset]]
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`
- 模块规范：`servers/common/AGENTS.md`、`servers/api/AGENTS.md`、`servers/crawler/AGENTS.md`、`servers/cookie/AGENTS.md`、`servers/recording/AGENTS.md`、`servers/recording-transcript-api/AGENTS.md`、`servers/recording-transcript-consumer/AGENTS.md`、`servers/notifier/AGENTS.md`、`servers/training/AGENTS.md`
- 现有契约：`contracts/*.yaml`（本期不变更）
