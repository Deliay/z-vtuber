---
description: integration 分支验收报告（第 2 轮 / 返工复审）——音频生成 + 测试运行性能优化（PR #153）返工提交 d7ff490、ca1bcad 对上轮 2 项 P2 的修复复核结果：全部修复到位且有回归测试锁定，结论通过
type: Permanent
---

# Integration 验收报告（第 2 轮·返工复审）：integration → release（PR #153）

- **验收日期**: 2026-09-30
- **PR**: https://github.com/Deliay/z-vtuber/pull/153 （integration → release，OPEN / MERGEABLE）
- **本轮 Review 范围**: 返工提交 `d7ff490`（`fix(training): batch-delete 向量级联限定数据集并收敛为实际删除条目`）、`ca1bcad`（`fix(api): TrainingService 代理超时显式高于 training 阶段预算`），即对上轮报告 [[20260930-acceptance-integration-pr153]]（结论：不通过，2 项 P2）的修复验证；连同 `release..integration` 全差距复核（**37 commits / 106 files / +7426/-235**）
- **基线**: `release` = `0f34a75ccc616eff722ef38555d33cbd3add4725`；`integration` = `ca1bcad2539ea5327639f0fd534ca11897e19af9`（`dc6d73c` + 2 笔修复提交）；`release` 为 `integration` 祖先，差距即 integration 相对 release 的全部新增。较上轮（起点 `7ee216e`，167 files）收敛，因「训练-数据集工具优化」（PR #146，`e9550e0`）与「模型服务会话头」（PR #147，`0f34a75`）两特性**已合入 release**，不再属于本 PR 差距；两部分上轮均已验收通过（维持 [[20260929-acceptance-integration-pr146-r2]] 结论 / 零问题），本轮确认无回退
- **上游产物**: 修复Review意见节点的「修复概述」（fix/pr153-review 提交 `d7ff490`、`ca1bcad`，已推送 origin/integration）
- **验收结论**: **通过**——上轮 2 项 P2 逐项修复到位且有回归测试锁定（含真 Milvus 端到端跨数据集用例），随返工处理的 5 项 P3/P4 全部落实；独立复跑构建/聚焦测试/全量测试/web 测试/契约 codegen/lint 全部达标；残留 3 项 P3/P4 维持登记（不阻塞）+ 本轮新增 3 条提醒，建议随后续小 PR 跟进，**可合入 release**

## 一、上轮 P2 修复逐项复核

### P2-1 跨数据集误删派生向量（数据完整性）— ✅ 已修复

- **修复实现**（与上轮建议一致）：
  - `servers/common/training/Persistence/TrainingDatasetItemRepository.cs`：`DeleteManyAsync` 改返回**实际被 Mongo 删除的条目 id 集合**（按 `datasetId + id in [...]` 先读命中 id 再删），调用方据实收敛级联范围；
  - `servers/training/Controllers/TrainingDatasetItemsController.cs`：`batch-delete` 仅对 `deletedIds` 做向量级联（不再对请求全量 id 无条件级联），响应 `{deleted}` 语义不变（仍为实际删除数）；
  - `servers/training/Vectors/MilvusVectorStore.cs`：单条目删除 filter 由 `id == "..."` 改为 **`datasetId == "..." && id == "..."`**，`DeleteItemVectorsAsync` 与 `UpsertItemVectorsAsync` null 侧删除**同步收敛**（两处都带数据集作用域）；
  - `servers/training/Vectors/IVectorStore.cs`：注释明确 `datasetId + itemId` 双重限定语义，消除上轮 P3-3 接口/实现漂移。
- **攻击路径闭合验证**：对数据集 A 调 `POST /api/training/datasets/{A}/items/batch-delete` 传入属于数据集 B 的条目 id → Mongo 删 0 行（filter 按数据集限定）→ 级联范围收敛为 `deletedIds`（空）→ Milvus 零删除；即便 id 被误入级联，Milvus 删除 filter 亦带 `datasetId` 作用域，**双层防护**。B 的 `synthesize` 候选不再静默丢失。
- **回归测试**（新增 `tests/Z.Vtuber.Tests/TrainingDatasetVectorCascadeTests.cs` 3 例，断言非空转）：
  - 控制器级 `BatchDelete_CrossDatasetId_OnlyCascadesActuallyDeletedItems`（替身记录级联 id，断言只删 A 实际删除条目、B 条目留存）；
  - 真 Milvus 端到端 `BatchDelete_CrossDatasetItemId_DoesNotDeleteOtherDatasetVectors`（对 A 传入 B 的 itemId：Mongo 删 0 行、B 向量仍可检索）；
  - 真 Milvus 端到端 `BatchDelete_DeletesOwnItemVectors_AndLeavesOtherDatasetVectors`（删本数据集条目只清自身向量、B 向量保留）；
  - `MilvusVectorStoreTests` 补 `datasetId && id` filter 精确断言（含 `UpsertItemVectorsAsync` null 侧）；`TrainingDatasetItemRepositoryTests` 适配返回类型并断言返回集合不含未删 id。
- **连带核查**：`DeleteDatasetVectorsAsync`/`DeleteAsync` 本就按 `datasetId` 作用域；条目更新不支持跨数据集迁移（只更 `audioUri/text/startMs/endMs`）；`DeleteManyAsync` 其余调用点（仓储测试）已同步适配；全仓 grep 无遗留无作用域删除路径。

### P2-2 代理超时预算倒挂（成功路径误报 502）— ✅ 已修复

- **修复实现**：`servers/api/Controllers/TrainingProxyController.cs` 新增 `DefaultTimeoutSeconds = 180`（文档注释写明必须高于 `Tts`/`Ai`/`Embedding` 的 120s 阶段预算及误报 502 机理）；`servers/api/Program.cs` 为 `TrainingService` named client 显式设 `client.Timeout`，默认 180s，支持 `TrainingService:RequestTimeoutSeconds` 配置覆盖。
- **口径文档**：`servers/api/AGENTS.md` 注明代理超时须高于各阶段预算及一致性守护。
- **一致性断言**（新增 `tests/Z.Vtuber.Tests/TrainingProxyTimeoutConsistencyTests.cs`）：断言 `DefaultTimeoutSeconds`（180）严格大于 `Tts`/`Ai`/`Embedding`/`Milvus` 各阶段 `RequestTimeoutSeconds` 默认值，配置漂移即红。
- **效果核验**：180s > 120s（TTS 慢合成 / embedding 阶段预算）> 30s（Milvus），上轮「training 侧正常完成、代理先超时误报 502」的窗口闭合；`ForwardAsync` 超时→502 映射保留（真不可达仍正确报 502）。
- **注**：上轮建议的「`vectors/rebuild`/`items/import` 向量同步改异步（202）或请求外执行」本轮未做（属建议非硬性要求），且随返工的 P3-2 已消除「重导整批重同步」这一最大放大面；超大批次（数百条串行 embedding）仍可能超 180s，见第四节遗留登记。

## 二、随返工处理的 P3/P4 复核

| 项 | 状态 | 证据 |
|---|---|---|
| P3-1 `TryBuildEmotionReferenceAsync` 吞取消异常 | ✅ | `catch (OperationCanceledException) when (ct.IsCancellationRequested) throw;` 先于兜底 `catch`，取消不再降级中性继续调 TTS |
| P3-2 导入增量同步重同步整批 source | ✅ | `items/import` 导入前记录该 source 已有条目 `AudioKey`，仅对本次新增条目 `SyncItemAsync`（`AudioKey` 有 `(DatasetId, AudioKey)` 唯一索引，判「新增」可靠） |
| P3-3 `DeleteItemVectorsAsync` 接口/实现漂移 | ✅ | 随 P2-1 闭合：形参生效 + 注释明确双重限定 |
| P4 命中头回显 Milvus 输出字段 | ✅ | `SynthesizeService` 改用条目记录（Mongo 事实源）`speaker.Item.AudioKey`/`match.Item.AudioKey`，控制器侧 `IsNullOrEmpty` 守卫；TTS 成功后不再因畸形值落 500 |
| P4 `ParseAudioUri` 接受 `.`/`..` 段 | ✅ | 拒绝 file 为 `.`/`..`（含 `/` 既有拒绝），`TrainingMappingTests` 补 2 条用例 |
| P4 `MilvusOptions.SearchLimit` 死配置 | ✅ | 删除属性，`servers/training/AGENTS.md` 同步；全仓 grep 无残留引用 |
| P4 `SynthesizeService` 未读 `ttsOptions`（CS9113） | ✅ | 构造参数移除，构建 0 CS9113（`TrainingSynthesizeApiTests` 的 `ttsOptions` 仍用于 `TrainingSynthesizeController`，非残留） |
| P4 `TestWait.cs:39` `Assert.True(false,…)` | ✅ | 改 `Assert.Fail(msg)`，`TestWaitTests` 异常类型同步 `TrueException → FailException` |

**未处理（维持登记，属提醒非阻塞）**：`RecordingAudioClient` 全量缓冲后再限长（上轮 P3-9 延续）、确定性 tie-break 仅 top-k 窗口内稳定、畸形 JSON body 返 ProblemDetails 既有口径。

## 三、验证结果（AI Review 独立复跑，非仅采信返工自述）

- **构建**: `dotnet build servers/Z.Vtuber.slnx` **0 错误**（30 warnings：xUnit/nullable 分析器既有告警 + NuGet `NU1903` 既有漏洞告警；上轮 CS9113 已消除）。
- **测试（dotnet 聚焦，本次差距相关）**: `dotnet test --filter "Training|Emotion|Model|Synthesize|Milvus|Vector|Embedding|IndexTts|RecordingAudio|CollectionNames|TestWait|TestReport"` **547/547 全部通过**（20s；基线 541 + 6 新增回归用例，含真 Milvus Testcontainers 跨数据集端到端）。
- **测试（dotnet 全量）**: **1642/1648**，6 项失败逐条与 [[20260930-test-perf-optimization-known-failures]] 登记的既有失败一致（`RecordingFileNameTests`×2、`FfmpegProcessBuilderTests`×2、`WeiboTimelineJobTests`×2，均已独立复现，与本差距无关），不阻塞验收。
- **测试（web）**: `npm test -- --run` **325/325 全部通过**（19 个文件）。
- **契约一致性**: `npm run codegen` 后 `git diff` **0 diff**（返工两笔提交未触及 `contracts/`，上轮契约三方一致结论维持）；`npm run lint`（oxlint）**0 warnings 0 errors**（51 files）。
- **测试耗时护栏**: `TestReport --check` 在本轮环境见 4 例 >5s（均为真 Milvus Testcontainers 用例，7.1s/6.9s/6.0s/5.9s）；对基线 trx 对照显示同一用例随「谁承担容器首次拉起成本」在 2.2s↔8.5s 间波动（返工自测环境仅 `Real_UpsertAndSearchText_Top1` 9.5s>5s，该例在返工前基线 `tests-focus.trx` 同样 9.5s，非本次引入），属环境负载/调度波动而非代码回归；返工未强塞例外条目（正确），护栏例外口径见第四节提醒。

## 四、遗留登记（不阻塞，建议随后续小 PR 排期）

1. **P3 `vectors/rebuild` / `items/import` 向量同步仍在请求内串行执行**：条目数百级时仍可能超过代理 180s 预算被误报 502（本轮 P2-2 只抬高预算、P3-2 只消除重导放大面，未改异步 202 + 任务查询）；建议按上轮建议改异步或请求外执行。
2. **P4 `TrainingService:RequestTimeoutSeconds` 覆盖值无启动校验**：配成 0/负值不会被一致性测试拦截，将在首次创建 client 时抛 `ArgumentOutOfRangeException`；建议加 `[Range]`/启动校验。
3. **P4 测试耗时护栏对真 Milvus 用例的口径**：Testcontainers 容器启动成本按调度落在首个触达的用例上，同用例 2.2s↔8.5s 波动，`TimeoutExceptions.json` 5s 阈值对此类用例不稳定；建议 fixture 预热（把启动成本挪进 fixture setup）或为 real-Milvus 集成用例登记单独口径。
4. **P4 维持上轮登记**：`RecordingAudioClient` 全量缓冲后再限长、tie-break 仅 top-k 窗口内稳定、畸形 JSON body 返 ProblemDetails 既有口径、`TrainingDatasetItemRepository.DeleteManyAsync` 读-删两跳（并发重复请求幂等无害，仅提一句）。

## 五、零问题部分（维持上轮结论）

- **测试运行性能优化 / 模型服务会话头**：返工两笔提交文件面仅 `servers/common/training`、`servers/training`、`servers/api`、`tests/`（+2 AGENTS.md），未触及该两特性实现与测试完整性约束（`Fact(Skip` 增量 0、断言零弱化），上轮「零问题」结论维持。
- **音频生成（数据集语音合成）主体**：上轮核验的错误矩阵、校验顺序、确定性口径（FLAT + 稳定 tie-break + `temperature: 0`）、日志不打文本/音频/apiKey、`AllowAutoRedirect=false` 等结论维持；本轮返工未引入新行为偏差。

## 六、结论

上轮 2 项 P2 **逐项修复到位**：P2-1 以「级联范围收敛为 Mongo 实际删除条目 + Milvus 删除 filter 带 `datasetId` 作用域」双层闭合跨数据集误删路径，并以真 Milvus 端到端回归锁定；P2-2 以显式 180s 代理超时（可配置覆盖）+ 一致性断言 + AGENTS 口径闭合预算倒挂。随返工的 5 项 P3/P4 全部落实，独立复跑构建 0 错误、聚焦 547/547、全量 1642/1648（余 6 项为已登记既有失败）、web 325/325、codegen 0 diff、lint 0 告警。残留 3 项新提醒 + 3 项维持登记均为不阻塞改进项。

**验收结论：通过**，PR #153（`release..integration`）可进入合入 release 流程。
