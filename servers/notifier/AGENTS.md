# Z.Vtuber.Notifier

## 技术栈说明

- 语言：C# (.NET 10)
- 类型：Worker Service（`Microsoft.NET.Sdk.Worker`）
- 依赖：`Z.Vtuber.Common.Core`（契约/基础设施）+ `Z.Vtuber.Common.Notification`（通知订阅实体与仓储）
- 消息通道：`ZeroAsh.Milky.Net.Client` 1.0.1-preview.4（fork 包，因官方 Milky.Net.Client 3.0.0 的 TextOutgoingSegment 序列化为扁平结构，与 LLBot 期望的 data 包装格式不兼容）

## 定位

独立部署的通知投递服务，负责：
1. 通过 MongoDB 持久化通知订阅配置
2. 定期检查 B 站直播状态变化
3. 通过 Milky 协议向 QQ 群发送通知消息

## 模块结构

- `Milky/`：Milky 协议接入
  - `IMilkyApiClient`：协议接口（定义 `GetLoginInfoAsync`、`GetGroupListAsync`、`SendGroupMessageAsync`）
  - `MilkyApiClient`：基于 `Milky.Net.Client.MilkyClient` 封装实现
  - `MilkyOptions`：配置选项（`Endpoint`、`AccessToken`）
- `Dispatch/`：通知投递调度
  - `NotificationDispatchService`：消费 `cookie-invalid` 事件，按事件 `platform` 映射 eventType（`bilibili`/空 → `bilibili_cookie_invalid`，`weibo` → `weibo_cookie_invalid`）匹配订阅投递，文案带平台名与账号标识
- `Events/`：`CookieInvalidEventSubscriber`：订阅 Redis 频道 `cookie-invalid`（单频道，原样转发 dispatch）
- `Controllers/`：配置管理 API
  - `NotifyConfigController`：提供群列表查询与订阅 CRUD 接口；eventType 仅接受 `bilibili_cookie_invalid`/`weibo_cookie_invalid`
- `Program.cs`：服务启动与 DI 注册；启动时 `EnsureIndexesAsync` 后调用 `MigrateCookieInvalidEventTypeSplitAsync`（存量 `cookie_invalid` 订阅幂等迁移为 `bilibili_cookie_invalid`）

## 环境变量

| 变量名 | 说明 | 示例 |
|--------|------|------|
| `Z_VTUBER_MILKY_ENDPOINT` | Milky 协议端点地址 | `http://localhost:8080` |
| `Z_VTUBER_MILKY_ACCESS_TOKEN` | Milky 访问令牌 | `your-token` |
| `ConnectionStrings:mongo` | MongoDB 连接串 | `mongodb://localhost:27017` |

## 开发流程

- TDD 开发（单元测试：`tests/Z.Vtuber.Tests`）
- 参考：`docs/engineering/common-rules.md`、`docs/engineering/backend-rules.md`
