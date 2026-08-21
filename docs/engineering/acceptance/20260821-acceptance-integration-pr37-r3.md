---
description: integration 分支验收报告（第三轮）——第二轮 P1/P2/P3 修复复核，确认全部功能性修复落实，但第二轮 P2 必修复项要求的「预置 danmaku + 无 Type 旧记录混合」重跑测试未补，_id 复用分支无回归保护
type: Permanent
---

# Integration 验收报告（第三轮）：PR #37 修复复核

- **验收日期**: 2026-08-21
- **PR**: https://github.com/Deliay/z-vtuber/pull/37 （integration → release）
- **Review 范围**: `release..integration` 代码差距 + 第二轮验收（`20260821-acceptance-integration-pr37-r2.md`）打回后的工作区修复（未提交修改）
- **验收结论**: **不通过**（全部功能性问题已正确修复、测试 297/297 全绿，但第二轮 P2 必修复项明确要求的混合重跑测试未补——danmaku `_id` 复用分支在所有测试中从未被走到，该路径正是连续两轮暴露生产启动失败 bug 的位置，必须补回归保护）

## 一、验证结果

- **构建/测试**: `dotnet test` **297/297 全部通过**（较第二轮 296 新增 1 个 BilibiliAccountProviderTests 用例），本轮由验收方亲自运行确认。
- **修复真实性**: 修复落在工作区未提交修改中，diff 与修复报告一一对应，无夹带无关改动。

## 二、第二轮问题逐项复核

| 修复项 | 判定 | 证据 |
|---|---|---|
| P1 camelCase Bson 约定回退 | ✅ 已修复 | `Program.cs` 全文无 `ConventionRegistry`/`CamelCaseElementNameConvention`（残留 camelCase 仅为 `System.Text.Json` API 输出命名策略，与 Bson 无关）；`MongoDbFixture.cs` 亦无注册。生产/测试序列化口径已一致，存量 PascalCase 数据不受影响，`SpaceTimelineRepository.cs:61` 原生管道 `"$DynamicId"` 恢复有效 |
| P2-1 danmaku upsert 复用 `_id`（代码） | ✅ 已修复 | `MongoDbInitializer.cs:585-590` 批内一次预查 `Mid in batch && Type == Danmaku` 建 `existingDanmakuDict`，:607 `TryGetValue` 命中则复用 `_id`、未命中才 `GenerateNewId()`，与 sender/TextIndex 两个回填模式一致。逻辑经逐行检查正确（部分回滚重跑场景 ReplaceOne 命中已有文档时 `_id` 相同，不再触发 code 66） |
| P2-1 要求的混合重跑测试 | ❌ **未补** | 第二轮打回原文：「补『预置 danmaku 记录 + 无 Type 旧记录混合』的重跑测试」。现有 9 个迁移用例中无任何用例在迁移前预置已存在的 danmaku 记录——`existingDanmakuDict.TryGetValue` 的 **true 分支（复用 `_id`）在所有测试中从未被执行**，修复无回归保护。下次有人把 :607 改回 `GenerateNewId()`，297 个测试照样全绿 |
| P3-1 BilibiliAccountProviderTests 重写 | ⚠️ 有改进但仍未测生产代码 | 新用例确实调用了 `AddBilibiliCrawlers(addHttpClient: false)` 并断言单 HttpClient + 超时（`BilibiliAccountProviderTests.cs:99-107`），钉住了库行为；但 `CreateProviderWithMockHttpClientAsync(provider, ...)` 的 `provider` 形参从未使用，测试自行复刻 DI 配置而非调用 `BilibiliAccountProvider.BuildProviderAsync`——若生产代码回退为 `AddBilibiliCrawlers()`（不传 false），测试仍全绿 |
| P3-2 openapi MinPrice 描述 | ✅ 已修复 | `contracts/bilibili-crawler.openapi.yaml:290` 已同步为「仅作用于 gift/gift_v2 礼物类事件」，与端点描述（:188）一致 |

## 三、本轮发现的问题

### P2 - 必修复项不完整：混合重跑场景测试缺失，`_id` 复用分支零覆盖

第二轮 P2-1 的修复要求包含两部分：代码复用 `_id`（已完成）+ 补「预置 danmaku 记录 + 无 Type 旧记录混合」的重跑测试（**未做**）。该测试不是锦上添花——订阅迁移路径连续两轮（首轮 P2-7、第二轮 P2-1）在同一失败模式（immutable `_id` code 66 → BulkWrite 抛异常 → 启动失败）上翻车，原因都是该路径无测试覆盖。当前修复再次处于「逻辑正确但无回归保护」状态。

- **要求**：新增一个用例——预置某 Mid 的 danmaku 记录（有 Type）+ 同 Mid 的无 Type 旧记录，执行 `MigrateSubscriptionTypeSplitAsync`，断言：不抛异常、danmaku 记录 `_id` 保持不变、最终该 Mid 恰有 Dynamic + Danmaku 两条记录。

### P4 - 次要

- `SubscriptionTypeSplitMigrationTests.cs:120` 用例名 `Migrate_Marker_PreventsRerun` 与断言矛盾（第二轮已反转语义为「marker 存在但有未迁移记录时仍会执行」），建议改名为 `Migrate_Marker_WithUnmigratedRecords_StillRuns` 之类；
- P3-1 残留（见上表）：建议用例真正调用 `GetProviderAsync`（无效 cookie + 容忍初始化失败），从返回的容器解析 `HttpClient` 断言 handler 链，或至少删除未使用的 `provider` 形参以免误导读者的测试意图。

## 四、结论

第二轮的 P1（camelCase 约定回退）、P2-1 代码修复（`_id` 复用）、P3-2（契约描述）均已正确落实，测试 297/297 全绿，功能层面已无已知问题。但第二轮 P2 必修复项要求的混合重跑测试未补，`_id` 复用分支零覆盖，**验收不通过，打回修复**。差距极小：补一个混合重跑用例（顺带修正误导性用例名）即可通过。
