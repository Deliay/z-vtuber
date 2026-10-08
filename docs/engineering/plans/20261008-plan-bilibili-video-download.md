---
description: B 站视频下载实现 Plan：crawler 服务新增 Video/VideoDownloadController + VideoStreamService（x/web-interface/view 取 cid、x/player/wbi/playurl 取流地址，DASH 优先 flv 回退，原始流 ResponseHeadersRead 实时透传不落盘，Range/If-Range 端到端透传 200/206/416 断点续传，RequestAborted 联动取消），IBiliApiClient 扩展 GetVideoPlayInfoAsync（WBI 签名 + Cookie checkout + 风控通道），api 服务 BilibiliVideoDownloadController 以 /api/b/video/{bvid}/download 流式代理（CrawlerService named client），契约先行两份 + codegen，TDD 补选流/文件名/错误映射/透传/不落盘用例，覆盖率 ≥ 80%
type: Permanent
---

# B 站视频下载 - 实现 Plan

**功能名称**: B 站视频下载（BV 号实时取原始流透传下载）
**关联 PRD**: `docs/product/draft/20261008-bilibili-video-download-prd.md`（v1.1）
**关联技术提案**: `docs/engineering/proposals/20261008-proposal-bilibili-video-download.md`（v1.1）
**Plan 版本**: v1.0
**创建日期**: 2026-10-08
**作者**: Workflow Agent（代起草）
**需求总分支**: `feat/bilibili-video-download`（需求总 PR #181，base: main）
**feat-branch**: `feat/bilibili-video-download`（提案 §头；实现提交同入需求总分支随 PR #181 评审）

## 1. 概述

依据 PRD 与技术提案落地四件事：① `servers/crawler` 新增 `Video/VideoDownloadController`（`GET /api/video-download/{bvid}?kind=&page=`）与 `VideoStreamService`：`CheckoutAsync` 领 Cookie → `GetVideoPlayInfoAsync` 调 `x/web-interface/view` 取 `pages[page-1].cid` 与标题、`x/player/wbi/playurl`（`qn=127&fnval=16&fourk=1`）取流地址（DASH `dash.video[]`/`dash.audio[]` 取最高档，DASH 不可用回退 `fnval=0` 整段 flv）→ 独立 HttpClient 打开 B 站 CDN 流（`ResponseHeadersRead`）逐段 `CopyToAsync` 透传，**全程无磁盘写入、无整段内存缓冲，响应结束即弃**；`Range`/`If-Range` 端到端透传上游，200/206/416 与 `Accept-Ranges`/`Content-Range`/`Content-Length` 原样回传，`RequestAborted` 联动取消上游；② `Crawlers/BiliApiClient` 扩展 `IBiliApiClient.GetVideoPlayInfoAsync`（WBI 签名复用 `BilibiliAccount.Sign`，直连/拟真/Resilient 三通道同步补齐，`RiskControlException` 映射 502）；③ `servers/api` 新增 `BilibiliVideoDownloadController`（`GET /api/b/video/{bvid}/download`）流式代理（named HttpClient `CrawlerService`，Range/Content-* 透传，同 `DownloadRecording` 先例），`infra/apphost` 注册 api → crawler 服务发现；④ 契约先行（新增 `contracts/video-download.openapi.yaml` + 更新 `contracts/bilibili-crawler.openapi.yaml`）+ `npm run codegen`。实施遵循 `docs/engineering/common-rules.md`（契约先行、TDD、覆盖率 ≥ 80%、单用例 ≤ 5s）、`backend-rules.md`、z-coding 流程与相关模块 `AGENTS.md`（`servers/crawler`、`servers/api`、`servers/common`）。

### 1.0 现状核实结论（读码确认，需求总分支 @ `3b03d31`）

- **crawler 是 WebApplication 但零 Controller**：`servers/crawler/Program.cs` 用 `WebApplication.CreateBuilder`（L13）仅 `app.MapHealthChecks("/health")`（L137），无 `AddControllers`/`MapControllers`；`Z.Vtuber.Crawler.csproj` 已带 `FrameworkReference Microsoft.AspNetCore.App`（AGENTS.md 口径）——新增 Controller 仅需 `builder.Services.AddControllers()` + `app.MapControllers()`，无 SDK/包变更。
- **WBI 签名与取数范式现成**：`BiliApiClient.GetSpaceDynamicsAsync`（`servers/crawler/Crawlers/BiliApiClient.cs:58-110`）示范「`accountProvider.GetProviderAsync(cookieId, cookie)` → `account.Sign(parameters)`（`BiliApiClient.cs:75`，Mikibot `BilibiliAccount.Sign` = WBI 签名）→ `http.Client.GetAsync`（`BiliApiClient.cs:77`）→ `JsonElement` 自解析」；Mikibot 虽有 `BiliVideoCrawler.GetVideoInfo`（`x/web-interface/view`），但其 `VideoInfo` 模型**只有 Title/Desc/Cover/BvId/Owner、无 `pages[]`/`cid`**（Mikibot.Crawler 1.0.9 反编译核实），取 cid 必须自行按 `JsonElement` 解析响应，不能复用该模型。
- **IBiliApiClient 共 5 方法、三实现一装饰器**：`servers/crawler/Crawlers/BiliApiClient.cs:21-27`；直连 `BiliApiClient`、拟真 `BrowserishBiliApiClient`、装饰器 `ResilientBiliApiClient`（`ExecuteWithResilienceAsync` 退避 + 风控降级，`servers/crawler/Crawlers/ResilientBiliApiClient.cs:35-51`）——**新增方法需同步加到接口 + 三个类**，否则编译不过。
- **风控 handler 会整段缓冲响应体**：`RiskControlHandler.SendAsync` 用 `response.Content.ReadAsStringAsync` 读全 body 再分类（`servers/crawler/Crawlers/RiskControlHandler.cs:26`），且该 handler 挂在 `BilibiliAccountProvider` 内部 HttpClient（`BilibiliAccountProvider.cs:117`）与拟真通道——**GB 级 CDN 流式下载绝不能走该 handler**（会整段缓冲且按文本破坏二进制），CDN 流请求须用独立 HttpClient（JSON 取数请求不受影响）。
- **Cookie 领取与互斥**：`CookieServiceClient.CheckoutAsync`（`servers/crawler/Services/CookieServiceClient.cs:30-44`）→ `CookieCheckoutResult{CookieId, Cookie}`，无可用抛 `NoAvailableCookieException`；`BilibiliAccountProvider.GetProviderAsync` 内部 `gate.AcquireAsync(cookieId)`（`BilibiliAccountProvider.cs:35-38`）per-cookie 互斥（`CookieCallGate`，锁租约 5 分钟、等待 30s，`services/CookieCallGate.cs:23-31`）——取数阶段已持锁，**流式传输阶段不得持锁**。
- **api 流式代理 + Range 透传有完整先例**：`BilibiliLiveRecordingController.DownloadRecording`（`servers/api/Controllers/BilibiliLiveRecordingController.cs:127-224`）——`Range`/`If-Range` 透传（L137-146）、`HttpCompletionOption.ResponseHeadersRead`、400 透传响应体 / 404 / 416 透传 `Content-Range`（L176-186）不归并 502、成功透传 `Content-Type`/`Content-Disposition`/`Accept-Ranges`/`Content-Range`/`Content-Length`（L195-217）后 `CopyToAsync(Response.Body)`；错误体口径 `{"error":"...unavailable"}` 502。
- **api named HttpClient 注册范式**：`servers/api/Program.cs:51-70`（`RecordingService`/`TranscriptService`/`TrainingService`，配置节 `XxxService:BaseUrl` 兜底 `http://xxx`）；`infra/apphost/AppHost.cs:105-115`（`api.WithReference(xxx).WaitFor(xxx).WithEnvironment("XxxService:BaseUrl", xxx.GetEndpoint("http"))`）。
- **文件名 Sanitize 规则**：recording `RecordingFileName.Sanitize`（`servers/recording/Recording/RecordingFileName.cs:29-49`）——去控制字符与 `\ / : * ? " < > |`、压缩空白、裁剪长度、空串回退 fallback。**服务间不互引**（common 归属约定），crawler 侧复制同款规则为纯函数 `VideoDownloadFileName`。
- **契约与 codegen 现状**：`contracts/` 7 份按服务切分（recording/training/cookie/... 各自一份，api 对外口径统一在 `bilibili-crawler.openapi.yaml`）；Range 语义声明先例 `bilibili-crawler.openapi.yaml:300-364`（`components/parameters/Range`/`IfRange`、200/206 `format: binary`、416 带 `Content-Range`）；`apps/web` `npm run codegen` 从 7 份契约生成 `src/api/schema*.d.ts`（勿手改），其中 `bilibili-crawler.openapi.yaml` → `schema.d.ts`。
- **测试设施可直接扩展**：`BiliApiClientTests`（`tests/Z.Vtuber.Tests/BiliApiClientTests.cs:8-52`，`StubAccountProvider` + `MockHttpMessageHandler` + `AddBilibiliCrawlers(addHttpClient: false)` 组合出可断言的 Mikibot 客户端）；`BilibiliLiveRecordingControllerTests`（`StubHandler`/`StubHttpClientFactory` + `DefaultHttpContext` + `Response.Body = MemoryStream`，`tests/Z.Vtuber.Tests/BilibiliLiveRecordingControllerTests.cs:20-53`）；tests 已引用 `servers/crawler`（`Z.Vtuber.Tests.csproj:37`）。
- **零新增存储/依赖成立**（提案 §4 复核）：无 Mongo 集合、无存储目录、无后台任务；`ZipArchive` 类比——本需求连 BCL 压缩都不需要，仅 `HttpClient`/`Stream`。

### 1.1 口径澄清（规划期裁定，随实现评审）

1. **输入口径**：接口只收 `bvid`（path 参数）。PRD 功能点 1 描述提到「或包含 BV 号的视频链接」，本期不做链接解析（API 面只有 bvid，与 PRD 验收标准「输入合法 BV 号」一致）；如需链接容错后续按增量需求补。
2. **「都下载」形态**：不设打包端点——调用方以 `kind=video` 与 `kind=audio` 各请求一次，各自支持断点续传（提案 §6.1 决策 3）。响应 `Content-Disposition` 文件名含 `.video.`/`.audio.` 段便于配对。
3. **kind/page 校验**：`kind` 仅 `video|audio`（大小写敏感，其他 → 400）；`page` 默认 1，`<1` 或 `>pages.Length` → 400（body 含实际分 P 数）；bvid 格式以 `^BV[0-9A-Za-z]{10}$` 校验，非法 → 400。
4. **选流口径**：优先 DASH（`x/player/wbi/playurl?bvid=&cid=&qn=127&fnval=16&fourk=1`）：`dash.video[]` 取 `id`（清晰度档）降序、`bandwidth` 降序第一档，`dash.audio[]` 同理取最高档；`kind=video|audio` 各取对应数组。DASH 缺失时回退 `fnval=0`（`durl[]` 整段 flv）：`kind=video` 取 `durl[0].url`，**`kind=audio` → 400**「该视频无独立音频流（仅整段视频流）」。URL 取 `baseUrl`（`backupUrl[]` 本期不重试，主地址失败按错误映射出 502）。
5. **CDN 请求头**：`Referer: https://www.bilibili.com/` + 浏览器 UA（`BilibiliCrawlerOptions.BrowserUserAgent`，与拟真通道同源）；调用方 `Range`/`If-Range` 原样透传上游。
6. **流式通道与风控隔离**：CDN 流用**独立 HttpClient**（仅 Referer/UA，不挂 `RiskControlHandler`/不走拟真通道）——`RiskControlHandler` 的 `ReadAsStringAsync` 缓冲语义（§1.0）与 GB 级流冲突；JSON 取数（view/playurl）走既有 resilient 通道不变。
7. **CookieCallGate 只覆盖取数**：`GetVideoPlayInfoAsync` 内部（经 `BilibiliAccountProvider`）天然持锁取数；拿到流地址后锁即释放，**CDN 流式传输全程不持 cookie 锁**，避免 GB 级下载长期独占 `cookie-call:{cookieId}` 阻塞其他抓取。
8. **超时口径（长连接）**：CDN 流式 HttpClient 显式 `Timeout = Timeout.InfiniteTimeSpan`，以 `RequestAborted` 联动取消（客户端断开即取消上游，无悬挂下载）；api 侧 `CrawlerService` named client 同样禁用整体超时（GB 级传输不得被默认 100s 截断），传输失败按 `HttpRequestException`/`IOException` → 502。该口径沿用「ResponseHeadersRead + 首字节即时」既有讨论（`20261007-plan-training-dataset-export-zip.md` §6），本需求显式设为 Infinite 更稳妥。
9. **响应头与文件名**：成功响应 `Content-Type: video/mp4`（m4v）/ `audio/mp4`（m4a）/ `video/x-flv`（flv）；`Content-Disposition: attachment; filename*=UTF-8''{Uri.EscapeDataString(文件名)}`，文件名模板 `{Sanitize(标题,80,"未命名视频")}.p{page:D2}.{kind}.{ext}`（ext：DASH video→`m4v`、audio→`m4a`、flv 回退→`flv`）；`Accept-Ranges: bytes` 与上游 `Content-Length`/`Content-Range` 原样回传；416 透传上游 `Content-Range: bytes */{total}`。
10. **错误映射表（统一 `{ "error": "<中文原因>" }`）**：

    | 情况 | 状态 | error 文案 |
    |------|------|-----------|
    | bvid/kind 非法、page 非法 | 400 | `参数非法：…` |
    | page 越界 | 400 | `分 P 越界：该视频共 {N} P` |
    | flv 回退且 kind=audio | 400 | `该视频无独立音频流（仅整段视频流），无法只下载音频` |
    | view 返回 -404（不存在/已删除/私密） | 404 | `视频不存在或不可访问` |
    | `RiskControlException`（风控） | 502 | `B 站风控受限，请稍后重试` |
    | `NoAvailableCookieException`/`CookieSessionInvalidException` | 502 | `暂无可用 B 站账号，无法取流` |
    | playurl 返回非 0（含 -403 权限受限） | 502 | `取流失败：权限受限或该视频不可播放` |
    | CDN 请求失败 / 上游不可达 | 502 | `取流失败：上游服务不可用`（api 侧 `crawler service unavailable` 同义） |

    B 站 API code → 文案映射收敛为 `VideoStreamService` 内纯函数（可单测）；api 代理对 400/404/416/502 按 `DownloadRecording` 先例透传，不归并。
11. **不落盘红线**：任何路径不得写磁盘/数据库/缓存——实现面只有 `HttpClient` + `Stream.CopyToAsync`；测试含「临时目录零新增文件 + 全仓 Video 路径无 FileStream/WriteAll\* 用例」（提案 §8.1）。
12. **契约与 codegen**：新增 `contracts/video-download.openapi.yaml`（crawler 对内契约，前缀 `/api/video-download`，本服务首个契约）；更新 `contracts/bilibili-crawler.openapi.yaml`（api 对外 `/api/b/video/{bvid}/download`，Range 语义按 `bilibili-crawler.openapi.yaml:300-364` 先例）。`bilibili-crawler.openapi.yaml` 变更后 `npm run codegen` 再生成 `schema.d.ts`（勿手改，复核 0 diff）；**`video-download.openapi.yaml` 不进 web codegen 清单**（web 本期无消费方，避免无用生成物）。
13. **零新增依赖与配置**：无 NuGet/npm 新包；新增配置仅 api `CrawlerService:BaseUrl`（named client + apphost 环境变量）；无 DB/存储卷/流水线变更（crawler 镜像不变，仍 EXPOSE 8080）。

### 1.2 实施范围

- **做**：`contracts/`（video-download 新增 + bilibili-crawler 更新 + web codegen 生成物）；`servers/crawler`（`Video/` 三文件、`Crawlers/BiliApiClient` 扩展、`Program.cs` 控制器注册与流式 HttpClient）；`servers/api`（`BilibiliVideoDownloadController` + `Program.cs` named client）；`infra/apphost`（api → crawler 服务发现）；`tests/Z.Vtuber.Tests`（`VideoDownloadFileNameTests`/`VideoStreamServiceTests`/`BiliApiClientVideoTests`/`VideoDownloadApiTests`/`BilibiliVideoDownloadControllerTests`）；文档同步（`servers/crawler/AGENTS.md`、`servers/api/AGENTS.md`、PRD 验收勾选、提案/Plan 互链）。
- **不做**：前端页面（PRD §3.4）；转码/合并/重封装；落盘归档与任务管理；批量/合集/追剧下载；清晰度手动选择（默认账号可得最高档）；`backupUrl` 失败重试与多 CDN 调度；playurl 地址缓存；鉴权/配额（对外接口与既有 `/api/b` 同信任域）；cookie 服务改动。

## 2. 分支与提交流程

- 需求总分支 `feat/bilibili-video-download`（基于 origin/main，PRD 与技术提案已随 `3b03d31` 提交，需求总 PR #181）：本 Plan 提交到该分支随 PR 评审；实现提交同入该分支（提案 §头 feat-branch 同名），如需隔离评审可从需求总分支切出 `feat/bilibili-video-download-impl` 以 PR 回需求总分支（同 `feat/dataset-model-capability-impl` → #168 先例）。**实际实现走隔离分支：`feat/bilibili-video-download-impl` → 需求总分支，PR #182。**
- 提交顺序（Conventional Commits，测试与实现分开）：
  1. `docs(bilibili-video-download):` 本实现 Plan（需求总分支）；
  2. `feat(contracts):` 契约变更（video-download 新增 + bilibili-crawler 更新）+ `npm run codegen`；
  3. `feat(crawler):` 取流扩展 + 流式透传端点（`IBiliApiClient.GetVideoPlayInfoAsync`、`VideoStreamService`、`VideoDownloadController`、`Program.cs`）+ `test(crawler):` 配套用例；
  4. `feat(api):` `BilibiliVideoDownloadController` 代理 + `CrawlerService` named client + apphost 接线 + `test(api):` 透传用例；
  5. `docs(bilibili-video-download):` `AGENTS.md`（crawler/api）/ PRD 验收勾选 / 提案与 Plan 互链同步。

## 3. 实施阶段

### Phase 0: 技术文档（先行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | PRD（上游节点产出） | `docs/product/draft/20261008-bilibili-video-download-prd.md` | 已完成（`3b03d31`，v1.1 不落盘口径） |
| 0.2 | 技术提案（上游节点产出） | `docs/engineering/proposals/20261008-proposal-bilibili-video-download.md` | 已完成（`3b03d31`，v1.1） |
| 0.3 | 实现 Plan（本文档） | `docs/engineering/plans/20261008-plan-bilibili-video-download.md` | 任务/文件/验收明确，与提案口径一致（§1.1 偏差已标注） |

### Phase 1: 契约先行

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | 新增 `GET /api/video-download/{bvid}`（operationId `downloadVideoStream`）：query `kind`（必填 `video|audio`）、`page`（可选默认 1）；200 `video/mp4`/`audio/mp4`/`video/x-flv` `format: binary`，声明 `Content-Disposition: attachment`、`Accept-Ranges: bytes`；206 带 `Content-Range`；416 带 `Content-Range`；400/404/502 `$ref` 错误体 `{error: 中文原因}`（口径与 §1.1 口径 10 逐字一致） | `contracts/video-download.openapi.yaml` | 契约自洽，可被 openapi-typescript 解析 |
| 1.2 | 更新 `/api/b/video/{bvid}/download`：同 1.1 镜像 + `Range`/`If-Range` 参数 `$ref`（照抄 `contracts/bilibili-crawler.openapi.yaml:300-364` 写法）+ 502 `crawler service unavailable` 口径 | `contracts/bilibili-crawler.openapi.yaml` | 与 1.1 语义一致 |
| 1.3 | `npm run codegen` 再生成 `schema.d.ts`（勿手改；`video-download.openapi.yaml` 不进清单，口径 12） | `apps/web/src/api/` | 生成物与契约一致、复核 0 diff |

### Phase 2: crawler 取流与流式透传（TDD：测试先行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | 纯函数用例先行（红）：`VideoDownloadFileNameTests`（Sanitize 同款规则/中文标题/空回退/`filename*` 编码）、`VideoStreamServiceTests` 选流部分（DASH 最高档选取、kind 过滤、flv 回退、kind=audio 无音频→错误）、错误映射函数（bilibili code → §1.1 口径 10 文案） | `tests/Z.Vtuber.Tests/VideoDownloadFileNameTests.cs`、`VideoStreamServiceTests.cs` | 用例先行落地并失败 |
| 2.2 | `IBiliApiClient` 新增 `GetVideoPlayInfoAsync(cookieId, cookie, bvid, page, ct)` → `VideoPlayInfo`（含 `Title`、`Cid`、`PageCount`、`Streams: [{Kind, Url, ContentType, Ext}]` 或等价不可变记录）；三实现同步：直连/拟真复用 `JsonElement` 自解析 `x/web-interface/view`（取 `pages[page-1].cid` 与 `title`，page 越界抛带 N 的领域异常）+ `x/player/wbi/playurl`（`account.Sign` WBI，口径 4 选流）；`ResilientBiliApiClient` 加 `ExecuteWithResilienceAsync` 包装（target `video:{bvid}`） | `servers/crawler/Crawlers/BiliApiClient.cs`、`BrowserishBiliApiClient.cs`、`ResilientBiliApiClient.cs` | 既有 5 方法零 diff；`BiliApiClientVideoTests`（`StubAccountProvider` + `MockHttpMessageHandler` 断言 WBI 查询串/解析 DASH/解析 flv/非 0 code 映射）转绿 |
| 2.3 | `VideoStreamService`：编排 Checkout（`ICookieServiceClient`）→ `GetVideoPlayInfoAsync` → 模式过滤/校验（口径 3/4/10）→ 返回「响应头元数据 + 上游流打开委托」；CDN 流经**独立 HttpClient**（口径 6/8：Referer+UA、`Timeout=Infinite`、`ResponseHeadersRead`、Range 透传）打开，异常按口径 10 映射 | `servers/crawler/Video/VideoStreamService.cs` | 2.1 服务用例转绿 |
| 2.4 | `VideoDownloadController`：`[ApiController][Route("api/video-download")]`，action 校验入参 → `VideoStreamService` 取流 → 回写状态码与 `Content-Type`/`Content-Disposition`（`VideoDownloadFileName`）/`Accept-Ranges`/`Content-Range`/`Content-Length` → `CopyToAsync(Response.Body)`，`RequestAborted` 联动取消上游与传输；错误路径统一 `{error}` JSON。`Program.cs`：`AddControllers()` + `MapControllers()` + `VideoStreamService`/流式 HttpClient 注册 | `servers/crawler/Video/VideoDownloadController.cs`、`Video/VideoDownloadFileName.cs`、`servers/crawler/Program.cs` | 2.1/2.3 全绿 |
| 2.5 | 关键用例矩阵（`VideoDownloadApiTests`，`DefaultHttpContext` + `Response.Body = MemoryStream` + 上游 Stub 流）：① kind=video DASH → 200 `video/mp4` + 字节与 stub 一致 + `Content-Disposition` 含 `.video.m4v`；② kind=audio → 200 `audio/mp4` + `.audio.m4a`；③ flv 回退 kind=video → `video/x-flv`；④ flv 回退 kind=audio → 400；⑤ bvid 非法/kind 非法 → 400；⑥ page 越界 → 400（含 N）；⑦ view -404 → 404；⑧ 风控 → 502 文案；⑨ 无可用 Cookie → 502；⑩ Range 透传（上游 206 → 206 + `Content-Range`/`Accept-Ranges`/`Content-Length` 一致）、416 透传；⑪ 客户端中断（`RequestAborted`）→ 上游请求被取消、无残留；⑫ 不落盘验证（临时目录零新增文件） | `tests/Z.Vtuber.Tests/VideoDownloadApiTests.cs` | 全绿，覆盖口径 3-11 |

### Phase 3: api 代理镜像

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 3.1 | `BilibiliVideoDownloadController`（`[Route("api/b/video")]`）`GET {bvid}/download` → named client `CrawlerService` 请求 `api/video-download/{bvid}{Request.QueryString}`：`Range`/`If-Range` 透传（照抄 `DownloadRecording` L137-146）、`ResponseHeadersRead` + `CopyToAsync`；状态映射 400 透传响应体、404、416 透传 `Content-Range`、200/206 透传 `Content-Type`/`Content-Disposition`/`Accept-Ranges`/`Content-Range`/`Content-Length`；不可达/超时 502 `{"error":"crawler service unavailable"}` | `servers/api/Controllers/BilibiliVideoDownloadController.cs` | 镜像面 +1，其余控制器零 diff |
| 3.2 | `Program.cs` 注册 named client（`CrawlerService:BaseUrl` 兜底 `http://crawler`，超时 Infinite，口径 8）；`infra/apphost/AppHost.cs` 增 `api.WithReference(crawler).WaitFor(crawler).WithEnvironment("CrawlerService:BaseUrl", crawler.GetEndpoint("http"))` | `servers/api/Program.cs`、`infra/apphost/AppHost.cs` | Aspire 编排下 api 可达 crawler `/health` 与下载端点 |
| 3.3 | 透传用例（`BilibiliVideoDownloadControllerTests`，`StubHandler`/`StubHttpClientFactory` 先例）：200/206 字节与头透传（含 `Content-Disposition`）、400 错误体透传、404、416、上游不可达 502、Range 请求头到达上游 | `tests/Z.Vtuber.Tests/BilibiliVideoDownloadControllerTests.cs` | 新用例转绿 |

### Phase 4: 测试收口与文档同步

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 4.1 | 全量回归：`dotnet build` + podman 容器 UT（Mongo/Redis）；既有 `BiliApiClient*`/`BilibiliLiveRecording*`/`CrawlerHealthCheck*` 测试不回退 | 全部 | 全绿 |
| 4.2 | 跑测口径：trx + `tests/Z.Vtuber.TestReport --check`（单用例 ≤ 5s；真实流式长耗时一律用 Stub 流，不新增 `TimeoutExceptions.json` 登记） | 全部 | 通过 |
| 4.3 | 覆盖率：核心逻辑（选流、校验、错误映射、文件名、Range/416 透传分支）≥ 80% | 全部 | 达标 |
| 4.4 | `npm run codegen` 复核 0 diff（生成物勿手改） | `apps/web/src/api/` | 一致 |
| 4.5 | 文档同步：`servers/crawler/AGENTS.md`（模块结构加 `Video/`、控制器与流式端点、Cookie/风控口径）、`servers/api/AGENTS.md`（`/api/b/video/**` 代理清单与透传口径）、PRD 验收项勾选、提案/Plan 互链 | 文档 | 与实现一致 |

## 4. 并行策略

- Phase 1 顺序 1.1 → 1.2 → 1.3；1.1/1.2 定稿后 Phase 2 与 Phase 3 可并行（文件面互不重叠：crawler / api+apphost），Phase 3.3 的上游 Stub 契约以 1.1 为准。
- Phase 2 内部顺序 2.1 → 2.2 → 2.3 → 2.4（2.5 随 2.4 收口）；2.2 接口签名先行，避免 2.1 长期红。
- Phase 4 收口最后；4.5 文档同步在实现定稿后。

## 5. 验收对照

| PRD/提案目标 | 实现任务 | 验证 |
|--------------|---------|------|
| 功能点 1：三种模式返回对应原始流（m4v/m4a/flv），字节与 B 站一致、可直接播放 | 2.2（取流）、2.3/2.4（透传）、2.5①②③ | `VideoDownloadApiTests` 字节一致断言；真实 BV 号冒烟（不进 CI） |
| 功能点 1：多 P 默认第 1 P、越界明确错误、非法输入明确错误 | 2.3、2.5⑤⑥ | 对应用例 |
| 功能点 1：「都下载」两份流互不缺失 | 口径 2（两次独立请求） | 2.5①② + 冒烟两份拼接验证 |
| 功能点 2：断点续传（206/416、续传拼接与完整下载一致） | 2.4/2.5⑩、3.1/3.3 | Range 透传用例（200/206/416）；冒烟 curl `-r` 拼接比对 |
| 功能点 3：失败原因可见（不存在/权限/风控） | 口径 10 映射、2.5⑦⑧⑨ | 错误文案用例 |
| 成功标准：平台无落盘残留 | 口径 11、2.5⑫ | 临时目录零新增；代码面无写盘 API |
| 契约先行 + api 镜像一致 | 1.1–1.3、3.1 | 4.4 codegen 0 diff；契约评审 |
| 回归不回退、覆盖率 ≥ 80%、单用例 ≤ 5s | 4.1–4.3 | 4.1–4.3 |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|----|------|------|
| `RiskControlHandler` 缓冲整条响应 | CDN 流若误走该 handler 会被 `ReadAsStringAsync` 整段缓冲并破坏二进制（`RiskControlHandler.cs:26`） | 口径 6：CDN 流独立 HttpClient；代码评审与用例（2.5① 字节一致）守护 |
| GB 级长连接与超时 | 默认 HttpClient 超时截断长下载 | 口径 8：两端 `Timeout=Infinite` + `RequestAborted` 联动取消；失败按 502 |
| Cookie 互斥被长下载占用 | `CookieCallGate` 租约 5 分钟 | 口径 7：锁只覆盖取数；流式阶段不持锁 |
| 风控/限流 | 大量下载可能触发 B 站风控 | 复用 resilient 通道（target `video:{bvid}`）+ `RiskControlException`→502 文案，不无限重试 |
| 未登录/普通账号清晰度受限 | qn=127 请求但实际返回档位较低 | 选流按响应内实际档位取最高（口径 4）；权限受限映射 502 可读原因 |
| CDN 地址时效/防盗链 | playurl 返回的 URL 有时效与 Referer 校验 | 每次实时取地址（无缓存，提案 §6.1）；请求带 Referer+UA（口径 5）；主地址失败按 502（backupUrl 本期不做，§1.2 不做清单） |
| flv 回退视频无独立音频流 | `kind=audio` 无值可取 | 400 明确文案（口径 4/10），不静默降级 |
| 并发下载 | 同视频并发重复下载 | 各自独立实时取流（PRD §3.5）；无共享状态、互不影响 |
| crawler 新增对外端点面 | Worker 服务首次挂 Controller | 仅 `/api/video-download/**` 一个路由面；`/health` 不动；部署顺序先 crawler 后 api（纯新增，旧版无感知、回滚无持久化副作用） |

## 7. 相关文档

- PRD：`docs/product/draft/20261008-bilibili-video-download-prd.md`
- 技术提案：`docs/engineering/proposals/20261008-proposal-bilibili-video-download.md`
- 参考范式：`docs/engineering/proposals/20260914-proposal-recording-download-range.md`（Range 端到端透传语义）、`docs/engineering/plans/20261007-plan-training-dataset-export-zip.md`（流式下载/文件名/代理先例）
- 模块规范：`servers/crawler/AGENTS.md`、`servers/api/AGENTS.md`、`servers/common/AGENTS.md`
- 规范：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`
- 契约：`contracts/video-download.openapi.yaml`（新增）、`contracts/bilibili-crawler.openapi.yaml`
