# 后端开发规范

本文档描述 Z.Vtuber 项目后端（C# / .NET）的统一规范。

## 技术栈

- 语言：C# (.NET 10)
- 框架：ASP.NET Core Web API
- API 文档：Microsoft.AspNetCore.OpenApi
- 测试框架：xUnit

## 初始化指令

```bash
dotnet new webapi -n Z.Vtuber -o servers/api --no-https --use-controllers
```

## 项目结构

```
servers/
├── Z.Vtuber.slnx                # 解决方案：api/common/crawler/cookie/recording/recording-transcript-* + tests + apphost
├── api/                         # 纯 API 服务（Z.Vtuber）
│   ├── Controllers/             # API 控制器
│   ├── Live/LiveEventBus.cs     # SSE 订阅门控（Redis pub/sub 驱动，按需订阅/退订）
│   ├── Program.cs               # 应用入口
│   └── Z.Vtuber.csproj          # 引用 common + 消息客户端
├── common/                      # 共享类库（Z.Vtuber.Common）
│   ├── Persistence/             # 实体/仓储/MongoDbInitializer/BsonConvert/聚合扩展
│   ├── Services/                # 共享领域服务 + ICookieNavProbeService 契约
│   ├── Live/                    # LiveEvent 模型/提取器/频道名约定
│   └── Z.Vtuber.Common.csproj
└── crawler/                     # 抓取服务（Z.Vtuber.Crawler）
    ├── Crawlers/                # 抓取通道/风控/退避
    ├── Jobs/                    # 周期任务 + 基类 + 锁
    ├── Live/                    # LiveEventSource/弹幕客户端/归一化/事件发布
    ├── Services/                # AccountInfoCrawler/CookieNavProbeService
    ├── Configuration/           # BilibiliCrawlerOptions
    ├── Program.cs               # Worker 宿主 + /health 健康检查
    ├── Dockerfile
    └── Z.Vtuber.Crawler.csproj  # 引用 common + 抓取库 + 消息客户端
├── cookie/                      # Cookie 管理服务（Z.Vtuber.Cookie，扫码登录/池调度）
├── recording/                   # 录播服务（Z.Vtuber.Recording）
│   ├── Subscriptions/           # （订阅读取内联于 LiveStatusPoller）
│   ├── Live/                    # 开播轮询/playurl 选流/直播 API 客户端
│   ├── Recording/               # ffmpeg 多输出编排/文件名/分布式锁/状态上报/场次归档事件/存量清洗
│   ├── Audio/                   # 实时音频 Hub（有界缓冲丢弃广播）
│   ├── Controllers/             # stream/audio、recordings、recording/status
│   ├── Configuration/           # RecordingOptions
│   ├── Program.cs               # Web 宿主 + /health 健康检查
│   ├── Dockerfile               # aspnet 运行时 + ffmpeg
│   └── Z.Vtuber.Recording.csproj
├── recording-transcript-consumer/ # 转写消费服务（Z.Vtuber.RecordingTranscript.Consumer，本地 8085）
│   ├── Events/                  # 订阅 recording-started/finished 做幂等 enqueue
│   ├── Enqueue/                 # 幂等建任务（事件/对账/手动共用）
│   ├── Reconcile/               # 周期性对账补建漏建场次
│   ├── Tasks/                   # 扫描器/Redis 任务锁/执行器/segment 落库与发布
│   ├── Asr/                     # IStreamingAsrService 抽象 + FunASR websocket 实现
│   ├── Audio/                   # ffmpeg 双音源（实时流/文件 seek）s16le pipe
│   ├── Services/                # RecordingServiceClient
│   ├── Configuration/           # TranscriptConsumerOptions / RecordingServiceOptions
│   ├── Program.cs               # Web 宿主 + /health 健康检查
│   └── Z.Vtuber.RecordingTranscript.Consumer.csproj
└── recording-transcript-api/    # 转写查询服务（Z.Vtuber.RecordingTranscript.Api，本地 8084）
    ├── Controllers/             # recordings/tasks/settings/models
    ├── Sse/TranscriptSegmentStream.cs # replay + Redis 订阅 + 去重
    ├── Contracts/               # Dtos/TranscriptMapping
    ├── Configuration/           # TranscriptApiOptions
    ├── Program.cs               # Web 宿主 + /health 健康检查
    └── Z.Vtuber.RecordingTranscript.Api.csproj
```

> 归属约定：仅被一方使用的代码归该方；被双方使用的代码入 `common`。抓取专用在 `servers/crawler`，对外接口与 SSE 在 `servers/api`。`ICookieNavProbeService` 契约在 common，具体实现（依赖抓取通道）在 crawler。

## 契约（codegen）

- 契约文件存放于 `contracts/` 目录（OpenAPI YAML）。
- 后端为契约的生成源，前端基于契约生成客户端。

## 测试框架

- 使用 xUnit 编写单元测试和集成测试。
- 测试项目位于 `tests/` 目录。

## 相关规范引用

- `docs/engineering/common-rules.md`
- `docs/engineering/backend-rules.md`
