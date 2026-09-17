---
description: Transcript 模型存量 IsDefault 字段兼容修复实现 Plan——基于 PRD v1.0 与技术提案（fix/transcript-model-isdefault）的实施阶段划分与任务分解；双层修复：TranscriptModelRecord 标注 [BsonIgnoreExtraElements] 止血 + MongoDbInitializer 新增幂等清理迁移 CleanupTranscriptModelIsDefaultAsync（zvtuber_meta marker + BsonDocument 级 $unset），api 与 consumer 启动各自接线；TDD 测试先行（Testcontainers MongoDB），无契约变更
type: Permanent
---

# Transcript 模型存量字段兼容修复 - 实现 Plan

**功能名称**: Transcript 模型存量字段兼容修复
**关联 PRD**: `docs/product/draft/20260917-transcript-model-isdefault-fix-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20260917-proposal-transcript-model-isdefault-fix.md`
**Plan 版本**: v1.0
**创建日期**: 2026-09-17
**作者**: Workflow Agent（代起草）
**feat-branch**: `fix/transcript-model-isdefault`（需求总分支，已推送 origin；需求总 PR：<https://github.com/Deliay/z-vtuber/pull/117>）

## 1. 概述

本 Plan 基于 PRD v1.0 与技术提案编写，指导「Transcript 模型存量字段兼容修复」的编码实施。改动分三块：① 测试先行——固化「存量含未知字段的文档可正常反序列化」与「清理迁移生效 + 幂等」两类行为（红）；② 实现——`TranscriptModelRecord` 标注 `[BsonIgnoreExtraElements]`（止血）、`MongoDbInitializer` 新增 `CleanupTranscriptModelIsDefaultAsync` 幂等迁移（清源）、api/consumer 启动接线；③ 收口——全量测试回归与文档同步。无 OpenAPI 契约变更、无 web 前端改动。实施全程遵循 `docs/engineering/common-rules.md`（TDD、核心逻辑覆盖率 ≥ 80%）、`backend-rules.md` 及 `servers/common`、`servers/recording-transcript-api`、`servers/recording-transcript-consumer` 的 AGENTS.md。

### 1.0 上游核实结论（代码现状复查）

对需求总分支 `fix/transcript-model-isdefault` 代码核实，提案技术路径**可行**，现状细节如下，处理方式在对应任务中给出：

- **实体确认无容错标注**：`servers/common/Persistence/Entities/TranscriptModelRecord.cs:6` 类声明无 `[BsonIgnoreExtraElements]`，字段为 `Id/Name/Provider/Type/ApiKey/Model/Enabled/MaxConcurrency/CreatedAt`，`MongoDB.Bson.Serialization.Attributes` 已 using（:2）。**处理**：类声明追加一行标注即可（任务 2.1）。
- **marker 幂等模式范本在位**：`MongoDbInitializer.MarkMigrationAppliedAsync`（`MongoDbInitializer.cs:658-663`）用 `ReplaceOneAsync` + `IsUpsert=true` 写 `zvtuber_meta`，天然防双服务并发写 marker 的 DuplicateKey；既有迁移常量集中在文件尾部区域（如 `SubscriptionTypeSplitMigrationKey`/`CookieInvalidEventTypeSplitMigrationKey`，:665-666）。**处理**：新迁移常量 `TranscriptModelIsDefaultCleanupMigrationKey = "transcript_model_isdefault_cleanup_v1"` 与新方法沿用同文件同风格（任务 2.2）。
- **api 启动接线点**：`servers/recording-transcript-api/Program.cs:45-49`，`EnsureIndexesAsync()` 在 :48。**处理**：:48 后追加一行 `await initializer.CleanupTranscriptModelIsDefaultAsync();`（任务 2.3）。
- **consumer 启动接线点**：`servers/recording-transcript-consumer/Program.cs:80-87`，`EnsureIndexesAsync()` 在 :83，`TranscriptModelMigration.MigrateAsync()` 在 :85-86。**处理**：:83 后追加一行清理迁移调用；`TranscriptModelMigration`（`Model` 回填）保持不变（任务 2.3）。
- **迁移须用 `BsonDocument` 级操作**：`IsDefault` 已不在实体映射，强类型 `Builders<TranscriptModelRecord>` 无法表达 `Unset("IsDefault")`；集合经 `database.GetCollection<BsonDocument>(MongoCollectionNames.TranscriptModels)` 获取（常量 `MongoCollectionNames.TranscriptModels` 在 `MongoDbInitializer.cs:31`）。**处理**：filter 用 `Builders<BsonDocument>.Filter.Exists("IsDefault", true)`，update 用 `Builders<BsonDocument>.Update.Unset("IsDefault")`（任务 2.2）。
- **测试范本在位**：`MongoDbInitializerTests`（`tests/Z.Vtuber.Tests/MongoDbInitializerTests.cs:9-76`）模式为 `MongoDbFixture` + `NullLogger<MongoDbInitializer>.Instance` + `ResetAsync`，已有幂等（:38-52）与双实例并发 marker（:54-76）用例可复刻；`TranscriptApiTests`（:20-63）直接 new 控制器 + 真实仓储（Testcontainers MongoDB/Redis）。**处理**：迁移测试并入 `MongoDbInitializerTests`；反序列化容错测试在 `TranscriptApiTests` 中用 `GetCollection<BsonDocument>` 直插含 `IsDefault` 的存量形态文档（绕过实体映射），再走路由控制器断言 200（任务 1.1/1.2）。
- **`[BsonIgnoreExtraElements]` 对写入侧无影响**：实体序列化只写映射字段；`transcript_models` 由 api 的 models CRUD 与 consumer 读取，双方共享同一实体类，标注后双方读写语义一致，无需其他改动。
- **无契约/前端波及面**：grep 确认本次不改 `contracts/` 与 `apps/web/`；`StartupDiTests` 等既有测试不涉及启动迁移调用串，无需调整。

### 1.1 实施范围

- **做**：
  - 测试：`MongoDbInitializerTests` 新增清理迁移用例（生效/幂等/不动无字段文档/并发双跑不报错）；`TranscriptApiTests` 新增「存量含 `IsDefault` 文档 → 任务/模型/设置接口 200」用例。
  - common：`TranscriptModelRecord` 加 `[BsonIgnoreExtraElements]`；`MongoDbInitializer` 新增 `CleanupTranscriptModelIsDefaultAsync`（marker 常量 + Exists/Unset + upsert marker + 日志）。
  - 接线：api `Program.cs` 与 consumer `Program.cs` 启动时各追加一行迁移调用。
- **不做**：OpenAPI 契约变更；web 前端改动；其他实体的 `[BsonIgnoreExtraElements]` 普查；存量 DB 旧 `IsDefault_1` 索引强制 drop（提案 §5.3 明确，运维可选）；`TranscriptModelMigration` 的 `Model` 回填逻辑（保持不变）；实体字段删除的"代码-数据同步"流程规范（另立事项）。

### 1.2 现状基线

- **迁移范本**：`MongoDbInitializer.MigrateCookieInvalidEventTypeSplitAsync`（:1019-1070，marker 短路 + 批量处理 + upsert marker + 统计日志）；`BackfillGiftPriceAsync`（:373-437）。
- **marker 设施**：`MarkMigrationAppliedAsync`（:658-663）upsert 语义，双服务并发安全。
- **测试 fixture 范本**：`MongoDbInitializerTests.cs:9-76`；`TranscriptApiTests.cs:20-63`（控制器直构造 + `Ok()` 断言辅助 :65-66）。
- **接线范本**：api `Program.cs:45-49` / consumer `Program.cs:80-87` 的启动 scope 调用串。

## 2. 分支与提交流程

遵循 z-git 规范（仅 merge、禁止 rebase/squash）与 Conventional Commits：

- 本需求开发全程只使用需求总分支 `fix/transcript-model-isdefault`：实现、测试、文档全部直接在该分支提交，不切开发子分支（沿用上期需求既定做法）。
- 需求总分支已推送 origin，需求总 PR 已存在（#117），提交照常推送更新分支。
- 提交信息使用 Conventional Commits：测试先行 `test:`、实现 `fix:`、文档 `docs:`；测试与实现分开提交。
- 改动面小（2 个测试文件 + 4 个源码文件），单线串行实施即可，不拆并行 subagent（见第 4 节）。

## 3. 实施阶段

按「TDD 红 → 绿 → 收口验证」推进。Phase 1 测试先行；Phase 2 实现；Phase 3 串行收口。

### Phase 1: 测试先行（红）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | 清理迁移测试（新用例并入既有文件，MongoDbFixture 模式照 :9-13）：① **生效**——预置 2 条含 `IsDefault` 的 `BsonDocument`（含不同业务字段值）+ 1 条不含的 → 执行 `CleanupTranscriptModelIsDefaultAsync` → 含字段文档的 `IsDefault` 被移除、其余字段值逐一比对不变、不含字段文档未改动；② **幂等**——连续执行两次，第二次无副作用（文档数/字段不变）、marker 存在且唯一；③ **marker 短路**——预写 marker 后执行，含 `IsDefault` 的文档不被清理（证明短路生效）；④ **空集合**——集合为空时正常完成不报错、marker 写入；⑤ **并发双跑**——两个 initializer 实例并发执行（照 :54-76 模式），不抛异常、marker 唯一 | `tests/Z.Vtuber.Tests/MongoDbInitializerTests.cs`（改） | 编译失败（方法尚不存在）→ 占位最小声明后红 |
| 1.2 | 反序列化容错测试（并入 `TranscriptApiTests`）：① 用 `mongo.Database.GetCollection<BsonDocument>(MongoCollectionNames.TranscriptModels)` 直插存量形态文档（含 `IsDefault: true` 及实体全部字段，seed 走 `SeedModelAsync` 形态但以 BsonDocument 落库）；② 断言 `GET /api/transcripts/models`（`CreateModelsController().List`）返回 200 且字段映射正确（name/model 等值正确，响应不含 isDefault）；③ 断言 `GET /api/transcripts/task`（`CreateTasksController().ListTasks`，先 seed 关联该模型的任务）返回 200 且 `modelName` 正确补齐（覆盖线上报错的 `MapTasksAsync` 链路）；④ 断言 `GET /api/transcripts/settings`（`CreateSettingsController()`，先 seed 绑定该模型的设置）返回 200；⑤ 同时含 `IsDefault` 与其他未知字段的文档同样可读 | `tests/Z.Vtuber.Tests/TranscriptApiTests.cs`（改） | 红（当前反序列化抛 FormatException） |

### Phase 2: 实现（绿）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | 实体容错（止血）：`TranscriptModelRecord` 类声明追加 `[BsonIgnoreExtraElements]`（attributes 命名空间已 using，单行改动） | `servers/common/Persistence/Entities/TranscriptModelRecord.cs` | 1.2 转绿；既有转写测试无回归 |
| 2.2 | 清理迁移（清源）：`MongoDbInitializer` 新增——① 常量 `public const string TranscriptModelIsDefaultCleanupMigrationKey = "transcript_model_isdefault_cleanup_v1";`（置于既有迁移常量区域，:665-666 旁）；② `public async Task CleanupTranscriptModelIsDefaultAsync(CancellationToken ct = default)`：查 `zvtuber_meta` marker → 存在则记 Info 日志并 return（幂等短路）；否则 `database.GetCollection<BsonDocument>(MongoCollectionNames.TranscriptModels)`，filter `Builders<BsonDocument>.Filter.Exists("IsDefault", true)`，`UpdateManyAsync(filter, Builders<BsonDocument>.Update.Unset("IsDefault"), cancellationToken: ct)`，记 matched/modified 日志；`await MarkMigrationAppliedAsync(TranscriptModelIsDefaultCleanupMigrationKey, ct)`（复用 :658-663 upsert） | `servers/common/Persistence/MongoDbInitializer.cs` | 1.1 全部转绿 |
| 2.3 | 启动接线：① api `Program.cs:48` `EnsureIndexesAsync()` 后追加 `await initializer.CleanupTranscriptModelIsDefaultAsync();`；② consumer `Program.cs:83` 后同样追加（与 :85-86 既有迁移同 scope 串行）；`TranscriptModelMigration` 调用保持原样 | `servers/recording-transcript-api/Program.cs`、`servers/recording-transcript-consumer/Program.cs` | 编译通过；`dotnet build` 无警告 |

### Phase 3: 收口验证

| # | 任务 | 验收 |
|---|------|------|
| 3.1 | 全量测试回归：`dotnet test tests/Z.Vtuber.Tests` 全绿（含既有回归）；核心变更（迁移方法）覆盖率 ≥ 80% | 全部通过 |
| 3.2 | grep 核对：源码中无针对 `IsDefault` 的强类型残留引用（迁移的 BsonDocument 字面量除外）；`contracts/` 与 `apps/web/` 零改动（`git diff origin/main --stat` 核对改动面） | 核对通过 |
| 3.3 | 编排手工验证（z-aspire-orchestrator，可选但有条件就做）：本地 Aspire 拉起 → 向 `transcript_models` 手工插入含 `IsDefault` 文档（mongosh）→ `GET /api/transcripts/models` 返回 200 → 重启 api/consumer 后集合中 `IsDefault` 消失、`zvtuber_meta` 出现 `transcript_model_isdefault_cleanup_v1` marker | 手工验证记录（环境不可用时以 1.1/1.2 集成测试结论替代并在 PR 描述注明） |
| 3.4 | AGENTS.md 同步：`servers/common/AGENTS.md`（`TranscriptModelRecord` 已标注 `[BsonIgnoreExtraElements]`、`MongoDbInitializer` 含 `IsDefault` 清理迁移）；`servers/recording-transcript-api/AGENTS.md` 与 `servers/recording-transcript-consumer/AGENTS.md`（启动迁移串补充 `CleanupTranscriptModelIsDefaultAsync`） | 文档与实现一致 |

## 4. 并行策略

- 改动面小且集中（2 个测试文件、4 个源码文件，均在 common/api/consumer 三个子项目），**单线串行实施**，不拆 subagent：Phase 1（1.1 → 1.2）→ Phase 2（2.1 → 2.2 → 2.3）→ Phase 3 顺序执行。
- 2.1 与 2.2 互不依赖，但均为个位数行改动，串行成本可忽略；合并为一次实现提交或按 `fix:` 分两次提交均可，提交粒度以保持测试/实现分离（`test:` 先行）为准。
- 全部提交在需求总分支 `fix/transcript-model-isdefault`。

## 5. 验收对照

| PRD 功能点 | 实现任务 | 验证方式 |
|-----------|---------|---------|
| 功能点 1：实体反序列化容错（止血） | 2.1 | 1.2（存量文档 → 接口 200）+ 3.1 回归 |
| 功能点 2：存量字段清理迁移（清源） | 2.2、2.3 | 1.1（生效/幂等/短路/空集合/并发）+ 3.3 手工验证 |
| 功能点 3：回归防护 | 1.1、1.2 | 3.1 全量测试绿 |
| 非功能：无契约变更、部署不依赖启动顺序 | 2.1（api 独立恢复）+ 2.3（双边接线） | 3.2 改动面核对 + 提案 §10 验收清单 |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|------|------|
| 迁移并发执行 | api 与 consumer 可能同时启动执行迁移 | marker upsert（既有 `MarkMigrationAppliedAsync`）+ `Exists` filter + unset 本身幂等；1.1⑤ 并发用例锁定 |
| 迁移中断后重启 | unset 与 marker 之间进程崩溃 | unset 幂等，重启重跑无副作用（marker 未写则重跑清理）；PRD §3.5 已接受 |
| marker 短路导致漏清理 | 极端回滚场景：marker 已写但数据又出现 `IsDefault` | 与提案决策一致：marker 短路为既定语义；若发生可手工删 marker 重跑，概率极低（字段只能由旧代码写入，而旧代码不含本迁移） |
| 其他实体同类隐患 | 本次只修 `TranscriptModelRecord` | 提案 §5.3 已明确全量普查另立技术债事项，不阻塞本修复 |
| 依赖 | `MongoDB.Driver` 3.9.0 既有能力（Exists/Unset/ReplaceOne upsert）；Testcontainers MongoDB/Redis fixture 既有 | 版本与现状已核实（§1.0） |

## 7. 相关文档

- PRD：`docs/product/draft/20260917-transcript-model-isdefault-fix-prd.md`（v1.0）
- 技术提案：`docs/engineering/proposals/20260917-proposal-transcript-model-isdefault-fix.md`
- 引入问题的 Plan：[[20260917-plan-transcript-model-context]]（删除 `IsDefault` 字段的来源）
- 转写功能 Plan：[[20260916-plan-recording-transcript]]
- 需求总 PR：<https://github.com/Deliay/z-vtuber/pull/117>
- 开发规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`、`servers/common/AGENTS.md`、`servers/recording-transcript-api/AGENTS.md`、`servers/recording-transcript-consumer/AGENTS.md`
