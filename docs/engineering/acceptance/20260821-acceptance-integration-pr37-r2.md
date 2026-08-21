---
description: integration 分支验收报告（第二轮）——首轮 P1/P2 修复复核，确认大部分修复落实，但修复引入 1 项新 P1（camelCase Bson 约定）且 P2-4 upsert 修复不完整
type: Permanent
---

# Integration 验收报告（第二轮）：PR #37 修复复核

- **验收日期**: 2026-08-21
- **PR**: https://github.com/Deliay/z-vtuber/pull/37 （integration → release）
- **Review 范围**: `release..integration` 代码差距 + 首轮验收（`20260821-acceptance-integration-pr37.md`）打回后的工作区修复（未提交修改）
- **验收结论**: **不通过**（修复引入 1 项新 P1：camelCase Bson 约定目标未达成且破坏存量数据与原生聚合；1 项 P2 修复不完整：订阅迁移 danmaku upsert 在部分回滚重跑场景必现 code 66 启动失败。其余修复均已落实，测试 296/296 全绿）

## 一、验证结果

- **构建/测试**: `dotnet test` **296/296 全部通过**（含 Testcontainers Mongo 集成测试，较首轮 288 新增 8 个用例）。
- **修复真实性**: 全部修复落在工作区未提交修改中（`git status` 14 个修改文件 + 3 个新增文件），diff 范围与修复报告一一对应，无夹带无关改动。

## 二、首轮问题逐项复核

| 修复项 | 判定 | 证据 |
|---|---|---|
| P1 BilibiliAccountProvider DI 覆盖 | ✅ 生产代码已修复 | `BilibiliAccountProvider.cs:35` 改 `AddBilibiliCrawlers(addHttpClient: false)`；反编译 mikibot.crawler 1.0.9 确认传 false 后不注册默认 HttpClient，crawler 解析到 :30 带 `RiskControlHandler` 的单例。**但新增的 `BilibiliAccountProviderTests.cs` 三个用例全部只 `Assert.NotNull(provider)`，从不调用 `GetProviderAsync`，属空转测试，P1 仍无回归保护（见 P3-1）** |
| P2-1 CookieProbeJob 异常分类 | ✅ 已修复 | `CookieProbeJob.cs:39-53` OCE 重抛 → RiskControl→冷却 → 其余→离线；`CookieProbeJobTests.cs` 覆盖全部 5 个分支，断言真实调用记录 |
| P2-2 配置绑定失效 | ✅ 已修复 | `Program.cs:29-35` `AddOptions<BilibiliCrawlerOptions>().Bind(...).ValidateOnStart()`，具体类型单例从 `IOptions.Value` 解析 |
| P2-3 拟真通道 wbi 签名 | ✅ 已修复 | `BrowserishBiliApiClient.cs:111-116,138-151` 注入 `IBilibiliAccountProvider`，复用 `BilibiliAccount.Sign`（反编译确认走 `WbiSigner.SignQueryParameters`，产出 `wts`/`w_rid`） |
| P2-4 danmaku 补插 upsert | ⚠️ 修复不完整 | 已改 `ReplaceOneModel(IsUpsert=true)`（`MongoDbInitializer.cs:597-605`），但 replacement 每次 `Id = ObjectId.GenerateNewId()`，见 P2-1（本轮） |
| P2-5 marker 唯一跳过依据 | ✅ 已修复 | `MongoDbInitializer.cs:559-568` marker 存在时仍查未迁移记录；`SubscriptionTypeSplitMigrationTests.cs:125-134` 已反转旧断言 |
| P2-6 sender 回填 N+1 | ✅ 已修复 | `MongoDbInitializer.cs:490-495` 批内一次 `Find(EventId in batchIds)` 建字典 |
| P2-7 TextIndex 回填 _id 覆盖 | ✅ 已修复 | `MongoDbInitializer.cs:384-402` 批量预查复用现有 `_id` |
| P3 契约 minPrice 描述 | ⚠️ 不彻底 | 端点描述已改（yaml:188），但 `MinPrice` 参数自身描述（yaml:290）仍写「仅作用于 gift 类型事件」 |
| P3 Controller 拒绝数字 type | ✅ 已修复 | `BilibiliSubscriptionController.cs:33-36,57-60` `type.All(char.IsDigit)` → 400 |
| P3 枚举落库 camelCase | ❌ 修复引入新问题 | 见 P1（本轮） |
| P3 gift_v2 Parse 保护 | ✅ 已修复（测试未补） | `DanmakuEventNormalizer.cs:62-81` try/catch 降级为原始 payload 事件；但 `DanmakuEventNormalizerTests.cs` 仍无 gift_v2/Price 用例 |
| P3 AGENTS.md 同步 | ✅ 已修复 | 集合计数与迁移键已更新 |
| P4 杂项 | ✅ 已修复 | 死参数移除、`ReportRiskControl` async void + try/catch（await 后异常已兜住，可接受）、`Random.Shared`、文件末尾换行 |

## 三、本轮发现的问题

### P1 - 新引入：camelCase Bson 约定目标未达成，且破坏存量数据与原生聚合管道

`Program.cs:13-15` 注册 `CamelCaseElementNameConvention`，修复意图是「枚举落库为小写」，但：

1. **目标未达成**：该约定只改**元素名**（`Mid`→`mid`），枚举值经 `[BsonRepresentation(BsonType.String)]` 仍落库为 `"Danmaku"/"Dynamic"` PascalCase。:13 注释「确保枚举落库为小写」是错的。
2. **破坏存量数据**：库中存量文档元素名全是 PascalCase，进程内读写突然变 camelCase——驱动按约定生成的查询/更新用 `{"mid": ...}` 匹配不到存量 `{"Mid": ...}` 文档，新旧数据互不匹配。首轮验收给出的另一半建议（bump 迁移键清洗存量）未做。
3. **打废原生聚合**：`SpaceTimelineRepository.cs:61` 原生管道写死 `"$DynamicId"`，约定生效后新写入的字段是 `dynamicId`，`$convert` 恒为 null，空间动态按 ID 排序**静默失效**（无报错、结果错序）。
4. **测试抓不住**：集成测试（`MongoDbFixture`）不注册该约定，测试环境 PascalCase、生产 camelCase，296 全绿无法暴露以上任何问题。

- **建议（二选一）**：
  a. **回退该约定**（推荐，最小改动）：删除 `Program.cs:13-15`，维持 PascalCase 元素名现状，P3 原始诉求（枚举小写落库）改为修订提案/Plan 文档口径，或仅对枚举追加 camelCase 序列化器（不动元素名）；
  b. 若坚持 camelCase：补枚举序列化处理 + 存量元素名清洗迁移 + `SpaceTimelineRepository.cs:61` 原生管道字段名同步 + 集成测试注册同一约定。
  无论选哪条，都必须消除「生产/测试序列化口径不一致」。

### P2-1 - 修复不完整：订阅迁移 danmaku upsert 未复用已有记录 `_id`，部分回滚重跑必现 code 66 启动失败

`MongoDbInitializer.cs:597-605`：`ReplaceOneModel` 的 replacement 每次 `Id = ObjectId.GenerateNewId()`（:601）。在「上次迁移部分完成 + 回滚/重新导入无 Type 旧数据」场景（正是 P2-5 修复后允许重跑的场景），库中已存在该 Mid 的 danmaku 记录，ReplaceOne 命中已有文档且 `_id` 不同 → MongoDB 报 immutable `_id`（code 66）→ BulkWrite 抛异常 → **启动失败**。这与首轮 P2-7 专门修掉的失败模式同类，此处复刻。且该路径无测试覆盖（现有回滚测试只预置无 Type 旧记录，未预置已存在的 danmaku 记录）。

- **建议**：与两个回填一致——批量查出已存在 danmaku 记录复用其 `_id`（不存在才 GenerateNewId）；补「预置 danmaku 记录 + 无 Type 旧记录混合」的重跑测试。

### P3-1 - BilibiliAccountProviderTests 空转，P1 修复无回归保护

`tests/Z.Vtuber.Tests/BilibiliAccountProviderTests.cs` 三个用例全部只 `Assert.NotNull(provider)`，从不调用 `GetProviderAsync`（注释自述"不调用以避免真实 API 调用"）。下次有人改回 `AddBilibiliCrawlers()` 测试照样全绿。

- **建议**：至少一个用例调用 `GetProviderAsync`（可用无效 cookie + 极短超时容忍 `InitializeAsync` 失败，或用 InternalsVisibleTo/反射解析内部 provider），从容器中解析 crawler 依赖的 `HttpClient`，断言其 handler 链含 `RiskControlHandler`。

### P3-2 - 次要遗留

- openapi `MinPrice` 参数自身描述（yaml:290）未同步为 gift/gift_v2；
- `DanmakuEventNormalizerTests.cs` 仍无 gift_v2 解析成功/失败用例；
- `BrowserishBiliApiClientTests` Nav 用例未断言 `w_rid`/`wts` 存在（功能可信，断言偏弱）；
- `ResilientBiliApiClientTests.cs:190` 用 `Task.Delay(100)` 等待 async void，存在时序脆弱性。

## 四、结论

首轮 1 项 P1 + 7 项 P2 中，6 项 P2 及绝大部分 P3/P4 已正确修复，测试 296/296 全绿。但本轮修复引入新的 P1（camelCase Bson 约定），且 P2-4 修复不完整（danmaku upsert `_id` 问题），两项均可导致生产启动失败或数据错乱，**验收不通过，打回修复**。修复范围明确、改动量小，完成 P1（建议回退约定）与 P2-1（复用 `_id`）后即可通过。
