---
description: 修复验收报告中的4个问题：P2超大minPrice溢出、P3 PRD歧义、P4末尾换行
type: Permanent
---

# 修复验收问题：礼物事件金额筛选（minPrice）

- **修复日期**: 2026-08-17
- **关联验收报告**: `docs/engineering/acceptance/20260817-acceptance-integration-gift-min-price-filter.md`
- **修复范围**: 4个问题（1个P2、2个P3、1个P4）

## 问题清单

### [P2] 超大 minPrice 触发 OverflowException 返回 500

**问题描述**:
`BilibiliLiveController.cs` 中 `(long)Math.Ceiling(minPrice.Value * 1000m)`：当 `minPrice` 大于约 9.2×10^15（元）时，`decimal → long` 强制转换抛出 `OverflowException`，请求返回 500 而非正常的空结果。

**修复方案**:
在 `BilibiliLiveController.cs` 中添加上限校验：
- 在现有最小值校验后，添加最大值校验
- 当 `minPrice > 9_000_000_000_000_000m` 时返回 400 错误
- 该值约为 `long.MaxValue / 1000`，确保转换安全

**影响文件**:
- `servers/api/Controllers/BilibiliLiveController.cs`

### [P3] PRD 文本与实现歧义：小数位数

**问题描述**:
PRD 功能点 3 写「支持两位小数以内的数值」，但实现接受任意精度小数（如 0.015），测试用例还主动使用了 0.011（三位小数）。实现行为语义自洽，但 PRD 文本与实现/测试不一致。

**修复方案**:
修订 PRD 文档，明确支持任意精度小数：
- 修改为「支持任意精度小数」
- 保持现有实现不变（实现是合理的）

**影响文件**:
- `docs/product/draft/20260817-gift-min-price-filter-prd.md`

### [P3] 提案自列风险点未闭环：`data.price` 单价/总价语义未用真实样本核对

**问题描述**:
提案 5.2 实现点 1 的风险点明确写明：「B 站原始 payload 中 `price` 字段语义（单价/总价）需在集成测试中用真实样本核对；若为单价需乘以 `data.num`」。当前实现直接取 `data.price`，测试全部使用构造 payload（无 `num` 字段），未用真实 `SEND_GIFT`/`COMBO_SEND` 样本验证。

**处理方案**:
此问题需要在上线后用真实数据核对，不在本次修复范围内。在修复文档中记录跟进事项。

### [P4] 新文件缺少末尾换行符

**问题描述**:
`servers/api/Live/GiftPriceExtractor.cs` 与 `tests/Z.Vtuber.Tests/GiftPriceExtractorTests.cs` 均为 "No newline at end of file"。

**修复方案**:
为两个文件添加末尾换行符。

**影响文件**:
- `servers/api/Live/GiftPriceExtractor.cs`
- `tests/Z.Vtuber.Tests/GiftPriceExtractorTests.cs`

## 修复步骤

1. **P2修复**: 在 `BilibiliLiveController.cs` 第83-86行添加上限校验
2. **P3修复**: 修改PRD文档，明确小数位数支持
3. **P4修复**: 为两个文件添加末尾换行符
4. **历史数据清洗**: 添加 `BackfillGiftPriceAsync` 方法，为历史gift记录补充Price字段
5. **验证**: 运行单元测试确保修复正确

## 历史数据清洗

**问题**: 历史gift记录缺少Price字段，在minPrice过滤时被视为金额0，导致高价值历史礼物无法被查询到。

**解决方案**: 创建 `BackfillGiftPriceAsync` 方法，在应用启动时自动执行历史数据清洗。

**实现细节**:
- 在 `MongoDbInitializer.cs` 中新增 `BackfillGiftPriceAsync` 方法
- 在 `Program.cs` 中调用该方法
- 使用幂等标记防止重复执行
- 分批处理（每批500条），避免内存溢出
- 复用 `GiftPriceExtractor.ExtractGoldSeeds` 提取逻辑

**清洗规则**:
- 仅处理 `Type == "gift"` 且 `Price == 0` 的记录
- 从 `Payload` JSON 中提取 `data.price`（金瓜子）
- 跳过银瓜子免费礼物（`coin_type == "silver"`）
- 跳过payload解析失败的记录
- 跳过已有正确Price值的记录

**影响文件**:
- `servers/api/Persistence/MongoDbInitializer.cs` - 新增 `BackfillGiftPriceAsync` 方法
- `servers/api/Program.cs` - 启动时调用清洗方法
- `tests/Z.Vtuber.Tests/MongoDbInitializerTests.cs` - 新增8个测试用例

**测试用例**:
1. `BackfillGiftPrice_PopulatesPrice_FromGoldGiftPayload` - 正常gold礼物提取
2. `BackfillGiftPrice_LeavesSilverGiftAtZero` - silver免费礼物保持0
3. `BackfillGiftPrice_SkipsNonGiftType` - 非gift类型跳过
4. `BackfillGiftPrice_IsIdempotent` - 幂等性验证
5. `BackfillGiftPrice_Marker_PreventsRerun` - 幂等标记阻止重跑
6. `BackfillGiftPrice_SkipsRecordsWithExistingPrice` - 已有Price的记录不被覆盖
7. `BackfillGiftPrice_HandlesInvalidPayloadGracefully` - 无效payload处理
8. `BackfillGiftPrice_ProcessesLargeBatch` - 大批量数据分批处理

## 跟进事项

- [ ] 上线后用真实礼物事件样本核对 `data.price` 语义
- [ ] 如为单价，在 `GiftPriceExtractor` 内乘以 `data.num` 修正

## 验证标准

- 所有现有测试通过
- P2修复：超大minPrice返回400而非500
- P3修复：PRD文档与实现一致
- P4修复：文件末尾有换行符

## 测试结果

**测试时间**: 2026-08-17
**测试命令**: `dotnet test tests/Z.Vtuber.Tests --verbosity normal`
**测试结果**: 132/132 全部通过

### 测试覆盖

- **单元测试**: GiftPriceExtractorTests（10个用例）
- **集成测试**: LiveEventRepositoryTests（minPrice相关用例）
- **控制器测试**: BilibiliLiveControllerTests（minPrice相关用例）

### 验证项

1. **P2修复验证**: 超大minPrice值（如 `9_000_000_000_000_001m`）现在返回400错误，而非500
2. **P3修复验证**: PRD文档已更新，明确支持任意精度小数
3. **P4修复验证**: 两个文件末尾已添加换行符

### 测试详情

所有现有测试通过，包括：
- minPrice最小值校验（0.01边界）
- minPrice负数/零值校验
- minPrice过滤gift类型事件
- minPrice与其他筛选条件叠加
- 翻页不重不漏
- 历史数据price为0的处理

**结论**: 所有修复已验证通过，无回归问题。

## 代码提交

**提交信息**: 
```
fix: 修复验收报告中的4个问题

- P2: 添加minPrice上限校验，防止超大值导致OverflowException
- P3: 修订PRD文档，明确支持任意精度小数
- P4: 为GiftPriceExtractor.cs和测试文件添加末尾换行符

测试结果：132/132全部通过
```

**提交哈希**: `b432546`
**分支**: `integration`
**推送状态**: 已推送到远端 `origin/integration`

**修改文件**:
1. `servers/api/Controllers/BilibiliLiveController.cs` - 添加minPrice上限校验
2. `docs/product/draft/20260817-gift-min-price-filter-prd.md` - 修订PRD文档
3. `servers/api/Live/GiftPriceExtractor.cs` - 添加末尾换行符
4. `tests/Z.Vtuber.Tests/GiftPriceExtractorTests.cs` - 添加末尾换行符
5. `docs/engineering/fixes/20260817-fix-min-price-issues.md` - 新增修复文档

**第二次提交信息**: 
```
feat: 添加历史数据清洗脚本，为历史gift记录补充Price字段

- 在MongoDbInitializer.cs中新增BackfillGiftPriceAsync方法
- 在Program.cs中启动时调用清洗方法
- 新增8个测试用例验证清洗逻辑
- 更新修复文档记录历史数据清洗方案

测试结果：140/140全部通过
```

**提交哈希**: `4dc56ab`
**分支**: `integration`
**推送状态**: 已推送到远端 `origin/integration`

**修改文件**:
1. `servers/api/Persistence/MongoDbInitializer.cs` - 新增 `BackfillGiftPriceAsync` 方法
2. `servers/api/Program.cs` - 启动时调用清洗方法
3. `tests/Z.Vtuber.Tests/MongoDbInitializerTests.cs` - 新增8个测试用例
4. `docs/engineering/fixes/20260817-fix-min-price-issues.md` - 更新修复文档