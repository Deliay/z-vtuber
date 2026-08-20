---
description: integration 分支验收报告——直播间文字事件搜索（含 minPrice、索引优化）合入 release 前的代码 Review 与测试验证结果
type: Permanent
---

# Integration 验收报告：直播间文字事件搜索（event/search）

- **验收日期**: 2026-08-19
- **PR**: https://github.com/Deliay/z-vtuber/pull/25 （integration → release）
- **Review 范围**: `release..integration` 代码差距（34 commits，26 个文件，+3171/-16）
- **关联 PRD**: `docs/product/draft/20260819-live-event-text-search-prd.md`、`docs/product/draft/20260817-gift-min-price-filter-prd.md`
- **关联提案**: `docs/engineering/proposals/20260819-proposal-live-event-text-search.md`
- **验收结论**: **不通过**（2 项 P1 阻塞合入，另有 P2/P3/P4 问题若干）

## 一、变更内容概述

本次 integration 相对 release 的增量包含三块：

1. **礼物金额筛选（minPrice）**（上次验收 PR #18 的内容仍在本次差距内，release 未合入）：`event/range` 新增 `minPrice` 参数、`LiveEventRecord.Price` 字段、`GiftPriceExtractor`、历史数据清洗 `BackfillGiftPriceAsync`（迁移键 v2）。上次报告的 P2（超大 minPrice 溢出 500）已修复为上限校验返回 400。
2. **文字事件全文搜索**：新增 `GET /api/b/live/{roomId}/event/search`，新增 `bilibili_live_event_text_index` 集合（`{RoomId:1, Text:"text"}` 全文索引 + `EventId` 唯一索引）、`LiveEventTextExtractor`、`LiveEventTextIndexRepository`、`BackfillLiveEventTextIndexAsync` 回填；`LiveEventSource` 落库时同步写文本索引（写失败仅记日志不阻断）。
3. **索引与排序调整**：`LiveEvent` 集合新增 3 个复合索引；`GetPageAsync` 排序由 `OccurredAt` **升序改为降序**，多 type 查询附加索引 hint `RoomId_1_OccurredAt_-1_Type_1`。

## 二、验证结果

- **测试**: `dotnet test tests/Z.Vtuber.Tests` 在 integration（67d4162）上 **190/190 全部通过**（含 Testcontainers Mongo 集成测试）。
- **契约一致性**: `event/search` 的 `stringCriteria` 校验（缺失/空/纯空白/超 200 字符 → 400）、分页参数 clamp、`LiveEventPage` 复用，与 OpenAPI 契约一致，控制器测试覆盖完整。
- **回填幂等性**: 文本索引回填使用 `_id` 游标分页 + `EventId` 唯一索引 upsert，幂等设计正确，有测试覆盖。
- **实测验证**: 用独立 `mongo:7.0` 容器实测全文索引中文行为（见问题 1）。
- **构建警告**: 新增 2 处 CS0618（`InsertOneAsync` 过时重载，`MongoDbInitializer.cs:277,373`）；NU1903（Microsoft.OpenApi 2.0.0 漏洞）在 release 已存在，非本次引入。

## 三、发现的问题

### P1 - 弹幕/SC 全文检索对中文关键词实际不生效，核心用例未交付

计划与提案的关键技术假设是错误的。`20260819-plan-live-event-text-search.md:125` 与 `20260819-proposal-live-event-text-search.md:230` 均声称「MongoDB `$text` 对 CJK 按二元组（bigram）切分，可满足关键词子串检索」。**MongoDB text 索引不做任何 CJK 分词/二元组切分**，仅按空白与标点切词，整段中文是单一 token。

实测（mongo:7.0，与本项目测试环境一致）：

```
文档: "今天天气不错适合出门"
$text $search "今天天气不错适合出门" → 命中 1   （整串精确匹配）
$text $search "天气"                 → 命中 0   （子串不命中）
```

而 B 站弹幕/SC 绝大多数是无空格中文，用户搜索关键词几乎必然不命中——该功能对主用例等价于未交付。

计划要求「集成测试用中文样本锁定命中语义」（plan 3.1 节），但实际测试改为英文样本，并留注释「使用英文关键词验证搜索功能，中文分词行为由 MongoDB 决定」（`LiveEventTextIndexRepositoryTests.cs:77`）——计划中的中文验收点被静默放弃，190 全绿的测试套件掩盖了该缺陷。

- **建议**：二选一——(a) 技术方案返工：改用 `$regex` 子串匹配（需评估性能，可配合 `RoomId` 等值收敛 + 排序键限制扫描范围）或引入支持中文的搜索引擎；(b) 产品层面明确接受「仅支持空格分词语言/整串匹配」的限制并修订 PRD。无论哪种，都需要补充中文样本的集成测试锁定真实语义。

### P1 - `BackfillGiftPriceAsync` 存在死循环风险，可挂死 API 启动

`servers/api/Persistence/MongoDbInitializer.cs:239-275`：回填过滤条件为 `Type == gift AND (Price 不存在 OR Price == 0)`。silver（免费）礼物及提取失败的记录经 `GiftPriceExtractor` 提取后 `Price` 仍为 0，**更新后依然匹配过滤条件**；循环每轮用同一 filter 从头 `Find`（无 `_id` 游标推进），只要库中「Price 恒为 0 的 gift 记录」≥ batchSize（500），`while (processed == batchSize)` 永不退出，对同一批记录反复无效写入。B 站免费辣条等 silver 礼物在真实数据中日增量即可超 500，属现实触发场景。

更严重的是该方法在 `Program.cs:56` 启动路径中 `app.Run` 之前同步执行——死循环意味着**服务永远无法完成启动**。迁移标记在循环结束后才写入，kill 重启后依然复现。

测试盲区：`BackfillGiftPrice_ProcessesLargeBatch` 用 600 条全部可修复的 gold 礼物；`BackfillGiftPrice_LeavesSilverGiftAtZero` 只有 1 条 silver（batch < 500 自然退出），未覆盖「≥500 条不可修复记录」场景。

- **建议**：改用基于 `_id` 的游标分页（同文件 `BackfillLiveEventTextIndexAsync` 已示范正确做法），过滤条件只保留「Price 字段不存在」（Price==0 的记录迁移键 v1/v2 已处理过，不应反复扫描）；补充「>500 条 silver 礼物」的终止性测试。

### P2 - `event/range` 排序由升序改为降序：与既有 PRD 冲突的 breaking change，且提案依据不实

- release 分支实现为 `SortBy(OccurredAt)` 升序，release 契约描述为升序；`20260815-live-event-filter-prd.md:153` 明确「事件排序维持按发生时间升序，不改变既有排序语义」；`20260815-proposal-bilibili-live-history.md:50` 亦声明 event/range 升序属时间线语义保持原样。
- 新提案（`20260819-proposal-live-event-text-search.md:211`）声称「既有排序本就为时间降序」，**与事实不符**——排序方向变更建立在错误前提上，未经产品决策。
- 实现、测试、OpenAPI 描述三者已同步为降序（内部一致），但旧 PRD 未修订；`apps/web` 当前未消费该端点，影响面为外部 API 调用方，翻页语义整体反转。

- **建议**：产品确认——若接受 breaking change，修订 20260815 PRD 并在 PR 描述中显式声明；否则 `GetPageAsync` 回退升序（hint 索引 `RoomId asc + OccurredAt desc + Type asc` 反向扫描同样可服务升序，或调整索引键方向）。

### P3 - minPrice 上限校验未同步到契约

控制器新增 `minPrice > 9_000_000_000_000_000m → 400`（正确修复了上次验收的 P2 溢出问题），但 OpenAPI `MinPrice` 参数仅声明 `minimum: 0.01` 及「小于 0.01 或非数值返回 400」，未声明 `maximum`。上次验收报告已建议契约补 maximum，本次只改了实现。

### P3 - `event/search` 的 `total` 与 `items` 可能不一致

`SearchEvents` 的 `total` 取自文本索引集合的 `CountDocuments`，`items` 是 join 回事件集合后的结果；若索引记录存在而事件记录缺失，`items` 会少于页大小且与 `total` 对不上。当前系统无事件删除路径，影响低，但 join 语义下的 total 口径值得在契约中说明。

### P3 - `stringCriteria` 未转义，`$text` 查询语法泄露给用户输入

plan 3.1 节自己声明「如发现用户输入引号导致语义变化，实现期在仓储层对 criteria 做转义（单点处理）」，实现未做。用户输入含双引号/连字符时会触发 `$text` 的短语/排除语法，改变检索语义（虽不构成注入风险，但与「关键词检索」的契约描述不符）。

### P4 - 杂项

- 新文件缺末尾换行符：`LiveEventTextExtractor.cs`、`LiveEventTextIndexRecord.cs`、`LiveEventTextIndexRepository.cs`。
- 新增 2 处 CS0618 构建警告（`MongoDbInitializer.cs:277,373` 使用过时 `InsertOneAsync` 重载）。
- 索引 hint 硬编码自动生成的索引名 `RoomId_1_OccurredAt_-1_Type_1`（`LiveEventRepository.cs`），依赖 Mongo 命名规则，脆弱；建议 `EnsureLiveEventIndexesAsync` 创建索引时显式命名并引用同一常量。
- 弹幕文本为空字符串时仍写入文本索引记录（`ExtractText` 返回 `""` 而非 `null`），产生无检索价值的索引行。

## 四、其他观察（非问题）

- 文本索引写入失败（含 11000 唯一键冲突）仅记日志不阻断事件落库与 SSE 发布，降级策略合理。
- `event/search` 限定 danmaku/super_chat 范围通过「索引集合只含这两类」隐式保证，设计成立。
- 构建警告 NU1903（Microsoft.OpenApi 2.0.0、SSH.NET 2025.1.0 高危漏洞）在 release 已存在，建议另行跟进升级，不计入本次验收。

## 五、结论

测试 190/190 全绿、契约与实现内部一致、回填幂等性设计正确，但存在 2 项 P1 阻塞问题：**中文全文检索的核心假设不成立（功能对主用例不生效）**，以及**礼物价格回填在生产数据量下有挂死服务启动的死循环风险**。另有 1 项 P2（排序方向 breaking change 未经产品确认）。建议打回修复 P1/P2 后重新验收。
