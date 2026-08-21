---
description: integration 分支验收报告（第四轮）——第三轮 P2 混合重跑测试/P4 修正均已落实，但验收实测全量测试 295/296 通过，新增用例 CooldownCookie_BecomesAvailableAfterExpiry 为零余量时序竞态（flaky），全量负载下可复现失败
type: Permanent
---

# Integration 验收报告（第四轮）：PR #37 修复复核

- **验收日期**: 2026-08-21
- **PR**: https://github.com/Deliay/z-vtuber/pull/37 （integration → release）
- **Review 范围**: `release..integration` 代码差距 + 第三轮验收（`20260821-acceptance-integration-pr37-r3.md`）打回后的工作区修复（未提交修改）
- **验收结论**: **不通过**（第三轮 P2/P4 修复项全部正确落实，但验收方亲自运行全量测试 295/296——新增的 `CooldownCookie_BecomesAvailableAfterExpiry` 用例存在零余量时序竞态，全量运行实测失败 1 次，单独重跑 3 次全过，确认为 flaky 测试，必须修复）

## 一、验证结果

- **构建/测试**: 验收方亲自运行 `dotnet test`：**Failed 1, Passed 295, Total 296**——`CookiePoolServiceTests.CooldownCookie_BecomesAvailableAfterExpiry` 失败（`Expected: "cookie-a", Actual: "cookie-b"`，`CookiePoolServiceTests.cs:98`）。随后单独 `--filter` 重跑该用例 3 次全部通过，确认为负载相关的时序 flaky，而非稳定失败。
- **修复真实性**: 修复落在工作区未提交修改中，diff 与第三轮返工报告一一对应，无夹带无关改动。

## 二、第三轮问题逐项复核

| 修复项 | 判定 | 证据 |
|---|---|---|
| P2 混合重跑测试（预置 danmaku + 无 Type 旧记录） | ✅ 已补齐 | `SubscriptionTypeSplitMigrationTests.cs:137-169` 新增 `Migrate_PreservesExistingDanmakuId_WhenDanmakuRecordAlreadyExists`：预置 Mid=2001 danmaku 记录（已知 ObjectId）+ 同 Mid 无 Type 旧记录，迁移后断言不抛异常、`danmaku2001.Id == existingDanmakuId`。该用例真实覆盖 `MongoDbInitializer.cs:609` `existingDanmakuDict.TryGetValue` true 分支——若回退为 `GenerateNewId()`，ReplaceOne 命中已有文档会触发 immutable `_id`（code 66）或 `_id` 断言失败，回归保护有效。给定 (Mid,Type) 复合唯一索引，Dynamic+Danmaku 均非空即等价于「恰有两条」，未显式断言总数不构成缺口 |
| P4 用例改名 | ✅ 已修正 | `SubscriptionTypeSplitMigrationTests.cs:120` 已改为 `Migrate_MarkerWithUnmigratedRecords_ExecutesMigration`，与反转后的断言语义一致 |
| P4 BilibiliAccountProviderTests 精简 | ✅ 已修正 | 4→2 用例（`CreatesProviderWithCorrectOptions` + `AddBilibiliCrawlers_DoesNotRegisterDefaultHttpClient`），`CreateProviderWithMockHttpClientAsync` 未使用的 `provider` 形参已删除，测试数 297→296 与返工报告一致 |
| 历史项抽检 | ✅ 无回退 | `Program.cs`/`MongoDbFixture.cs` 仍无 camelCase Bson 约定注册；openapi `MinPrice` 描述（:290）与端点描述（:188）一致；启动顺序「删旧 Mid 索引 → 迁移 → 建复合唯一索引」（`Program.cs:80-86`）正确 |

## 三、本轮发现的问题

### P2 - 必修复：`CooldownCookie_BecomesAvailableAfterExpiry` 为零余量时序竞态测试（flaky）

`tests/Z.Vtuber.Tests/CookiePoolServiceTests.cs:85-99`（本 PR 新增用例）：

```csharp
await service.MarkCooldownAsync("cookie-a", TimeSpan.FromSeconds(1)); // CooldownUntil = UtcNow + 1s
...
await Task.Delay(TimeSpan.FromSeconds(1));                             // 恰好等 1s，零余量
var after = await service.NextAsync();                                 // 过滤条件 CooldownUntil <= now
```

失败机理：`MarkCooldownAsync` 写入 `CooldownUntil = DateTime.UtcNow + 1s`（BSON DateTime 仅毫秒精度，存在舍入）；`Task.Delay(1s)` 不保证恰好/超过 1s 才恢复，全量测试负载下线程调度抖动可能使 `now` 略早于 `CooldownUntil`，cookie-a 仍被过滤，round-robin 落到 cookie-b 上。验收实测：全量运行失败（expected cookie-a / actual cookie-b），隔离重跑 3 次全过——典型 flaky。该用例进入主干后会使 CI 随机变红，其破坏性不亚于功能 bug。

- **要求**：为时间断言留足余量，例如 `MarkCooldownAsync(cookie-a, 1s)` 后 `Task.Delay(2s)`（或 cooldown 100ms + delay 1s），或改为轮询「cookie-a 恢复可用」直到超时。修复后请全量运行测试确认 296/296。

### P4 - 次要（不阻断，可顺带）

- `ResilientBiliApiClient.cs:167` `ReportRiskControl` 由 `_ =` 丢弃改为 `async void`：虽有 try/catch 兜底，但 `async void` 在非事件处理器场景属于反模式（无法被调用方观察、测试困难），建议保持 `async Task` + 调用处 `_ = ReportRiskControlAsync(...)` 或 `Task.Run` 语义明确的写法。

## 四、结论

第三轮 P2 必修复项（混合重跑测试）与两项 P4 修正均已正确落实，`_id` 复用分支已有真实回归保护，功能层面无已知问题。但本轮验收实测全量测试 295/296，新增的冷却过期用例为零余量时序竞态，**验收不通过，打回修复**。差距极小：给该用例加时间余量（顺带评估 `async void` 写法）并全量复跑即可通过。
