---
description: integration 分支验收报告（第五轮）——第四轮 P2 flaky 用例与 P4 async void 均已正确修复，验收方亲自运行全量测试两轮 296/296 全绿，验收通过
type: Permanent
---

# Integration 验收报告（第五轮）：PR #37 修复复核

- **验收日期**: 2026-08-21
- **PR**: https://github.com/Deliay/z-vtuber/pull/37 （integration → release）
- **Review 范围**: `integration..release` 代码差距 + 第四轮验收（`20260821-acceptance-integration-pr37-r4.md`）打回后的工作区修复（未提交修改）
- **验收结论**: **通过**（第四轮 P2 必修复项与 P4 建议项全部正确落实，验收方亲自运行全量测试两轮均 296/296 通过，历史项无回退）

## 一、验证结果

- **构建/测试**: 验收方亲自运行 `dotnet test tests/Z.Vtuber.Tests` 两轮（含 `--no-build` 复跑）：均 **Failed 0, Passed 296, Total 296**。上一轮 flaky 的 `CookiePoolServiceTests.CooldownCookie_BecomesAvailableAfterExpiry` 在全量负载下连续两轮通过。
- **修复真实性**: 修复落在工作区未提交修改中，diff 与第四轮返工报告一一对应，无夹带无关改动。

## 二、第四轮问题逐项复核

| 修复项 | 判定 | 证据 |
|---|---|---|
| P2 flaky 用例加时间余量 | ✅ 已修复 | `CookiePoolServiceTests.cs:91-96`：`MarkCooldownAsync(cookie-a, 1s)` + `Task.Delay(2s)`，并附注释说明 BSON 毫秒精度 + 调度抖动竞态成因。余量由 0 提升至 ~1s，竞态窗口消除；全量复跑两轮通过 |
| P4 `async void` 反模式 | ✅ 已修复 | `ResilientBiliApiClient.cs:167` 恢复为 `async Task ReportRiskControl`；4 处调用点（:79, :94, :130, :162）均为 `_ = ReportRiskControl(...)` 显式丢弃，保持 fire-and-forget 语义；全仓库 `servers/api` 下 grep 无残留 `async void` |
| 历史项抽检 | ✅ 无回退 | 启动顺序「删旧 Mid 索引 → 迁移 → 建复合唯一索引」（`Program.cs:78-89`）正确；`MongoDbInitializer.cs:609` danmaku `_id` 复用分支在位；混合重跑用例 `Migrate_PreservesExistingDanmakuId_WhenDanmakuRecordAlreadyExists`（`SubscriptionTypeSplitMigrationTests.cs:137-169`）保持；openapi `MinPrice` 参数描述（:286-289）与端点描述（:188）一致；无 camelCase Bson 约定注册 |

## 三、本轮新增检查

- 全量扫描测试套件中所有 `Task.Delay` 使用点（`CookiePoolServiceTests.cs:96`、`ResilientBiliApiClientTests.cs:191`、`CrawlLockProviderTests.cs:23,48`、`BilibiliLiveControllerTests.cs:401`），无其他零余量时序断言。

## 四、结论

第四轮打回项全部修复到位，全量测试两轮 296/296 稳定通过，历史验收项无回退，**验收通过**。
