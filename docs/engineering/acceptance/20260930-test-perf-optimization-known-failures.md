---
description: 测试运行性能优化——既有失败用例登记（非本次优化引入，另案处理）
type: Permanent
---

# 测试运行性能优化 - 既有失败用例登记（另案处理）

**日期**: 2026-09-30
**关联需求总分支**: `feat/test-perf-optimization`
**关联实现分支**: `impl/test-perf-optimization`（PR #152）

## 背景

「测试运行性能优化」验收要求「全量测试全部通过」。但在实现分支优化后，全量仍有 **6 个用例失败**。经在需求总分支 `feat/test-perf-optimization`（实现分支的 base）上**独立复现**，确认这 6 个失败**并非本次优化引入**，而是既有失败。为不越界修改业务行为，特此显式登记为**另案处理**。

## 既有失败清单

| # | 用例 | base 分支复现 | 初判根因 | 处理 |
|---|------|--------------|---------|------|
| 1 | `RecordingFileNameTests.Render_Raw_BuildsExpectedTemplate` | 是 | 文件名模板与实现输出漂移（期望 `[RAW][123][某主播] 2026-09-13-晚间歌回.flv`，实现输出含时间戳、无空格） | 另案：录播文件名模板对齐 |
| 2 | `RecordingFileNameTests.Render_KeepsRoomIdAndAnchor_WhenTitleOverlong` | 是 | 同上 | 同上 |
| 3 | `FfmpegProcessBuilderTests.Av1TargetBitrate_IsNinetyPercent` | 是 | AV1 目标码率构建与实现漂移 | 另案：ffmpeg 参数构建对齐 |
| 4 | `FfmpegProcessBuilderTests.Av1TargetBitrate_FallsBackToDefault_WhenMetadataMissing` | 是 | 同上 | 同上 |
| 5 | `WeiboTimelineJobTests.RunOnce_PageAllExisting_Converges_NoSecondFetch` | 是 | `WeiboTimelineJob.CrawlAsync` 在整页已存在时未把 `sinceId` 推进为 `page.SinceId`，游标不前进；两次请求 `sinceId` 均为 `null`（`CalledSinceIds=[null,null]`） | 另案：需改业务分页行为，超出本「测试性能优化」范围 |
| 6 | `WeiboTimelineJobTests.RunOnce_RespectsMaxPagesPerCrawl` | 是 | `CrawlAsync` 首页有新增即 `return`，不继续翻页，`MaxPagesPerCrawl` 不被行使（`CalledSinceIds.Count` 恒为 1） | 同上 |

## 证据

- base 分支复现命令：
  ```bash
  git checkout feat/test-perf-optimization
  dotnet test tests/Z.Vtuber.Tests --filter "FullyQualifiedName~RecordingFileNameTests|FullyQualifiedName~FfmpegProcessBuilderTests|FullyQualifiedName~WeiboTimelineJobTests"
  # → Failed: 6
  ```
- 优化后全量连续 3 次运行：失败集合稳定为上述 6 项，无新增失败，无 >5s 用例。

## 结论与边界

- 本 PR 的目标是**测试基础设施性能**，PRD/技术提案明确约束「**不改变任何业务代码的对外行为**」。UUT 5/6 需要修改 `WeiboTimelineJob` 的分页/收敛行为，属于业务逻辑修复，不应混入本优化。
- 已在本 PR 与需求总 PR #151 描述中显式登记，**待上游/人类确认后另案修复**（建议新建独立 fix 分支并补充对应单测）。
