# 安全设计要点

> 从 [README](../README.md) 拆出。

| 项 | 做法 |
|----|------|
| 授权码拦截 | 强制 PKCE（`RequireProofKeyForCodeExchange`），public 客户端无密钥也安全。**注意**：OpenIddict 7.7.2 仍无 builder API 限制 `code_challenge_method`（升级后已核对），发现文档会同时声明 `plain` 与 `S256`；客户端必须用 **S256**（见[待办](../README.md#待办与扩展方向)） |
| 刷新令牌 | 每次刷新旋转，宽限期 0；**复用已旋转的旧令牌会撤销该授权下全部令牌**（重放防护） |
| 令牌撤销 | `EnableTokenEntryValidation()` 让每次校验都查令牌状态，未过期的 JWT 也能立即失效 |
| 令牌形态 | `DisableAccessTokenEncryption()` 产出 JWS 而非 JWE，下游可凭 `/.well-known/jwks` 离线校验 |
| 令牌有效期 | Access 1h、ID 30min、授权码 5min、刷新 30d（可按需在 `Extensions/OpenIddictServerExtensions.cs` 调整） |
| 登录防爆破 | 限流 `LoginRequestsPerMinute`（默认 10 次 / 5 分钟），同时作用于 HTML 登录表单与 `/api/account/*`；再叠加 Identity 锁定（5 次失败锁 15 分钟） |
| 令牌端点限流 | `/connect/*` 前缀单独限流，**默认额度更严**（60/min）；其余接口放宽 10 倍 |
| 会话 Cookie | `HttpOnly` + `SameSite=Lax` + 按需 `Secure`；密码/角色变更经安全戳 5 分钟内失效 |
| CSRF | 登录、同意等表单带防伪令牌（`[ValidateAntiForgeryToken]`）；API 写操作不靠表单 |
| 响应头 | `X-Content-Type-Options`、`X-Frame-Options: DENY`、CSP（`frame-ancestors 'none'`；`script-src 'self'`，仅 `/scalar` 文档 UI 因 Scalar 的内联初始化脚本额外放行 `'unsafe-inline'`） |
| 异常信息 | 生产环境异常详情只进日志，响应只给 `traceId`；开发环境才附 `exception` |
| 客户端密钥 | 经 OpenIddict 哈希后入库，不落明文；支持 `rotate-secret` 轮换 |
| CORS | 未配置来源时收紧到同源；显式禁止 `AllowAnyOrigin()` + `AllowCredentials()` 组合 |

## 相关的深入文档

| 主题 | 去哪看 |
|------|--------|
| 后台侧授权三层模型、防自锁护栏、权限声明时效性 | [admin-ui.md · 权限模型](admin-ui.md#权限模型) |
| 反向代理信任范围（`X-Forwarded-*` 会被静默丢弃） | [deployment.md · 反向代理信任范围](deployment.md#②-反向代理信任范围authhubsecuritytrustforwardedheaders--knownproxies--knownnetworks) |
| Data Protection 密钥环（会话 Cookie / 防伪令牌 / MFA 票据） | [deployment.md · 隐性依赖 ①](deployment.md#①-data-protection-密钥环authhubsecuritydataprotectionkeyspath) |
| 启动期环境护栏（开发配置被带进生产会在启动期被拦下） | [configuration.md · 启动期环境护栏](configuration.md#启动期环境护栏) |
| CSP 与零外部依赖的后台约定 | [admin-ui.md · 前端约定](admin-ui.md#前端约定改动前请先读) |
| 错误响应不泄露内部信息（只给 `traceId`） | [api-reference.md · 错误响应约定](api-reference.md#错误响应约定) |
| 签名密钥必须持久化（临时密钥重启即作废全部令牌） | [deployment.md · 配置（生产）](deployment.md#配置生产) |