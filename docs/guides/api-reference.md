# API 参考

> 从 [README](../../README.md) 拆出。端点清单与错误响应约定。
> 交互式文档见运行时 `/scalar/v1`（Development 默认开启），文档 JSON 在 `/openapi/v1.json`。

## 目录

- [OIDC / OAuth 2.0](#oidc--oauth-20)
- [API 文档](#api-文档)
- [会话与账号（浏览器，Cookie 认证）](#会话与账号浏览器cookie-认证)
- [账号 API（Cookie 认证，面向 SPA）](#账号-apicookie-认证面向-spa)
- [受保护资源与管理 API](#受保护资源与管理-api)
- [错误响应约定](#错误响应约定)
- [所有错误共用一个出口](#所有错误共用一个出口)

---

## OIDC / OAuth 2.0

| 方法 | 路径 | 说明 |
|------|------|------|
| GET/POST | `/connect/authorize` | 授权端点（`[Authorize]`，未登录跳登录页） |
| POST | `/connect/token` | 令牌端点（授权码 / 刷新令牌 / 客户端凭证 / 可选密码流程） |
| GET/POST | `/connect/userinfo` | 用户信息（要求令牌能代表一个用户） |
| GET/POST | `/connect/logout` | 全局登出 |
| POST | `/connect/introspect` | 令牌内省 |
| POST | `/connect/revoke` | 令牌撤销（RFC 7009） |
| GET | `/.well-known/openid-configuration` | 发现文档 |
| GET | `/.well-known/jwks` | 公钥集（下游离线校验令牌用） |
| GET | `/health` | 健康检查 |

## API 文档

| 路径 | 说明 |
|------|------|
| `GET /scalar/v1` | Scalar 交互式 API 文档 UI（受 `AuthHub:Features:EnableApiDocs` 控制，Development 默认开） |
| `GET /openapi/v1.json` | OpenAPI 文档 JSON（由框架内置生成器产出） |

文档只收录面向调用方的接口（`/api/*`、`/api/account/*`、`/connect/*`）；
产出浏览器页面的端点（`/account/*`、`GET /`）**有意排除**，理由与做法见
[openiddict-notes.md 第 10 条](../reference/openiddict-notes.md#文档只收录面向调用方的接口页面与站点跳转不在其中)。

## 会话与账号（浏览器，Cookie 认证）

| 路径 | 说明 |
|------|------|
| `GET/POST /account/login` | 登录页（HTML + 防伪令牌） |
| `GET/POST /account/2fa` | 两步验证页 |
| `GET /account/loggedout`、`GET /account/denied` | 登出 / 无权限提示页 |

## 账号 API（Cookie 认证，面向 SPA）

`POST register`、`POST login`、`POST 2fa/verify`、`POST logout`、`GET me`、`POST password`、
`GET 2fa`、`POST 2fa/setup`、`POST 2fa/enable`、`POST 2fa/disable`、`POST 2fa/send-code`、
`GET consents`、`DELETE consents/{authorizationId}`

**防伪令牌（Cookie 认证的变更端点）**：账号 API 与管理 API 里的 POST / PUT / DELETE / PATCH
（匿名端点除外）要求 `X-CSRF-TOKEN` 头 —— 先 `GET /api/antiforgery/token` 取一次性令牌，随变更请求
带上；响应同时下发配对的 `authhub.antiforgery` Cookie，两者必须共存于同一 Cookie jar。令牌与登录身份
绑定，登录态变更后重新获取。Bearer 认证的调用方不受影响。

## 受保护资源与管理 API

| 路径 | 必需权限 / Scope |
|------|------------------|
| `/api/profile`、`/api/profile/claims` | `api:read` 或 `api:write` |
| `POST /api/profile/echo` | `api:write` |
| `/api/users`（含 `{id}/roles`、`{id}/lockout`、`{id}/revoke-tokens`） | `users.manage` |
| `/api/clients`（含 `{clientId}/rotate-secret`） | `clients.manage` |
| `/api/scopes` | `scopes.manage` |
| `/api/roles`、`/api/roles/permissions` | `roles.manage` |
| `/api/audit-logs` | `audit.read` |
| `POST /api/tokens/revoke`、`/api/tokens/revoke/{referenceId}`、`/api/tokens/prune` | `tokens.revoke` |

## 错误响应约定

所有 API 错误统一为 **RFC 7807 ProblemDetails**（`Content-Type: application/problem+json`），
并额外带两个字段：

```jsonc
{
  "title": "未认证",
  "status": 401,
  "detail": "当前请求没有携带有效的登录会话或访问令牌。请先登录，或改用 Bearer 访问令牌调用。",
  "instance": "/api/users",
  "code": "Unauthorized",          // 稳定的机器可读错误码
  "traceId": "0HN8...",            // 与 Serilog 日志关联，线上凭它定位
  "errors": { "Password": ["..."] } // 仅校验失败时出现
}
```

状态码映射（`ApiControllerBase.Problem`）：

| `ErrorType` | HTTP | 典型场景 |
|-------------|------|----------|
| `Validation` | 400 | 参数校验失败（附 `errors`） |
| `Unauthorized` | 401 | 凭证无效 |
| `Forbidden` | 403 | 认证通过但权限/Scope 不足 |
| `NotFound` | 404 | 资源不存在 |
| `Conflict` | 409 | 唯一约束冲突（用户名、邮箱、client_id） |
| `LockedOut` | 423 | 账号被锁定 |
| `Failure` | 500 | 其他业务失败 |

浏览器页面（`/account/*`、`/connect/*`）仍走 302 跳转，只有 `/api/*` 返回 JSON ——
对 SPA、脚本、网关来说，"被 302 到 HTML 登录页"是最难排查的失败模式之一。

## 所有错误共用一个出口

上表之外，**400 只有一种形状**。这件事值得单独说，因为踩过：

| 出错路径 | 由谁产出 |
|----------|----------|
| 控制器里的业务失败（`Result`） | `ApiControllerBase.Problem` |
| 认证 / 授权失败（`/api/*`） | `AuthHubAuthorizationResultHandler` |
| **请求体绑定失败**（`[ApiController]` 自动 400） | `ApiBehaviorOptions.InvalidModelStateResponseFactory` |
| **FluentValidation 规则不通过** | `ValidationFilter`（把错误写进 `ModelState` 后复用同一个工厂） |
| 未处理异常（500） | `ExceptionHandlingMiddleware` |

它们全部经由 `ApiProblemWriter` 产出，因此字段集恒定是
`title / status / detail / instance / code / traceId`（校验失败再加 `errors`），
Content-Type 恒定是 `application/problem+json`。

不这么做会怎样（实测过的原始状态）：

```jsonc
// POST /api/account/register  body = "{}"  —— 绑定失败，走框架默认
{ "type": "...15.5.1", "title": "One or more validation errors occurred.", "status": 400,
  "errors": { "Email": ["The Email field is required."] },
  "traceId": "00-7ed0…-97f1…-00" }       // ← 没有 code，traceId 是 W3C traceparent
//                                 Content-Type: application/json   ← 不是 problem+json
```

同一时刻业务校验失败那条路却有 `code`、有中文标题 —— 两条 400 契约不同，
下游按 `code` 分支时会静默漏掉一半。统一之后两条路径的顶层字段**完全相同**，
`ApiErrorContractTests` 守着这一点（它同时盯 Content-Type 与字段集）。

边界：绑定失败时框架给的字段级文案是英文（`The X field is required.`）。
中文化需要成体系地配 DataAnnotations 本地化资源，不属于"统一出口"这件事，因此暂不处理。

⚠ 顺带一个坑：控制器上挂 `[Produces("application/json")]` 会覆盖 Content-Type，
把 `application/problem+json` 悄悄打回 `application/json` ——
完整经过见 [openiddict-notes.md 第 5 条](../reference/openiddict-notes.md#5-producesapplicationjson-会悄悄破坏-rfc-7807)。