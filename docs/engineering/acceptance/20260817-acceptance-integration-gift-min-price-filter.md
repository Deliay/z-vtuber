---
description: integration 分支验收报告——礼物事件金额筛选（minPrice）功能合入 release 前的代码 Review 与测试验证结果
type: Permanent
---

# Integration 验收报告：礼物事件金额筛选（minPrice）

- **验收日期**: 2026-08-17
- **PR**: https://github.com/Deliay/z-vtuber/pull/18 （integration → release）
- **Review 范围**: `release..integration` 代码差距（11 commits，12 个文件，+908/-5）
- **关联 PRD**: `docs/product/draft/20260817-gift-min-price-filter-prd.md`
- **关联提案**: `docs/engineering/proposals/20260817-proposal-gift-min-price-filter.md`
- **验收结论**: **通过**（附 4 项问题/跟进项，均不阻塞合入）

## 一、变更内容概述

`GET /api/b/live/{roomId}/event/range` 新增可选参数 `minPrice`（元，最小 0.01），仅对 `gift` 类型事件按金额下限过滤：

- `contracts/bilibili-crawler.openapi.yaml`：新增 `MinPrice` 查询参数契约。
- `LiveEventRecord` 新增 `Price` 字段（long，金瓜子）；`LiveEventSource` 落库时经 `GiftPriceExtractor` 从 payload 提取金额。
- `LiveEventRepository.GetPageAsync` 叠加 `(Type != gift) OR (Price >= threshold)` 过滤。
- `BilibiliLiveController` 接收/校验 `minPrice`，`Ceiling(minPrice × 1000)` 换算为金瓜子。
- 测试：新增 `GiftPriceExtractorTests`（10 例）+ 控制器与仓储层 minPrice 用例（15 例）。

## 二、验证结果

- **测试**: `dotnet test tests/Z.Vtuber.Tests` 在 integration（fcb855a）上 **132/132 全部通过**（含 Testcontainers Mongo 集成测试）。
- **契约一致性**: 参数命名 camelCase `minPrice`、最小 0.01、`< 0.01 或非数值 → 400`、仅作用于 gift 类型，与 OpenAPI 契约及 PRD 一致。
- **PRD 验收点逐条核对**:
  - 含边界过滤 ✓（`Gte`，测试覆盖 0.011 元 = 11 金瓜子边界）
  - 与类型/时间筛选叠加、`total` 为筛选后真实条数 ✓（Count 与 Find 共用过滤条件，有测试）
  - 翻页不重不漏 ✓（`GetPage_MinPrice_PaginateWithoutOverlapOrGap`）
  - 不传 `minPrice` 行为与现状一致 ✓（有回归测试）
  - 免费礼物（silver）与历史缺金额记录视为 0 ✓（提取器逻辑 + 测试覆盖）
  - 非 gift 类型不受 minPrice 影响 ✓（`Ne(Type,"gift") | Gte(Price,...)`，有测试）
  - 非数值 → 400 ✓（`[ApiController]` 模型绑定失败自动 400）
- **提案决策核对**: 金瓜子存储、不落库回填、不新增索引、落库时提取等决策均已按提案执行。

## 三、发现的问题

### P2 - 超大 minPrice 触发 OverflowException 返回 500

`BilibiliLiveController.cs` 中 `(long)Math.Ceiling(minPrice.Value * 1000m)`：当 `minPrice` 大于约 9.2×10^15（元）时，`decimal → long` 强制转换抛出 `OverflowException`，请求返回 500 而非正常的空结果。OpenAPI 契约只声明了 `minimum: 0.01`，未声明 `maximum`，按契约该输入合法。

- **影响**: 极低（正常业务不会传入如此值），但属于未处理异常路径。
- **建议**: 换算前加上限校验（如 `minPrice > 9_000_000_000_000_000m → 400`），或契约补充 `maximum`。

### P3 - PRD 文本与实现存在歧义：小数位数

PRD 功能点 3 写「支持两位小数以内的数值」，但实现接受任意精度小数（如 0.015），测试用例还主动使用了 0.011（三位小数）。实现行为语义自洽（换算为金瓜子后含边界过滤），但 PRD 文本与实现/测试不一致。

- **建议**: 澄清口径——若允许任意精度，修订 PRD 文本；若限定两位小数，需补校验。

### P3 - 提案自列风险点未闭环：`data.price` 单价/总价语义未用真实样本核对

提案 5.2 实现点 1 的风险点明确写明：「B 站原始 payload 中 `price` 字段语义（单价/总价）需在集成测试中用真实样本核对；若为单价需乘以 `data.num`」。当前实现直接取 `data.price`，测试全部使用构造 payload（无 `num` 字段），未用真实 `SEND_GIFT`/`COMBO_SEND` 样本验证；`COMBO_SEND` 的 `data.price` 字段是否同结构存在也未验证。

- **影响**: 若真实 payload 中 `price` 为单价，多数量礼物的过滤阈值会偏低（按单价而非总价过滤）。
- **建议**: 上线前用真实礼物事件样本核对一次；若为单价，在 `GiftPriceExtractor` 内单点修正（乘以 `data.num`）。

### P4 - 新文件缺少末尾换行符

`servers/api/Live/GiftPriceExtractor.cs` 与 `tests/Z.Vtuber.Tests/GiftPriceExtractorTests.cs` 均为 "No newline at end of file"。

## 四、其他观察（非问题）

- 索引：提案 6.1 已明确决策复用既有 `RoomId + OccurredAt` 索引、不新增金额索引，理由充分（RoomId 等值谓词已大幅收敛扫描范围），后续如实测不达 PRD 性能要求再补。
- 历史数据：缺 `Price` 字段的旧 gift 记录在 Mongo 查询中不匹配 `Gte(Price, threshold)`，被过滤，符合 PRD「视为金额 0」语义。
- PR #18 标题/描述（"integration branch sync with main"）与实际内容（minPrice 功能合入 release）不符，建议后续流程中保持 PR 描述与变更内容一致。

## 五、结论

功能实现与 PRD/契约/提案一致，测试全绿（132/132），核心验收点均有测试覆盖。发现的 4 项问题均为中低优先级，不阻塞合入 release；其中 P3（真实 payload 样本核对）建议在上线后的真实数据环境中优先跟进确认。
