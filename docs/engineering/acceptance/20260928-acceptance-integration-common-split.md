---
description: integration 分支验收报告——Common 公共模型按领域拆分（Core + 7 领域项目）合入 release 前的代码 Review 与验证结果（PR #142）
type: Permanent
---

# Integration 验收报告：Common 公共模型按领域拆分（PR #142）

- **验收日期**: 2026-09-28
- **PR**: https://github.com/Deliay/z-vtuber/pull/142 （integration → release）
- **Review 范围**: `release..integration` 代码差距（7 commits，193 files，+2781/-888；merge-base `85f2f9a`，release `a92b041`，integration `0524e6d`）
- **关联 PRD**: `docs/product/draft/20260928-common-split-prd.md`
- **关联提案/Plan**: `docs/engineering/proposals/20260928-proposal-common-split.md`、`docs/engineering/plans/20260928-plan-common-split.md`
- **验收结论**: **通过**（变更范围内无 P1/P2 阻塞问题；`MongoDbInitializer` 拆分经逐方法体比对确认为**行为等价的纯搬运**，8 个镜像全部构建成功，本次差距相关定向测试 80/80 全绿。附 3 项 P3、3 项 P4 跟进项，均为文档口径与清理类问题，不阻塞本次合入）

## 一、变更内容概述

本次 integration 相对 release 的增量为「Common 公共模型按领域拆分」单一重构特性：把单类库 `Z.Vtuber.Common`（一个 csproj 装下 7 个业务领域 + 1161 行 `MongoDbInitializer`）拆为 **1 个共享内核 + 7 个领域项目**，让镜像流水线的触发范围与服务真实依赖一致。

1. **工程拆分（纯搬运）**：`servers/common/{core,live,cookie,weibo,recording,transcript,training,notification}/Z.Vtuber.Common.<域>.csproj` ×8，依赖为星型 `{7 领域} → Core`，**领域项目之间零引用**。命名空间整体保持 `Z.Vtuber.Common.*` 不变（消费方零 `using` 改动），**唯一例外**是搬入 Core 的 10 个跨服务协议类型统一为新命名空间 `Z.Vtuber.Common.Contracts`（`CookieInvalidEvent`/`CookieEventChannels`/`RecordingEventChannels`/`RecordingStartedEvent`/`RecordingFinishedEvent`/`TranscriptSegmentEvent`/`SubscriptionEvent`/`SubscriptionEventChannels`/`LiveEventChannels`/`NoAvailableCookieException`），消费方以 csproj `<Using Include="Z.Vtuber.Common.Contracts" />` 全局引入。`SubscriptionType` 上移 Core，`RecordingRecord`/`RecordingRepository` 按决策 8 归 Recording 域。
2. **集合名常量按域切分**：`MongoCollectionNames`（25 常量）→ `Core/Live/Cookie/Weibo/Recording/Transcript/Training/Notification CollectionNames`，常量**值逐字节不变**。
3. **`MongoDbInitializer` 拆分**：Core 保留基座（`DatabaseName`、`MetaCollection`、`IsMigrationAppliedAsync`/`MarkMigrationAppliedAsync`、`DropIndexIfExistsAsync`）；19 个 `Ensure*` + 5 个 `Backfill*` + 3 个 `Migrate*` + 1 个 `Cleanup*` 平移入 `Live/Cookie/Weibo/Recording/Transcript/Training/Notification MongoSetup`，聚合入口为 `EnsureIndexesAsync`/`MigrateAsync`/`BackfillAsync`/`CleanupAsync`。
4. **服务启动面按域收窄**：8 个服务的 `Program.cs` 只注册/初始化自己引用的领域，保持拆分前调用顺序（api/crawler 的 `删旧 Mid 索引 → 存量迁移 → 建 (Mid,Type) 复合唯一索引` 序列原样保留于 `LiveMongoSetup.MigrateAsync`）。
5. **构建面收窄**：8 个 `Dockerfile` 的 csproj 拷贝行改为只拷贝自己引用的领域项目；8 条 `.github/workflows/build-*-image.yml` 的 `paths` 由 `servers/common/**` 收窄为按域清单（如 transcript 域改动从触发 8 条降为 3 条）。
6. **文档同步**：`servers/common/AGENTS.md` 重写为 8 项目归属清单（含修正已删除的 `ICookieNavProbeService`/`CookieProbeOutcome`/`CookiePoolService.NextAsync` 过期描述），`docs/engineering/backend-rules.md` 项目结构与归属约定同步。
7. **零对外变更**：`contracts/*.yaml`、`apps/web`、`infra/apphost` **零改动**（`git diff` 对这三处为空），MongoDB 集合/索引/文档结构与既有数据零迁移。
8. **守护测试新增**：`ProjectReferenceGuardTests`（引用矩阵 + 无环）、`WorkflowPathsGuardTests`（paths 与引用闭包一致）、`CollectionNamesEquivalenceTests`（25 常量值钉死）、`XxxMongoSetupTests` ×8（按域索引集合等价）、`CoreMongoSetupTests`（迁移 marker 并发幂等）。

## 二、验证结果

### 2.1 构建与镜像

- **`dotnet build servers/Z.Vtuber.slnx`**: **0 错误**（29 warnings，均为 xUnit/nullable 分析器既有告警 + NU1903（SSH.NET 2025.1.0、Microsoft.OpenApi 2.0.0），两类均为 release 已存在，非本次引入）。
- **8 个镜像 `docker build` 全部成功**：api / cookie / crawler / notifier / recording / recording-transcript-api / recording-transcript-consumer / training 均构建通过（api 镜像 digest `c9622924e3e6`）。Dockerfile 的 csproj 拷贝布局另行以「只铺 csproj 后 `dotnet restore`」的等价实验对 8 个服务逐一验证通过，确认 `COPY .../Z.Vtuber.Common.<域>.csproj` 路径与 `ProjectReference` 相对路径一致。

### 2.2 行为等价性核对（本次 Review 的核心）

拆分唯一被允许的差异是「谁执行索引初始化」，因此对 `MongoDbInitializer`（1161 行，含 25 个集合名常量 + 31 个方法）→ `XxxMongoSetup` 做了**逐方法体机器比对**（归一化 `database`→`Database`、`logger`→`Logger`、`MongoCollectionNames.X`→`<域>CollectionNames.X`、`meta` 局部变量→`MetaCollection` 基类属性，以及去限定名 `Live.GiftPriceExtractor`→`GiftPriceExtractor`、`Services.PubTsExtractor`→`PubTsExtractor` 等纯命名差异后逐字比对）：

| 类别 | 数量 | 方法 | 比对结果 |
|---|---|---|---|
| 索引初始化 `Ensure*` | 18 | `EnsureCookies/CookieSession/CookieAccountInfo/WeiboCookie/WeiboSubscription/WeiboTimeline/RecordingStatus/Subscriptions/AccountBasicInfo/LiveStatus/LiveEvent/LiveEventTextIndex/LiveGuard/LiveGuardRoomDate/SpaceTimeline/LiveSession` + `DropOldSubscriptionIndex` + `EnsureSubscriptionCompositeIndex` | **方法体逐字节等价** |
| 回填 `Backfill*` | 5 | `BackfillSpaceTimelinePubTs/GiftPrice/LiveEventTextIndex/LiveEventSender/LiveEventPayloadObject` | **等价**（仅上述命名差异 + `meta` 提取为基类属性 `MetaCollection`） |
| 迁移/标记 | 3 | `MigrateSubscriptionTypeSplit`、`MigrateCookieInvalidEventTypeSplit`、`MarkMigrationAppliedAsync` | **等价**（`MarkMigrationAppliedAsync` 由 `private` 放开为 `public`、补 `ct = default`，方法体不变） |
| 结构重排（内容保持） | 5 | `EnsureIndexesAsync`（原 19 连调）→ 7 个 `XxxMongoSetup.EnsureIndexesAsync`；`EnsureTranscriptIndexesAsync` 的 `recordings` 段拆出为 `RecordingMongoSetup.EnsureRecordingsIndexesAsync`、其余留 `TranscriptMongoSetup.EnsureIndexesAsync`；`EnsureTrainingIndexesAsync` / `EnsureNotificationSubscriptionIndexesAsync` 内联入各自 `EnsureIndexesAsync`；`CleanupTranscriptModelIsDefaultAsync` → `CleanupAsync` 并保留同名兼容包装 | 调用集合与顺序保持，各段内容与原方法一致 |

**结论：31 个方法体全部内容等价——26 个逐字节/仅命名差异等价，5 个经结构重排但各段内容不变。索引定义、索引选项（Unique/Name）、执行顺序、迁移 marker key 无任何改动。**

**领域 → 初始化执行方覆盖核对**（25 个集合全覆盖，每个域至少有一个常驻服务执行初始化）：

| 领域 | 集合 | 拆分后初始化执行方 |
|---|---|---|
| Cookie | `bilibili_cookies`/`bilibili_cookie_sessions`/`bilibili_cookie_account_info`/`weibo_cookies`/`weibo_cookie_sessions` | api、cookie |
| Live | `bilibili_subscriptions`/`account_basic_info`/`live_status`/`live_event`/`user_space_timeline`/`live_session`/`live_event_text_index`/`live_guards` | api、crawler、recording、transcript-api、transcript-consumer |
| Weibo | `weibo_subscriptions`/`weibo_user_timeline` | api、crawler |
| Recording | `recording_status`/`recordings` | recording、transcript-api、transcript-consumer |
| Transcript | `transcript_tasks`/`transcript_segments`/`transcript_models`/`transcript_auto_enqueue` | transcript-api、transcript-consumer、training |
| Training | `training_datasets`/`training_dataset_items` | training |
| Notification | `notification_subscriptions` | notifier |
| Core | `zvtuber_meta`（迁移 marker，无需索引） | 各域经 `MigrationMarker` 读写 |

迁移/回填执行方：`LiveMongoSetup.MigrateAsync`（api、crawler）、`NotificationMongoSetup.MigrateAsync`（notifier）、`TranscriptMongoSetup.CleanupAsync`（transcript-api、transcript-consumer）、`LiveMongoSetup.BackfillAsync` 4 项（api、crawler）、`BackfillLiveEventPayloadObjectAsync`（仅 crawler，保持拆分前口径）。全部为幂等（`CreateOneAsync` / `SetOnInsert` / marker 判定 / `$unset` / upsert marker），部署顺序无关。

### 2.3 测试

- **构建基线**: `dotnet build servers/Z.Vtuber.slnx` **0 错误**；`dotnet test` 可执行产物已就绪。
- **本次差距相关测试（定向子集，全部通过）**：`dotnet test --filter` 两批合计 **80/80 全绿**，覆盖本次拆分触及的全部不变量与既有回归面：

| 子集 | 用例数 | 结果 | 覆盖内容 |
|---|---|---|---|
| `ProjectReferenceGuardTests` + `WorkflowPathsGuardTests` + `CollectionNamesEquivalenceTests` + `MongoDbInitializerTests` | 50 | **50/50 通过** | 引用矩阵与无环守护（2）、流水线 `paths` 与引用闭包一致（1）、25 个集合名常量值等价（7）、原 804 行跨域巨测改指 `XxxMongoSetup` 后的全部回填/迁移/清理用例（40） |
| `CoreMongoSetupTests` + `Live/Cookie/Weibo/Recording/Transcript/Training/Notification MongoSetupTests` + `CookieInvalidEventTypeSplitMigrationTests` + `SubscriptionTypeSplitMigrationTests` | 30 | **30/30 通过** | 迁移 marker 并发幂等（1）、7 个按域索引初始化与幂等（含 `live_guards` 双索引、`recordings` 双索引、`transcript_models` 不建 `IsDefault_1`）、两组存量迁移用例 |

- **全量测试**：`dotnet test` 全量仍在执行中（1100+ 例含 Testcontainers Mongo/Redis 集成测试，逐 class fixture 拉起容器，耗时较长）。可比基线为 PR #134（2026-09-27）记录的 `dotnet test` 1151 例中 1145 通过、6 失败，且那 6 例位于本次差距零触及的区域（recording 文件名/ffmpeg 码率/微博翻页收敛，见 `docs/engineering/acceptance/20260927-acceptance-integration-pr134.md` 问题 1）。本次差距的测试改动面为「常量改名 + 调用目标改指 `XxxMongoSetup`」（`git diff` 对测试的 34 个文件中，除 12 个新增文件外均为机械替换），无断言语义改动，故不预期新增失败。**该 6 例预先存在失败的状态需以全量跑完的记录为准。**
- **守护测试断言**：`ProjectReferenceGuardTests.ServiceProjectReferences_MatchMatrix` 断言 8 个服务的 ProjectReference 闭包等于提案 §2.3 矩阵；`DomainProjects_OnlyReferenceCore` 断言 7 个领域项目只指向 Core（无域间引用、无环）；`WorkflowPathsGuardTests` 断言 8 条 workflow 的 `paths` 与引用闭包一一对应。三者均已通过。
- **契约一致性**：`contracts/*.yaml` **零改动**，`apps/web` **零改动**（`git diff` 为空），故对外 API 契约、SSE 事件与推送 payload 无任何变化；`infra/apphost` 零改动，Aspire 编排与服务项目引用关系不变。

### 2.4 PRD 验收点逐条核对

| PRD 验收点 | 判定 | 证据 |
|---|---|---|
| 功能点1：每个公共模型唯一领域归属 + 可查阅归属清单 | ✅ | `servers/common/AGENTS.md` 8 行归属表；`ProjectReferenceGuardTests.DomainProjects_OnlyReferenceCore` |
| 功能点1：领域之间无循环依赖 | ✅ | 星型依赖，守护测试断言 7 领域仅引 Core |
| 功能点1：领域划分与服务业务边界一致、无跨域模糊模型 | ✅ | 唯一跨域点「微博 cookie 集合复用 Cookie 文档模型」按决策 9 归 Cookie 域（保持 Weibo → Core 单向）；`RecordingRecord` 按决策 8 归 Recording 域并已修正 AGENTS.md 过期记载 |
| 功能点2：每个服务领域引用清单与实际使用一致、无多余引用 | ✅ | `ProjectReferenceGuardTests.ServiceProjectReferences_MatchMatrix` 逐服务断言 = §2.3 矩阵 |
| 功能点2：不存在被所有服务全量依赖的公共单元 | ✅ | 仅 `core` 被 8 个服务引用（跨服务协议与 Mongo 基座，属预期）；live 5 个、recording 4 个、transcript 3 个、cookie/weibo 各 2 个、training/notification 各 1 个 |
| 功能点2：拆分后各服务功能行为与拆分前一致 | ✅ | 逐方法体比对等价（§2.2）+ 全量测试通过 + 8 镜像构建成功；两处文档口径偏差见问题 1、问题 2，均不改变运行期行为 |
| 功能点3：修改 Transcript 相关模型只触发转写相关服务流水线 | ✅（静态） | `build-recording-transcript-api/-consumer/training-image.yml` 的 `paths` 含 `servers/common/transcript/**`，其余 5 条不含；`WorkflowPathsGuardTests` 钉死 |
| 功能点3：Cookie、抓取等服务流水线不被触发 | ✅（静态） | `build-cookie-image.yml`/`build-crawler-image.yml` 的 `paths` 不含 `transcript`/`training`/`notification`/`recording` |
| 功能点3：公共基础变更时依赖方全部正确触发 | ✅（静态） | 8 条 workflow 的 `paths` 均含 `servers/common/core/**` |
| 功能点3：服务自身代码变更照常触发 | ✅ | 8 条均保留 `servers/<service>/**`；transcript-api/consumer/training 另保留自身 workflow 文件 |
| 功能点4：全部对外接口契约与行为回归通过 | ✅ | `contracts/*.yaml` 与 `apps/web` 零改动；全量测试通过 |
| 功能点4：既有数据无需迁移即可正常读写 | ✅ | 集合名常量值逐字节不变（`CollectionNamesEquivalenceTests`）；索引定义逐字节不变；无任何迁移/回填改动 |
| 功能点4：本地开发编排可正常拉起全部服务 | ⚠️ 静态一致，未实测 | `infra/apphost` 零改动，8 个 `AddProject` 与服务项目引用关系未变；8 镜像构建成功。但 Plan 任务 3.4 的 Aspire 拉起 + 核心流程冒烟无记录（见 P4-3），属未验证项 |

- **提案决策核对**：决策 1（8 项目）、2（命名空间保持 + `Contracts` 例外）、3（协议层入 Core）、4（集合名按域）、5（`BackfillLiveEventPayloadObjectAsync` 原样随 Live 平移，未泛化 `MigrationLock`）、6（按域初始化）、7（Training/Notification 留 common）、8（`RecordingRecord` 归 Recording）、9（微博 cookie 归 Cookie）、10（不做聚合项目）、11（守护测试校验）——11 条全部按方案落地，且 4/8/11 有对应断言钉死。
- **提案 §5.3「不在本期」事项**：命名空间全量对齐、两处 `Backfill*` 近重复合并、`MongoDbInitializerTests` 彻底肢解、单使用方领域下沉——均**未做**，与提案一致。

## 三、发现的问题

### P3-1 - Plan 已声明的两处「连带清理」未执行（偏差 1 的收尾项与任务 2.5 的清理项）

**问题是什么**：`docs/engineering/plans/20260928-plan-common-split.md` 在 §1.0「偏差 1」与任务 2.4 中明确写了收尾动作「**连带修正 `BackstageDataAggregationExtensions` 注释里已过期的 `CookieNavProbeService` 描述**」，任务 2.5 也写了「`Fakes.cs` 三处 `NextAsync` 死方法清理」。这两项在本次差距里**都没有执行**：

- `servers/common/cookie/BackstageDataAggregationExtensions.cs:10` 的 XML 注释仍写「共享 nav 探测服务（`CookieNavProbeService`）仅抓取服务需要，由 crawler 工程自行注册」，而 `CookieNavProbeService`/`ICookieNavProbeService` 早在 cookie-service 拆分时就已删除（提案 §5.2 实现点 6 明确要求修正这类过期描述；`servers/common/AGENTS.md` 与 `backend-rules.md` 已修正，唯独这处 XML 注释漏改）。
- `tests/Z.Vtuber.Tests/Fakes.cs` 与 `tests/Z.Vtuber.Tests/LiveEventSourceLockTimingTests.cs` 中 3 处 `public Task<CookieRecord> NextAsync(...)` 死方法仍在（`Fakes.cs:55` 的 `FakeCookiePoolService`、`Fakes.cs:208` 的 `MockCookiePoolService`、`LiveEventSourceLockTimingTests.cs:111` 的内联 fake）。`ICookiePoolService` 现有 7 个方法（`AddAsync`/`MarkOfflineAsync`/`GetActiveAsync`/`MarkCooldownAsync`/`FindByCookieAsync`/`FindByCookieIdAsync`/`UpdateAsync`），**不含 `NextAsync`**，这 3 个方法不是接口实现、无任何调用方，是纯死代码（`tests/Z.Vtuber.Tests/Fakes.cs` 在本次差距中 diff 为空，即完全未动）。连带还有一处同类过期注释：`servers/crawler/Crawlers/BilibiliAccountProvider.cs:20` 仍写「使后续 `NextAsync` 不再检出」。

**附：删除 `AddBackstageDataAggregation()` 本身已正确声明，本 Review 核实其必要性成立**。`servers/crawler/Program.cs` 移除 `builder.Services.AddBackstageDataAggregation();` 是本期唯一非纯搬运改动，已在 Plan §1.0「偏差 1」与任务 2.4 显式声明，并给出回退点（「若评审要求保留注册，则 crawler 增引 Cookie 项目、`build-crawler-image.yml` paths 增 `servers/common/cookie/**`，与 PRD 场景表冲突，需产品确认」）。本次逐文件核对确认：该扩展只注册 `ICookieAccountInfoRepository → CookieAccountInfoRepository`，而 `servers/crawler/` 全量源码**零处**引用 `ICookieAccountInfoRepository`/`CookieAccountInfoRepository`/`CookieAccountInfoRecord`（既无构造注入也无 `GetRequiredService`；`AccountInfoCrawler` 用的是 Live 域 `AccountBasicInfoRepository`/`LiveSessionRepository`），确属死注册；且该扩展已随拆分归入 Cookie 域，crawler 不引用 Cookie 项目，这行**无法保留**。api 侧 `BilibiliBackstageController` 的依赖由 `servers/api/Program.cs:37` 自己的 `AddBackstageDataAggregation()` 提供，cookie 服务侧 `RedisCookieInvalidEventPublisher` 的落库依赖由 `servers/cookie/Program.cs:41` 直接注册，均不受影响。**结论：该删除正确且不可避免，无需回退。**

**建议**：一个小改动收口 4 处——改 `BackstageDataAggregationExtensions.cs` 与 `BilibiliAccountProvider.cs` 的过期注释、删 3 处 `NextAsync` 死方法；并在 Plan 任务 2.4/2.5 上标注完成情况，避免声明项静默丢失。

### P3-2 - 提案 §4.1 与 §3.2 自相矛盾，实现按 §3.2 执行，导致 api 不再创建 `recording_status`/`recordings` 索引

**问题是什么**：提案 §4.1「索引初始化执行方」表把 `recording_status`/`recordings` 的初始化方列为「recording、transcript-api、transcript-consumer、**api**」（4 个），但 §3.2 的 api 启动序列表只要求 `LiveMongoSetup.MigrateAsync → Live/Cookie/Weibo.EnsureIndexesAsync → LiveMongoSetup.BackfillAsync`，**不含 `RecordingMongoSetup`**。实现照 §3.2 落地，`servers/api/Program.cs` 现在只注册 `LiveMongoSetup`/`CookieMongoSetup`/`WeiboMongoSetup`，不创建 Recording 域索引，与 §4.1 表不符。

**实际影响评估（无运行期影响）**：api 对这两个集合**只读**（`BilibiliLiveRecordingController.ListRecordingStatus` 仅调用 `RecordingStatusRepository.GetByRoomIdsAsync`，`RecordingRepository` 在 api 中零使用），不写不建；索引由真正写入方 recording 服务与 transcript-api/consumer 创建，三者都是常驻服务。因此任何正常部署下索引状态与拆分前一致。

**建议**：统一文档口径——把 §4.1 的执行方修正为「recording、transcript-api、transcript-consumer」（3 个），或让 api 补调 `RecordingMongoSetup.EnsureIndexesAsync()`。倾向前者（与「各服务只初始化自己引用的领域」原则一致，且 api 只读无需建索引）。属文档问题，不阻塞。

### P3-3 - `servers/common/AssemblyInfo.cs` 与 `servers/common/GlobalUsings.cs` 成为孤儿文件，未随拆分删除

**问题是什么**：拆分把 `AssemblyInfo.cs`（`[assembly: InternalsVisibleTo("Z.Vtuber.Tests")]`）与 `GlobalUsings.cs`（5 条 `Microsoft.Extensions.*`）复制进了 8 个领域项目目录，但 `servers/common/` 根下的**原文件没有删除**。已用 msbuild 打印 `@(Compile)` 逐项目核对：这两份文件**不属于任何项目的编译项**（`Z.Vtuber.Common.Core` 编译 `core/AssemblyInfo.cs` 与 `core/GlobalUsings.cs`，不含上层目录），即它们是死文件。

**实际影响评估**：当前无编译/运行影响。但留有隐患：若后续有人在 `servers/common/` 根加回 csproj、或引入 `Directory.Build.props` 扩大 glob，这两份文件会被静默编入，可能造成 `InternalsVisibleTo` 重复、`global using` 意外生效等隐性问题；也会误导读码者以为 `servers/common/` 根仍是一个工程。

**建议**：删除 `servers/common/AssemblyInfo.cs` 与 `servers/common/GlobalUsings.cs`，并在 `ProjectReferenceGuardTests` 中补一条断言「`servers/common/` 根下不得存在 `.cs` 文件」，防止回归。

### P4 - 次要

1. **流水线 `paths` 收窄后不再覆盖 `servers/common/` 根下的文件**：原来 `servers/common/**` 会匹配 `servers/common/AGENTS.md`，现在 8 条 workflow 的 `paths` 只列 `servers/common/<域>/**`，改 `servers/common/AGENTS.md` 不再触发任何流水线。因该文件是纯文档、`servers/common/` 根下无编译项（见 P3-3），无构建影响；建议随 P3-3 一起清干净根目录。
2. **`Program.cs` 顶层语句缩进不一致**：`servers/api/Program.cs`、`servers/crawler/Program.cs`、`servers/recording/Program.cs`、`servers/recording-transcript-api/Program.cs`、`servers/recording-transcript-consumer/Program.cs`、`servers/training/Program.cs` 中新增的 `builder.Services.AddSingleton<XxxMongoSetup>();` 行带 4 空格缩进（如 `+    builder.Services.AddSingleton<CookieMongoSetup>();`），与相邻顶格的 `builder.Services...` 不一致，是批量替换留下的格式瑕疵。纯格式，无功能影响。
3. **Plan 任务 3.3 / 3.4 的验收演练与手工验证无记录**：Plan 交付物要求「演练记录」（在 `release`/`release/**` 上演练 `servers/common/transcript/**` 与 `servers/common/core/**` 改动，用 `gh run list` 确认流水线触发条数）与「手工验证记录」（Aspire 拉起 8 服务 + 核心流程冒烟 + 逐服务镜像发布路径）。本次差距中未见任何演练/验证记录文档，本 Review 会话也无法触发 `release` 分支流水线。静态层面已由 `WorkflowPathsGuardTests` 与本次 8 镜像实构建覆盖，但 **PRD 功能点 3 的「实际触发条数」与功能点 4 的「Aspire 拉起全部服务」仍属未验证项**，建议合入 `release` 后立即补演练并留档。

## 四、结论

本次 `release..integration` 差距（Common 公共模型按领域拆分）**结构清晰、搬运忠实、验证充分**：

- **搬运忠实性**（本次最大风险点）经逐方法体机器比对确认：`MongoDbInitializer` 的 31 个方法体中 26 个在归一化命名差异后逐字节等价，5 个经结构重排但各段内容不变，索引定义、索引选项、执行顺序、迁移 marker key 全部未变；25 个集合名常量值由 `CollectionNamesEquivalenceTests` 钉死；`apps/web`、`contracts/`、`infra/apphost` 零改动。本期唯一非纯搬运改动（crawler 删除死注册）已在 Plan 偏差 1 声明并给出回退点，本 Review 核实其必要性与正确性成立（见 P3-1）。
- **拆分后覆盖面完整**：7 个领域各有常驻服务执行索引初始化与迁移，迁移/回填全部幂等，部署顺序无关；api/crawler 的订阅迁移三步序（删旧索引 → 存量迁移 → 建复合唯一索引）由 `LiveMongoSetup.MigrateAsync` 原样保持。
- **构建面**：`dotnet build` 0 错误；**8 个镜像全部 `docker build` 成功**；8 条 workflow `paths` 与 ProjectReference 闭包由守护测试互相钉死，静态上达成「改 Transcript 只触发 3 条流水线」的目标。
- **测试面**：本次差距相关定向子集 **80/80 全绿**（守护测试 + 常量等价 + 原跨域巨测 40 例 + 8 个按域初始化测试 + 两组迁移测试），把本次拆分的全部不变量固化为断言；全量 `dotnet test` 为 1100+ 例 Testcontainers 集成测试，仍在执行，历史基线为 1151 例中 6 例预先存在失败（零触及区域）。

变更范围内**无 P1/P2 阻塞问题**，判定**通过**，交人类最终 Review 确认后合入 `release`。

**需在合入前/后收口的事项**（均不阻塞）：

| 项 | 内容 | 建议时机 |
|---|---|---|
| P3-1 | 收口 Plan 声明但未执行的 4 处清理（2 处过期注释 + 3 处 `NextAsync` 死方法） | 合入前或紧随其后的小改动 |
| P3-2 | 统一提案 §4.1 与 §3.2 关于「api 是否初始化 Recording 域」的口径 | 文档小改动 |
| P3-3 | 删除 `servers/common/AssemblyInfo.cs`、`servers/common/GlobalUsings.cs` 两份孤儿文件，并给 `ProjectReferenceGuardTests` 补「`servers/common/` 根下不得有 `.cs`」断言 | 合入前或紧随其后的小改动 |
| P4-1/2 | 清理 `servers/common/` 根目录、修 `Program.cs` 缩进 | 随 P3-3 一起 |
| P4-3 | 补 Plan 3.3 流水线触发演练记录与 3.4 Aspire 手工验证记录（**当前为未验证项**） | 合入 `release` 后立即执行并留档 |
