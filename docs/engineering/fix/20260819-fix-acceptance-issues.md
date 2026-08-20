# 验收问题修复方案

- **关联验收报告**: `docs/engineering/acceptance/20260819-acceptance-integration-live-event-text-search.md`
- **修复日期**: 2026-08-19

## 修复清单

### P1-1 弹幕/SC 全文检索对中文关键词不生效

**根因**: MongoDB `$text` 索引不做 CJK 分词/二元组切分，整段中文是单一 token，子串搜索不命中。

**方案**: 将 `LiveEventTextIndexRepository.SearchAsync` 从 `$text` 改为 `$regex` 子串匹配：
- 将文本索引从 `Text("text")` 改为 `Ascending("Text")` 普通索引（配合 `RoomId` 复合索引）
- 查询时使用 `Regex(criteria, options: "i")` 进行大小写不敏感的子串匹配
- 对用户输入的正则特殊字符（如 `.*+?^${}()|[]\`）进行转义（P3-3 一并修复）
- 空字符串弹幕不写入索引（P4 一并修复）

**影响文件**:
- `servers/api/Persistence/MongoDbInitializer.cs` — 索引定义
- `servers/api/Persistence/LiveEventTextIndexRepository.cs` — 查询逻辑
- `tests/Z.Vtuber.Tests/LiveEventTextIndexRepositoryTests.cs` — 中文测试用例

### P1-2 BackfillGiftPriceAsync 死循环风险

**根因**: 过滤条件包含 `Price == 0`，silver 礼物更新后 Price 仍为 0，永远匹配；无 `_id` 游标推进。

**方案**:
- 过滤条件改为仅「Price 字段不存在」（`Filter.Not(Filter.Exists(e => e.Price))`）
- 改用 `_id` 游标分页（同文件 `BackfillLiveEventTextIndexAsync` 的正确示范）
- 补充 >500 条 silver 礼物的终止性测试

**影响文件**:
- `servers/api/Persistence/MongoDbInitializer.cs:225-279`
- `tests/Z.Vtuber.Tests/MongoDbInitializerTests.cs`

### P2 event/range 排序升序→降序

**根因**: 实现将 `SortBy(OccurredAt)` 改为 `SortDescending(OccurredAt)`，与 release 分支和既有 PRD 不一致，提案依据不实。

**方案**: `LiveEventRepository.GetPageAsync` 排序回退为 `Sort.Ascending(e => e.OccurredAt)`，索引 hint 同步调整。

**影响文件**:
- `servers/api/Persistence/LiveEventRepository.cs:44`
- `contracts/bilibili-crawler.openapi.yaml:179`

### P3-1 OpenAPI 契约补 minPrice maximum

**方案**: `MinPrice` 参数 schema 添加 `maximum: 9000000000000000`。

### P4 杂项

- 新文件末尾换行符：`LiveEventTextExtractor.cs`、`LiveEventTextIndexRecord.cs`、`LiveEventTextIndexRepository.cs`
- CS0618 警告：`MongoDbInitializer.cs:277,373` 使用 `InsertOneAsync(record, options: null, ct)` 替代过时重载
- 空字符串弹幕过滤：`LiveEventTextExtractor.ExtractText` 在 `string.IsNullOrWhiteSpace` 检查中同时过滤空字符串，或在写入端过滤

## Review 追加修复（2026-08-20）

针对 PR #31 review 意见（见 PR comment）的追加修复：

### R1 旧索引清理迁移

- `EnsureLiveEventTextIndexAsync` 幂等 drop 旧索引：`RoomId_Text_text`（$text 方案全文索引）、`RoomId_Text_1`（普通 Text 索引，无法服务 regex 与排序）
- `EnsureLiveEventIndexesAsync` 幂等 drop 旧索引 `RoomId_1_OccurredAt_-1_Type_1`（排序方向调整前的多 type 查询索引）
- 新增 `DropIndexIfExistsAsync` 辅助方法（先 list 后 drop，不存在则跳过）

### R2 搜索排序索引

- 文本索引集合的查询索引由 `{RoomId:1, Text:1}` 改为 `{RoomId:1, OccurredAt:-1, Id:-1}`（名称 `RoomId_1_OccurredAt_-1_Id_-1`，常量化于 `LiveEventTextIndexRepository.SearchIndexName`）：RoomId 等值收敛后按排序键顺序扫描，regex 作残余过滤，skip/limit 可提前终止，避免内存排序 32MB 上限风险
- `SearchAsync` 的 Find 附加该索引 hint（与 `LiveEventRepository.GetPageAsync` 的 hint 模式一致）

### R3 杂项

- `LiveEventTextExtractor.ExtractText` 改为 `IsNullOrWhiteSpace` 过滤（与本文档 P4 节方案对齐），并修正既有测试 `ExtractText_ReturnsNull_WhenTextIsWhitespace` 的 payload（原 payload 为非法 JSON，经 catch 分支返回 null，未真正覆盖空白路径）
- 契约 `event/search` 与 `stringCriteria` 描述同步为「不区分大小写的子串匹配」
