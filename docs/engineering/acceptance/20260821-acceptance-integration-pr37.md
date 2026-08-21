---
description: integration 分支验收报告——B站412风控修复、订阅抓取类型独立控制、直播事件发送者搜索合入 release 前的代码 Review 与测试验证结果
type: Permanent
---

# Integration 验收报告：412风控修复 + 订阅类型拆分 + 发送者搜索（PR #37）

- **验收日期**: 2026-08-21
- **PR**: https://github.com/Deliay/z-vtuber/pull/37 （integration → release）
- **Review 范围**: `release..integration` 代码差距（84 commits，72 个文件，+8441/-86；release 为 integration 祖先）
- **关联 PRD/提案**: `docs/product/draft/20260820-bilibili-412-fix-prd.md`、`20260820-subscription-crawl-type-prd.md`、`20260820-live-event-sender-search-prd.md` 及对应 proposals/plans
- **关联修复文档**: `docs/engineering/fix/20260819-fix-acceptance-issues.md`（上次验收 PR #25 问题修复）
- **验收结论**: **不通过**（1 项 P1 阻塞合入：412 风控修复核心目标在直连通道不生效；另有 P2×7、P3×5、P4 若干）

## 一、变更内容概述

本次 integration 相对 release 的增量包含四块：

1. **B 站 412 风控修复**：新增 `RiskControlClassifier`/`RiskControlHandler`/`RiskControlException`、`BrowserishBiliApiClient`（拟真通道）、`ResilientBiliApiClient`（直连/拟真/Auto 三模式装饰器）、`CrawlBackoffRegistry`（目标级指数退避+抖动+冷却阈值）、`CookieRecord.CooldownUntil` 凭证冷却；`BilibiliCrawlerOptions` 扩展风控配置；4 个 Job 捕获 `CrawlBackoffActiveException` 跳过当轮；`CookieProbeJob` 探活与风控联动。
2. **订阅抓取类型独立控制**：`SubscriptionRecord` 新增 `Type` 字段（`SubscriptionType` 枚举 danmaku/dynamic），订阅/退订 API 增加 `type` 参数，存量数据迁移 `subscription_type_split_v1`（原记录置 dynamic + 补插 danmaku），`(Mid, Type)` 复合唯一索引，Job 按类型过滤。枚举 JSON 序列化修复为 camelCase（f2429f3）。
3. **直播事件发送者搜索**：`DanmakuEventNormalizer` 提取 sender 昵称/uid/粉丝牌，`LiveEventTextIndexRecord` 扩展 sender 字段，`event/search` 支持 sender 多维度匹配，存量回填（保留现有 `_id`，25e1cbc）。
4. **上次验收修复**：`$text` → `$regex` 子串匹配、`BackfillGiftPriceAsync` 游标分页、契约 minPrice maximum、旧索引清理等，详见下文核验结果。

## 二、验证结果

- **构建**: `dotnet build` 成功，0 错误；仅 NU1903（Microsoft.OpenApi 2.0.0 已知漏洞，release 已存在，非本次引入）。
- **测试**: `dotnet test` **288/288 全部通过**（含 Testcontainers Mongo 集成测试）。
- **上次验收修复逐项核验**（对照 `docs/engineering/fix/20260819-fix-acceptance-issues.md`）：

| 修复项 | 判定 | 证据 |
|---|---|---|
| P1-1 $text→$regex 中文子串搜索 | ✅ 已正确修复 | `LiveEventTextIndexRepository.cs:29-30` Regex.Escape + "i"；索引 `{RoomId:1,OccurredAt:-1,Id:-1}` + hint；中文用例测试在 |
| P1-2 BackfillGiftPrice 死循环 | ✅ 已正确修复 | 过滤仅 `Not(Exists(Price))`（`MongoDbInitializer.cs:288`）+ `_id` 游标；600 条 silver 终止性测试在（`MongoDbInitializerTests.cs:289-312`） |
| P2 排序升序（已撤销） | ✅ 处理与更正一致 | 保持降序，索引显式命名，契约同步 |
| P3-1 契约 minPrice maximum | ✅ 已修复 | 契约 `maximum: 9000000000000000` 与控制器一致 |
| P4（换行/CS0618/空白弹幕） | ✅ 已修复 | 三文件末尾 0a；`InsertOneAsync(record, null, ct)`；`IsNullOrWhiteSpace` 拦截 |
| R1 旧索引清理 | ✅ 已修复 | `DropIndexIfExistsAsync` + 两个 drop 测试 |
| R2 搜索排序索引/hint 常量 | ✅ 已修复 | `SearchIndexName` 单一事实源 |
| R3 IsNullOrWhiteSpace/契约描述 | ✅ 已修复 | 契约已同步「不区分大小写子串匹配」 |

- **核对为符合方案的项**: Cookie 冷却过滤与 UTC 序列化、退避曲线/抖动/阈值逻辑、Job 捕获 backoff 异常不影响调度、订阅迁移启动顺序（删旧索引→迁移→建复合唯一索引）、sender 回填幂等性（marker + EventId 唯一索引 upsert + `_id` 保留）、regex 注入防护、契约主体一致性（stringCriteria 400 四条件、订阅 API type 参数、event/range 降序）。

## 三、发现的问题

### P1 - 直连通道 RiskControlHandler 被 Mikibot 默认 HttpClient 覆盖，412 识别整体不生效，核心修复目标落空

`servers/api/Crawlers/BilibiliAccountProvider.cs:30-35`：`BuildProviderAsync` 先注册 `AddSingleton(new HttpClient(new RiskControlHandler(...)))`（:30），随后 `services.AddBilibiliCrawlers()`（:35）。经反编译本地 `mikibot.crawler/1.0.9` 包确认，`AddBilibiliCrawlers(addHttpClient: true)` 内部执行 `services.AddSingleton<HttpClient>()`——**后注册生效**，MS DI 解析单服务取最后注册，故 `BiliLiveCrawler`/`BiliBasicInfoCrawler` 拿到的是无 handler、无超时的默认 `HttpClient`。

后果：直连通道 412/-412 永远不会抛 `RiskControlException`（Mikibot `HttpCrawler.GetAsync` 不调 `EnsureSuccessStatusCode`：HTML 412 → `JsonException`；JSON -412 → `InvalidOperationException`），**Auto 模式永不降级，退避/冷却/凭证联动全部不触发**，`HttpTimeoutSeconds` 配置同样失效。本次 412 修复的核心目标在生产路径下等价于未交付。现有 45 个风控单测全部直接 new 对象，无一覆盖该 DI 管线，288 全绿掩盖了该缺陷。

- **建议**：`services.AddBilibiliCrawlers(addHttpClient: false)`，或将自家 HttpClient 注册移到 `AddBilibiliCrawlers()` 之后；补一个从 provider 解析 crawler 并断言其 HttpClient 管线含 `RiskControlHandler` 的接线测试。

### P2-1 - CookieProbeJob 把任何异常一律改判冷却，丢失离线语义（2 个审查线独立命中）

`servers/api/Jobs/CookieProbeJob.cs:39-44`：`catch (Exception)` 一律 `MarkCooldownAsync`，包括 :32 主动抛出的「nav returned mid=0」（cookie 失效的明确信号，release 版此处为 `MarkOfflineAsync`）。Plan `20260820-plan-bilibili-412-fix.md:93` 明确要求「风控异常→冷却、**普通异常→离线（回归）**」。后果：永久失效 cookie 永不标离线，冷却到期后回到池中被反复取用反复失败；`MarkOfflineAsync` 在生产代码中已无调用方，AGENTS.md 宣称的「在线/冷却/离线」三态中离线态不可达；`catch (Exception)` 还会吞掉关停时的 `OperationCanceledException` 误标冷却。且 Plan 3.2 列明的三个 CookieProbeJob 测试用例全部缺失（无任何 CookieProbeJob 测试文件），正是该偏差漏网的原因。

- **建议**：拆分 catch——`RiskControlException`→冷却，其余异常→`MarkOfflineAsync`；补 `CookieProbeJobTests`。

### P2-2 - CrawlBackoffRegistry 配置绑定静默失效

`servers/api/Crawlers/CrawlBackoffRegistry.cs:21-26` + `servers/api/Program.cs:22-25,58`：Program.cs 只注册了具体类型 `BilibiliCrawlerOptions` 单例，全仓库无 `Configure<BilibiliCrawlerOptions>`；registry 构造器要 `IOptions<BilibiliCrawlerOptions>`，由 OptionsManager 以属性默认值新建实例。appsettings/环境变量对 `BackoffBaseSeconds`/`BackoffMaxMinutes`/`CooldownMinutes`/`RiskControlCooldownThreshold` 的覆盖对退避注册表不生效（当前 appsettings 恰好等于代码默认值，暂未暴露）。

- **建议**：`AddOptions<BilibiliCrawlerOptions>().Bind(section)` 并令具体单例取自 IOptions，或 registry 改注具体类型。

### P2-3 - 拟真通道未实现方案要求的 wbi 签名，降级通道可用性未验证

`servers/api/Crawlers/BrowserishBiliApiClient.cs:103-163`：Plan 4.1 与提案 5.2 要求 Nav/空间动态复用 `BilibiliAccount.Sign` wbi 签名，实现完全未签名（Nav 还换了端点：直连 `x/web-interface/nav`，拟真 `x/nav/wbi`）；空间动态 `feed/space` 在直连通道是带 wbi 签名的，拟真通道裸拼 URL。若 B 站强制 wbi，降级后依旧失败。测试全走 mock handler；Plan Phase 6.1 要求 `FetchMode=Browser` 实测，仓库中未见验证记录。

- **建议**：按提案复用 `Sign`，并补 Browser 模式实测记录。

### P2-4 - 订阅类型迁移崩溃缝隙可永久丢失 danmaku 订阅

`servers/api/Persistence/MongoDbInitializer.cs:578-588`：批内先 `UpdateOneModel(Set Type=Dynamic)` 再 `InsertOneModel(danmaku)`。若进程在两者之间崩溃，重启后该记录已带 Type 不再匹配迁移过滤（:559），danmaku 补插永不重试 → 该 mid 弹幕/直播抓取静默停止。Plan Phase 4.1 明确要求第二条记录用 **upsert**（`{Mid, Type="danmaku"}`），实现用了 `InsertOneModel`，正是偏差来源。

- **建议**：改为 `ReplaceOneModel(Filter.Eq(Mid)+Eq(Type), IsUpsert=true)`，或重跑时按「缺 danmaku 对端」匹配。

### P2-5 - 迁移 meta 标记为唯一跳过依据，与已批准 Plan 冲突

`MongoDbInitializer.cs:547-553`：marker 存在即整体 return。Plan Phase 4.1 要求「迁移判断以数据状态为准，meta 仅作快速跳过」。回滚场景（新版迁移完成→回滚旧版写入无 Type 新订阅→再升级）下 marker 直接跳过，这些订阅不在任何类型过滤结果中，抓取整体失效。测试 `Migrate_Marker_PreventsRerun`（`SubscriptionTypeSplitMigrationTests.cs:120-133`）把偏差固化为预期。

- **建议**：marker 命中后再做 `Exists(Type)==false` 计数检查，为 0 才跳过。

### P2-6 - sender 回填 N+1 查询且启动阻塞无取消

`MongoDbInitializer.cs:493-495` 对每条记录单独 `Find(EventId)` 再进批；`Program.cs:90` 在 `app.Run()` 前同步串行执行且 `ct=default`。弹幕量级下每记录 2 次往返；中途被杀 marker 未写入（:538），下次启动从零重扫，大数据量下有「永远迁移不完、服务起不来」的风险。

- **建议**：批内 `Filter.In(EventId, batchIds)` 一次取回建字典（消 N+1），或改 `UpdateOneModel` `$set` sender + `$setOnInsert`；传取消令牌或改后台迁移。

### P2-7 - BackfillLiveEventTextIndexAsync 残留 25e1cbc 同类 `_id` bug

`MongoDbInitializer.cs:395` 无条件 `Id = ObjectId.GenerateNewId()`，`ReplaceOneModel` upsert 命中已存在 EventId 文档时触发 immutable `_id` 错误（code 66），BulkWrite 抛异常直接导致启动失败。正常流程被 marker 挡住，但运维手动清 marker 强制重跑即必现——与 sender 回填专门修掉的失败模式完全相同，修复不彻底。

- **建议**：与 sender 回填对齐（保留现有 `_id`）或同样改 `$set` 方案。

### P3-1 - 契约 minPrice 描述与实现不一致（gift_v2）

契约 MinPrice 描述「仅作用于 gift 类型事件」；但 `LiveEventRepository.cs:36` 用 `Nin(Type, ["gift","gift_v2"])`，`gift_v2`（本次新增类型）同样被过滤。docs 中无记载。建议契约改为「作用于 gift/gift_v2 礼物类事件」。

### P3-2 - Controller 接受契约外的数字字符串 type

`BilibiliSubscriptionController.cs:28-29,42-43`：`Enum.TryParse` 可解析 `"0"`/`"1"`（甚至 `"Dynamic, Danmaku"`）且 `Enum.IsDefined` 通过，`POST {"mid":1,"type":"0"}` 返回 200，违反契约 `enum: [danmaku, dynamic]`。Plan Phase 2.1 要求的 Controller 测试缺失（无 `BilibiliSubscriptionControllerTests.cs`），问题因此漏网。建议解析前拒绝纯数字并补控制器测试。

### P3-3 - Mongo 落库枚举为 PascalCase，与 Plan/提案「落库小写」不符

`SubscriptionRecord.cs:14` `[BsonRepresentation(BsonType.String)]` 落库为 `"Dynamic"/"Danmaku"`；提案 §2.3 与 Plan 1.1 写明落库小写且要求集成测试断言落库值——测试未实现。API 层不受影响（JSON 层 camelCase 已修复），但属文档/验收偏差，未来脚本直读库会踩坑。建议注册 camelCase Bson 序列化约定并 bump 迁移键清洗存量，或修订提案/Plan 记录实际约定。

### P3-4 - gift_v2 归一化 `Parse()` 无保护且无测试

`DanmakuEventNormalizer.cs:61-62` `v2Cmd.Data.Parse()` 遇畸形 SEND_GIFT_V2 payload 抛异常，冒泡到 `LiveEventSource.cs:189-191` 通用 catch 被误判「连接丢失」5s 重连；反复出现造成连接抖动。`DanmakuEventNormalizerTests.cs` 无任何 gift_v2/Price 用例，gold/silver/缺 DiscountPrice 分支（:65-69）零覆盖。建议 normalizer 内 try/catch 降级返回 null 并补测试。

### P3-5 - servers/api/AGENTS.md 未同步

第 27-28 行只列旧迁移键，未提 `subscription_type_split_v1`、`SubscriptionType` 维度、`(Mid, Type)` 复合唯一索引及 `Subscribed` 事件新载荷（Plan 5.4 未完成）；「7 个 MongoDB 集合实体」计数错误（实际 9 个：8 实体 + Meta）。

### P4 - 杂项

- `ResilientBiliApiClient.cs:27` 构造参数 `optionsSnapshot` 从未使用（死参数），`Program.cs:54` 冗余 `Options.Create(...)`——删除或改用其一。
- `ResilientBiliApiClient.cs:171` 凭证冷却写库 fire-and-forget：Mongo 写失败成 unobserved exception 且无日志，cookie 实际未冷却仍被分配。建议 await 或 ContinueWith 记日志。
- `ResilientBiliApiClient.cs:128-148` Auto 模式「直连风控→当轮拟真成功」立即 `ReportSuccess` 清掉刚登记的退避，目标级退避在最常见路径不积累；`ResilientBiliApiClientTests:143-171` 未断言退避登记。建议固化语义（降级当轮不再 ReportSuccess）。
- `RiskControlResult.RetryAfter`/`RiskControlException.RetryAfter` 为死属性（分类器从不读 `Retry-After` 头）——或实现或删除。
- `RiskControlHandler.cs:19` 对每个响应（含 200 大 JSON）全量 `ReadAsStringAsync` 缓冲，分页响应内存翻倍；命中风控时 response 未 Dispose。
- `BrowserishBiliApiClient.cs:14` `new Random()` 非线程安全（建议 `Random.Shared`）；`BrowserMinDelaySeconds > BrowserMaxDelaySeconds` 错配会让 `Random.Next` 抛异常（:70），无启动校验；预热请求固定 `buvid3=test` 且走同一 RiskControlHandler，预热命中风控会冷却无关业务 cookie（:54-57）。
- 缺末尾换行：`BrowserishBiliApiClient.cs`、`BrowserishBiliApiClientTests.cs`、`docs/engineering/fixes/20260817-fix-min-price-issues.md`（fix 文档 P4 清单刚修过同类问题却漏了它们）。
- 契约 `event/range` 400 响应描述（yaml:206）遗漏「超过 9000000000000000」上限条件。
- `event/search` 的 total 取自索引计数、items 经 join 过滤，两集合不一致时口径对不上（当前无删除路径，影响低，建议契约注明）。
- `LiveEventTextIndexRepository.cs:41-44` 纯数字关键词 `"0"` 会命中所有 `SenderUid=0`（信息缺失降级值）记录；建议 uid==0 时不追加 Eq 过滤。搜索 `CountDocumentsAsync` 无 maxTimeMS，建议文档注明量级上限。
- `docs/engineering/` 同时存在 `fix/` 与 `fixes/` 两个目录，命名不统一。
- `SubscriptionType.cs` 枚举成员上的 `[BsonRepresentation]` 为无效挂载（生效的是 `SubscriptionRecord.Type` 上的），建议删除以免误导；`SubscriptionType_SerializesToCamelCase` 测试自建 JsonSerializerOptions 而非复用 Program.cs 实际配置，存在漂移风险。

## 四、结论

构建通过、测试 288/288 全绿、上次验收的 8 项修复全部正确落实、契约主体与实现一致。但存在 1 项 P1 阻塞问题：**412 风控修复的 RiskControlHandler 因 DI 注册顺序被 Mikibot 默认 HttpClient 覆盖，直连通道风控识别完全不生效，核心修复目标在生产路径下落空**；另有 7 项 P2（CookieProbeJob 异常改判、退避配置绑定失效、拟真通道缺 wbi 签名、订阅迁移两处数据完整性缝隙、sender 回填 N+1/启动阻塞、文本索引回填 `_id` bug 修复不彻底）。建议打回修复 P1 及 P2 后重新验收。
