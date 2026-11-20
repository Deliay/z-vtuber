# servers/auth（Z.Vtuber.Auth）

B 站弹幕帐号认证服务：为多站点统一登录提供 Casdoor Custom OAuth Provider 与内嵌验证页/账号中心。
本地端口 `8087`，镜像 `zero-tools/z-vtuber-auth`。

## 定位与边界

- **负责**：消费配置房间的既有 SSE/range 事件源、一次性弹幕 token、B 站发送者解析、`auth_users` 绑定、Provider Ticket/Provider Session/授权码/token、内嵌 `/connect/bilibili` 与 `/account` 页面。
- **不负责**：抓取/订阅管理（不修改 `servers/crawler`）、业务站点会话、Casdoor 源码/前端插件、密码/邮箱注册、B 站 OAuth。
- **不改动**：`servers/crawler/**`、`servers/api/**`、`apps/web/**`。auth 只读既有事件源：
  - `GET /api/b/live/{roomId}/event/subscribe?type=danmaku`
  - `GET /api/b/live/{roomId}/event/range`

## 模块

| 目录 | 说明 |
|--|--|
| `Auth/` | token 生成/HMAC、returnTo 校验、登录状态机、用户绑定、Provider Session/Ticket、standalone 会话、限流、审计 |
| `Danmaku/` | SSE 帧解析、发送者/头像解析、补投、去重、每房间 Redis 选主、事件源 Host、健康注册表 |
| `Persistence/` | 8 类集合记录、仓储、`AuthMongoSetup` 索引/TTL |
| `OAuth/` | authorize/token/userinfo/logout、PKCE、授权码与 Provider Token |
| `Controllers/` | 账号中心 API、房间状态、页面、健康 |
| `wwwroot/` | `/connect/bilibili`、`/account` 静态页面（无 Node/Razor） |
| `Configuration/` | `Auth`、`Auth:Danmaku`、`OAuthProvider`、`Casdoor` 配置与校验 |

## 关键路由

| 方法 | 路径 | 说明 |
|--|--|--|
| GET | `/connect/bilibili?return_to=` | 弹幕验证/注册页 |
| GET | `/account` | 账号中心 |
| GET | `/api/auth/danmaku/rooms` | 房间 `role/connected/available/lastHeartbeatAt/lastEventAt` |
| POST | `/api/auth/login-requests` | 创建 10 位一次性 token（全部房间不可用 503） |
| GET/POST | `/api/auth/login-requests/{id}`、`.../confirm`、`.../cancel` | 查询/确认/取消，`X-Poll-Token` |
| GET | `/api/auth/me` | Provider Session 或 Bearer |
| POST | `/api/auth/provider-sessions/logout` | 撤销验证会话 |
| POST | `/api/auth/sessions/refresh`、`/logout` | 仅 standalone |
| GET/POST | `/oauth/authorize`、`/oauth/token`、`/oauth/userinfo`、`/oauth/logout` | Casdoor Provider（standalone 下 404） |
| GET | `/health`、`/health/ready` | 存活 / Mongo+Redis+至少一个房间就绪 |

## 配置

见 `appsettings.json` 与 `docs/engineering/plans/20261119-plan-danmaku-account-auth.md`。

- `Auth:Mode`：`casdoor`（生产）/`standalone`（本地/降级）。
- `Auth:TokenPepper`：≥32 字符，生产必须通过密钥注入；**禁止**提交真实值。
- `Auth:PublicBaseUrl`：决定 Cookie `Secure`；HTTPS 生产必配。
- `Auth:ConfirmOperationTimeoutSeconds`：`confirming` 最长停留时间（默认 30s），超时由扫描器回退 `verified`。
- `Auth:RateLimit:TokenPerHourPerClient`：外部 client 的小时配额（默认 10000）；内置验证页不发送 `clientId`，只受每 IP 分钟/日配额约束。
- `Auth:Danmaku:RoomIds`：JSON `string[]`，默认 `["30066"]`；roomId 全程字符串。
- `Auth:Danmaku:BaseUrl`：事件源内网地址。
- `OAuthProvider:ClientId/ClientSecret/RedirectUri`：Casdoor Custom Provider，`RedirectUri` 精确匹配。
- `Casdoor:ReauthorizeUrl`：账号中心「重新验证」跳转入口；未配置时按钮隐藏。
- `Casdoor:LogoutUrl`：Casdoor RP-initiated logout（通常 `{Endpoint}/api/logout`）；未配置时账号中心仅退出本服务会话。
- `Casdoor:LogoutClientId`：可选，Casdoor Application 的 Client ID（用于无 id_token_hint 时定位应用）。
- `Casdoor:PostLogoutRedirectUri`：Casdoor 登出回跳地址，必须登记在 Casdoor Application 的 Redirect URLs。
- `Auth:AllowDegradedRedis`：Redis 不可用时是否所有实例都订阅；生产建议 `false`。
- `Auth:TrustForwardedHeaders`：反代后启用 X-Forwarded-For/Proto，默认关闭。

## 事件源前置条件

auth 不创建 crawler 订阅。上线前确认 `RoomIds` 中每个房间在事件源已有 Danmaku 数据（心跳之外有弹幕事件）；否则：

- `/api/auth/danmaku/rooms` 显示 `available=false`；
- 全部房间不可用时创建 token 返回 `503 AUTH_DANMAKU_SOURCE_UNAVAILABLE`；
- `/health/ready` 不就绪。

## 安全约定

- token/pollToken/ticket/授权码/access/refresh 只存 HMAC 摘要，不写日志/审计/DB 明文。
- Provider Ticket 60s、一次性；授权码 5 分钟、一次性、绑定 client/redirect/PKCE；Provider Session 30 天、`HttpOnly; SameSite=Lax; Secure` 按公网协议。
- `returnTo` 仅允许 `/oauth/authorize` 相对 URL，并校验 `client_id/redirect_uri/response_type`；`redirect_uri` 精确匹配。
- 页面 JS 只用 `textContent`，禁止 `innerHTML`/`localStorage`；CSP 同源。
- 限流 fail-open 仅限 Redis 故障，token 本身由 58 bit 熵 + 5 分钟 + 一次性兜底。

## 可观测性

- 指标（`System.Diagnostics.Metrics`，Meter `Z.Vtuber.Auth`）：`auth.login.created/verified/confirmed/failed`、`auth.danmaku.room.connects/reconnects/backfill.events/backfill.truncated`、`auth.oauth.authorize/token/failures`。
- 审计事件（`auth_audit_logs`，90 天 TTL，脱敏）：`login_request_created/danmaku_verified/confirm_succeeded/confirm_failed/cancelled/expired/rate_limited/oauth_authorize/oauth_token_issued/oauth_userinfo/logout`。
- 审计/日志禁止写入 token、pollToken、ticket、授权码、Authorization 或明文密钥。

## 测试

```bash
dotnet test tests/Z.Vtuber.Tests --filter "FullyQualifiedName~Auth|FullyQualifiedName~Danmaku|FullyQualifiedName~Pkce|FullyQualifiedName~ReturnTo|FullyQualifiedName~RoomLeaderLease"
```

- 单元/集成：core primitives、状态机（含 confirming 超时回退 / failed 终态 / 取消边界）、持久化 CAS、多房间事件源、账号中心 API、OAuth Provider、页面安全头。
- HTTP 端到端：`AuthFakes/FakeDanmakuSource` + `AuthFakes/AuthApiFactory` 启动真实 Kestrel，覆盖单/多房间全链路、断线重连、补投、follower 可用性与 CORS 边界。
- 安全：`AuthSecurityTests`（DB/审计无明文、ticket/code/token 重放篡改、OAuth/账号中心错误体分离）、`AuthRedisRateLimiterTests`（计数与 fail-open）、`DanmakuHealthRegistryTests`（就绪矩阵）。
- 页面/契约行为：`AuthPageTests` 覆盖重新验证先注销、退出返回 Casdoor 登出地址、内置页不共享 client 桶、确认 409 可重试。
- 慢用例须登记 `tests/Z.Vtuber.Tests/TimeoutExceptions.json`；单用例 ≤5s。

## Casdoor 配置核对清单

- [ ] 同一 Organization 下创建站点 Application A/B。
- [ ] Identity > Providers 新建 Custom OAuth Provider：Client ID/Secret 与 `OAuthProvider:*` 一致。
- [ ] Auth URL=`{Auth:PublicBaseUrl}/oauth/authorize`，Token URL=`.../oauth/token`，UserInfo URL=`.../oauth/userinfo`，Logout URL 可选。
- [ ] Redirect URI 精确等于 `OAuthProvider:RedirectUri`（Casdoor `<endpoint>/callback`）。
- [ ] Scope `openid profile`；建议开启 PKCE（S256）。
- [ ] User mapping：ID=`sub`、Username=`preferred_username`、DisplayName=`name`、Avatar=`picture`。
- [ ] Provider 加入每个站点 Application，允许 Sign in/Sign up。
- [ ] 如需自动 SSO：配置 Home/Signin session/Auto signin，站点实现 `silentSignin=1`。
- [ ] 联调：首次注册 → 站点 A 登录 → 站点 B SSO；清 Casdoor 会话保留 Provider Session → 静默；清 Provider Session → 重验仍同一用户；退出。
- [ ] `Casdoor:LogoutUrl={endpoint}/api/logout`，`PostLogoutRedirectUri` 已加入 Application Redirect URLs；账号中心退出后其他站点按 Casdoor 会话策略失效。
