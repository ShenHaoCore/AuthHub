# AuthHub

基于 **.NET 8 + OpenIddict 5.8** 的统一认证授权中心（Identity Provider）。

为下游多个应用提供一套账号体系与登录入口：一处登录，多处通行（SSO）；下游服务既能用
Bearer 令牌调用 API，也能凭 `jwks_uri` 在本地离线校验令牌，无需回源。

---

## 目录

- [功能范围](#功能范围)
- [技术栈](#技术栈)
- [架构](#架构)
- [快速开始](#快速开始)
- [种子数据](#种子数据)
- [端点清单](#端点清单)
- [测试](#测试)
- [错误响应约定](#错误响应约定)
- [安全设计要点](#安全设计要点)
- [与原始需求 / OpenIddict 实际 API 的差异](#与原始需求--openiddict-实际-api-的差异)
- [部署](#部署)
- [待办与扩展方向](#待办与扩展方向)

---

## 功能范围

| # | 能力 | 实现位置 |
|---|------|----------|
| 1 | 用户注册 / 登录（含密码策略、锁定、安全戳） | `AccountController`、`AccountApiController`、`AccountService` |
| 2 | 授权码流程（强制 PKCE）、同意页 | `AuthorizationController.Authorize` |
| 3 | 客户端凭证流程（M2M） | `AuthorizationController.Exchange` |
| 4 | 刷新令牌 + 旋转 + 重放防护（自动撤销整条授权） | `AuthorizationController.Exchange` |
| 5 | 单点登录（`prompt=none` 静默签发）与全局登出 | `AuthorizationController` |
| 6 | MFA：TOTP（验证器 App）、邮箱 / 短信验证码 | `AccountApiController`、`AccountService`、`IEmailSender` / `ISmsSender` |
| 7 | RBAC：角色 → 权限 → 令牌内的 `authhub:permission` 声明 | `RolePermissionMap`、`AuthorizationPolicyExtensions` |
| 8 | 客户端 CRUD（密钥哈希存储、支持轮换） | `ClientsController`、`OpenIddictClientAdminService` |
| 9 | Scope 管理 | `ScopesController`、`ScopeService` |
| 10 | 令牌撤销与黑名单（未过期 JWT 立即失效） | `TokensController`、`EnableTokenEntryValidation` |

附带：审计日志（`AuditLogs` 表 + `/api/audit-logs`）、限流、安全响应头、健康检查、Swagger。

---

## 技术栈

| 组件 | 版本 | 说明 |
|------|------|------|
| .NET / ASP.NET Core | 8.0 | `net8.0`，`LangVersion 12`，可空引用开启 |
| OpenIddict | **5.8.0** | Server + Validation + EF Core 集成 |
| Entity Framework Core | 8.0.31 | Identity / OpenIddict / 自定义表共用一个 `DbContext` |
| ASP.NET Core Identity | 8.0 | 用户、角色、密码哈希、锁定、MFA |
| 数据库 | SQL Server（生产）/ SQLite（开发与测试） | 二者切换只需改配置 |
| AutoMapper | 13.x | 实体 → DTO |
| FluentValidation | 11.x | 校验规则集中在 Application 层，经 `ValidationFilter` 自动执行 |
| Serilog | 8.x | Console + 按天滚动文件，可平滑接入 Seq / ELK |
| Swashbuckle | 6.x | Swagger UI + OAuth 交互式授权 |
| xUnit + Moq + FluentAssertions | — | 单元测试 66 项、集成测试 21 项 |

---

## 架构

```
AuthHub.slnx
├── src/
│   ├── AuthHub.Domain/           8 个文件   实体、常量、角色-权限映射（不依赖任何框架）
│   ├── AuthHub.Application/     31 个文件   用例编排、接口、DTO、校验器、Result 体系
│   ├── AuthHub.Infrastructure/  23 个文件   EF Core、迁移、OpenIddict 适配、各类实现
│   └── AuthHub.Api/             24 个文件   控制器、HTML 页面、中间件、DI 组装、Program.cs
├── tests/
│   ├── AuthHub.UnitTests/        4 个文件   领域规则、Result、校验器、AccountService（Moq）
│   └── AuthHub.IntegrationTests/ 6 个文件   WebApplicationFactory 起真实管道跑 OIDC / 鉴权 / 限流
├── docs/AuthHub.postman_collection.json    可直接导入的接口集合
├── .github/workflows/ci.yml                编译 + 单元/集成测试 + 依赖漏洞检查
└── Dockerfile                              多阶段构建，非 root 运行
```

依赖方向：

```
Api ──▶ Application ──▶ Domain
 │                        ▲
 └──▶ Infrastructure ─────┘   （Api → Infrastructure 仅用于 DI 注册，不直接使用其类型）
```

**几处有意的分层取舍**（不是疏漏，写下来避免后来人"顺手修正"）：

- `Domain` 引用了 `Microsoft.Extensions.Identity.Stores`：`ApplicationUser` / `ApplicationRole`
  直接继承 `IdentityUser<Guid>` / `IdentityRole<Guid>`。把 Identity 的泛型基类隔在领域模型之外
  收益很小（Identity 是事实上的框架底座），却要为每个字段再写一层映射。
- `Application` 引用了 `Microsoft.AspNetCore.App` 框架引用：服务里要用 `IHttpContextAccessor`、
  `AuthenticationProperties` 等类型。这个项目本身就是 Web 应用，不做跨宿主复用，"纯净度"换不来实际收益。
- 查询密集型服务（`OpenIddictClientAdminService`、`TokenAdminService` 等）落在 `Infrastructure`：
  它们直接操作 `IOpenIddict*Manager` 与 `DbContext`，放在 Application 只会制造无意义的接口隔层。

---

## 快速开始

### 前置条件

- **.NET SDK 9.0.200 及以上**。运行目标框架是 `net8.0`（见 `Directory.Build.props`），
  但解决方案文件用的是新的 `.slnx` 格式，需要 9.0.200+ 的 SDK / IDE 才认识。
  不想升级 SDK 的话，也可以按项目路径操作：`dotnet build src/AuthHub.Api/AuthHub.Api.csproj`
  （CI 与 Dockerfile 就是这么做的，因此它们对 SDK 版本不敏感）。
- 任选其一：SQL Server LocalDB（默认）/ SQL Server / SQLite
- 开发用 HTTPS 证书（`dotnet dev-certs https --trust`）

### 本机运行（SQL Server LocalDB）

```bash
git clone https://github.com/ShenHaoCore/AuthHub.git
cd AuthHub

# 首次：启用开发证书
dotnet dev-certs https --trust

# 建库（Development 下也会自动迁移，但显式跑一次便于确认）
dotnet ef database update -p src/AuthHub.Infrastructure -s src/AuthHub.Api

# 启动
dotnet run --project src/AuthHub.Api
```

启动后：

| 地址 | 说明 |
|------|------|
| <https://localhost:5001/swagger> | Swagger UI（Development 默认开启，可交互授权） |
| <https://localhost:5001/.well-known/openid-configuration> | OIDC 发现文档 |
| <https://localhost:5001/health> | 健康检查 |
| <http://localhost:5000> | 同端口组的 HTTP 端点 |

`appsettings.Development.json` 默认：`Seeding.Enabled=true`、`MigrateOnStartup=true`、
`RequireHttps=true`、`EnablePasswordFlow=true`。**生产环境这三项都不要直接沿用**，
见 [部署](#部署)。

### 切到 SQLite（零依赖跑起来）

```jsonc
{
  "Database": { "Provider": "Sqlite" },
  "ConnectionStrings": { "DefaultConnection": "Data Source=authhub.db" }
}
```

SQLite 路径下启动时走 `EnsureCreatedAsync()` 按模型建表（不套用 SQL Server 迁移），
因此不需要为 SQLite 另维护一套迁移文件。**SQLite 仅用于开发与集成测试，不要上生产。**

---

## 种子数据

仅在 `AuthHub:Seeding:Enabled=true` 时执行（幂等，先查后建）。所有口令/密钥都可被
配置项 `AuthHub:Seed:*` 覆盖，代码里的是**仅限开发的**默认值。

### 客户端

| client_id | 类型 | 密钥 | 允许的流程 | 用途 |
|-----------|------|------|-----------|------|
| `spa-client` | public | 无 | 授权码 + PKCE、刷新令牌 | 前端 SPA（回调 `https://localhost:3000/callback`） |
| `web-client` | confidential | `web-secret` | 授权码 + PKCE、刷新令牌 | 服务端 Web 应用（回调 `https://localhost:5002/signin-oidc`） |
| `m2m-service` | confidential | `m2m-secret` | 仅客户端凭证 | 后端服务间调用（`api:read` / `api:write`） |

> `spa-client` 设为 public 客户端（无密钥），安全边界完全由 PKCE 承担；
> 代码里 `ClientValidator` 会拒绝"public 客户端携带密钥"这种自相矛盾的配置。

### 账号与角色

| 用户名 | 密码 | 角色 |
|--------|------|------|
| `admin` | `Admin@12345` | `Administrator` |
| `alice` | `Alice@12345` | `User` |

| 角色 | 权限 |
|------|------|
| `Administrator` | `clients.manage`、`scopes.manage`、`users.manage`、`roles.manage`、`tokens.revoke`、`audit.read` |
| `UserManager` | `users.manage`、`tokens.revoke`、`audit.read` |
| `Auditor` | `audit.read` |
| `User` | 无管理权限 |

单元测试里有一条断言专门守着"每个已声明的权限至少被一个内置角色覆盖"，
防止新增权限后忘记挂到角色上（那会变成谁也拿不到的幽灵权限）。

---

## 端点清单

### OIDC / OAuth 2.0

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

### 会话与账号（浏览器，Cookie 认证）

| 路径 | 说明 |
|------|------|
| `GET/POST /account/login` | 登录页（HTML + 防伪令牌） |
| `GET/POST /account/2fa` | 两步验证页 |
| `GET /account/loggedout`、`GET /account/denied` | 登出 / 无权限提示页 |

### 账号 API（`/api/account`，Cookie 认证，面向 SPA）

`POST register`、`POST login`、`POST 2fa/verify`、`POST logout`、`GET me`、`POST password`、
`GET 2fa`、`POST 2fa/setup`、`POST 2fa/enable`、`POST 2fa/disable`、`POST 2fa/send-code`、
`GET consents`、`DELETE consents/{authorizationId}`

### 受保护资源与管理 API（Bearer 或 Cookie）

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

---

## 测试

```bash
# 全解决方案（编译须 0 警告 0 错误）
dotnet build AuthHub.slnx

# 单元测试：66 项
dotnet test tests/AuthHub.UnitTests

# 集成测试：23 项
dotnet test tests/AuthHub.IntegrationTests
```

| 项目 | 用例数 | 覆盖内容 |
|------|--------|----------|
| `AuthHub.UnitTests` | 66 | 角色-权限映射、`Result` / `PagedResult` 语义、FluentValidation 规则（含重定向 URI 白名单）、`AccountService` 的登录/注册分支（Moq 构造 `UserManager`/`SignInManager`，含"未知用户不得泄露账号存在性"） |
| `AuthHub.IntegrationTests` | 23 | 发现文档、**用公钥集对令牌做真实 RSA 离线验签**、JWS 令牌内容（sub/scope/role/iss/aud）、错误密钥与未知客户端、登录页与防伪令牌、安全响应头、未见令牌 401、会话 Cookie 不能认证 Bearer API、scope 门禁 403、M2M 令牌被 userinfo 拒绝、令牌端点与 HTML 登录表单的限流 429 |

集成测试用 `WebApplicationFactory<Program>` 起真实管道（认证中间件顺序、限流、CORS、
安全头都参与），数据库用独立的临时 SQLite 文件，跑完即删。

> **注意**：集成测试会占用 `src/AuthHub.Api/bin` 下的 DLL。如果本机正跑着
> `dotnet run`，先停掉它，否则会拿到 `MSB3021 / MSB3027: 文件被 AuthHub.Api (PID) 锁定`。

### 端到端脚本

`.tmp/flow_test.py`（纯标准库，无第三方依赖）对着已启动的服务跑一遍完整链路，
共 **33 项断言**，覆盖上面所有功能：

```bash
dotnet run --project src/AuthHub.Api          # 另开一个终端
python .tmp/flow_test.py
```

内容包括：错误密码被拒 → 正确密码登录 → 同意页 → 授权码 + PKCE 换令牌 → 令牌内容校验 →
userinfo → 受保护 API → scope 门禁 → 管理员权限声明 → 撤销后过期前 JWT 立即失效 →
刷新令牌旋转 → 旧 refresh_token 复用被拒且整条授权被撤销 → `prompt=none` 静默签发 →
登出后 `login_required` → 客户端凭证。脚本已经加了 `.gitignore`，它是开发期工具，不随仓库发布。

### Postman 集合

`docs/AuthHub.postman_collection.json` 是可直接导入的集合，按五个文件夹组织，
互相之间有令牌传递（`accessToken` / `refreshToken` / `adminToken` 都是集合变量）。

| 文件夹 | 内容 |
|--------|------|
| 0. 环境与发现 | 健康检查、发现文档、JWKS 公钥集 |
| 1. 机器对机器 | 客户端凭证换令牌、访问受保护资源、scope 门禁、**无令牌 401 的 RFC 7807 形状** |
| 2. 用户会话 | JSON 登录（Cookie）、查询当前用户、登出 |
| 3. 授权码 + PKCE | 放行 Postman 回调地址、OAuth2 助手拿管理员令牌、管理员接口 |
| 4. 刷新令牌与撤销 | 令牌旋转、按 subject 批量撤销、撤销后立即 401 |

导入后需要先做两步：在 Postman 设置里**关闭 SSL 证书校验**（本机是自签名开发证书），
以及在本机以 Development 环境启动服务（否则没有种子客户端和账号）。

---

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

---

## 安全设计要点

| 项 | 做法 |
|----|------|
| 授权码拦截 | 强制 PKCE（`RequireProofKeyForCodeExchange`），public 客户端无密钥也安全。**注意**：OpenIddict 5.x 无法限制 `code_challenge_method`，发现文档会同时声明 `plain` 与 `S256`；客户端必须用 **S256**（见[待办](#待办与扩展方向)） |
| 刷新令牌 | 每次刷新旋转，宽限期 0；**复用已旋转的旧令牌会撤销该授权下全部令牌**（重放防护） |
| 令牌撤销 | `EnableTokenEntryValidation()` 让每次校验都查令牌状态，未过期的 JWT 也能立即失效 |
| 令牌形态 | `DisableAccessTokenEncryption()` 产出 JWS 而非 JWE，下游可凭 `/.well-known/jwks` 离线校验 |
| 令牌有效期 | Access 1h、ID 30min、授权码 5min、刷新 30d（可按需在 `Program.cs` 调整） |
| 登录防爆破 | 限流 `LoginRequestsPerMinute`（默认 10 次 / 5 分钟），同时作用于 HTML 登录表单与 `/api/account/*`；再叠加 Identity 锁定（5 次失败锁 15 分钟） |
| 令牌端点限流 | `/connect/*` 前缀单独限流，**默认额度更严**（60/min）；其余接口放宽 10 倍 |
| 会话 Cookie | `HttpOnly` + `SameSite=Lax` + 按需 `Secure`；密码/角色变更经安全戳 5 分钟内失效 |
| CSRF | 登录、同意等表单带防伪令牌（`[ValidateAntiForgeryToken]`）；API 写操作不靠表单 |
| 响应头 | `X-Content-Type-Options`、`X-Frame-Options: DENY`、CSP（`frame-ancestors 'none'`） |
| 异常信息 | 生产环境异常详情只进日志，响应只给 `traceId`；开发环境才附 `exception` |
| 客户端密钥 | 经 OpenIddict 哈希后入库，不落明文；支持 `rotate-secret` 轮换 |
| CORS | 未配置来源时收紧到同源；显式禁止 `AllowAnyOrigin()` + `AllowCredentials()` 组合 |

---

## 与原始需求 / OpenIddict 实际 API 的差异

写这节是因为**照着文档写会编译不过或运行期才炸**。以下每一条都是实际踩过的。

### 1. OpenIddict 5.8 的 API 与常见示例不一致

| 常见写法 | 5.8 实际 | 说明 |
|----------|----------|------|
| `SetRefreshTokenReuseInterval(...)` | `SetRefreshTokenReuseLeeway(...)` | 只是改名，语义相同 |
| `SetRefreshTokenExpiration(...)`、`TokenExpiration` | 不存在 | 改用 `SetRefreshTokenLifetime(...)` |
| `OpenIddictScopeDescriptor.Claims` | 不存在 | Scope 只管 `Resources`；声明归属由 `RegisterClaims` 决定 |
| `OpenIddictValidationBuilder.UseLocalServer()` | 需要额外的包 | 属于 `OpenIddict.Validation.ServerIntegration`，**不在主包里**，不加 `PackageReference` 就报 `CS1061` |
| `app.UseOpenIddict()` | 不需要 | 5.x 由认证中间件承载，端点处理挂在 request-handler 环节 |

### 2. passthrough 的真实语义：每个 grant type 都要应用自己收尾

官方文档原话是"校验通过后，请求继续走后续管道"。**容易误读成"OpenIddict 会顺手处理掉"**。
实际是：只要开了 `EnableTokenEndpointPassthrough()`，`authorization_code`、`refresh_token`、
`client_credentials` **全部**都要在你的控制器里显式
`SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)` 才算完。
少写任何一个，那个流程就会返回 `400 unsupported_grant_type`，而且日志里显示的是
"token request successfully validated" —— 一开始极具误导性。

`AuthorizationController.Exchange()` 因此按 grant type 拆成了三个分支。

### 3. OpenIddict 的 Server 方案不能作为默认方案

把 `DefaultChallengeScheme` 设成 `OpenIddictServerAspNetCoreDefaults.AuthenticationScheme`，
启动时直接抛：

```
The OpenIddict ASP.NET Core server handler cannot be used as the default scheme handler.
```

Server handler 实现了 `IAuthenticationRequestHandler`，只负责 `/connect/*`，与"默认方案"无关。
默认方必须指向 **Validation** 方案。另外 `AddIdentity()` 会把
`DefaultAuthenticateScheme` / `DefaultChallengeScheme` 都指向会话 Cookie，
**只改 `DefaultScheme` 是不够的**（`DefaultAuthenticateScheme` 优先级更高），三项都要显式覆盖。

### 4. 多认证方案的 Challenge/Forbid 会互相踩（本项目真实修复的坑）

管理类策略同时挂了会话 Cookie 与 Bearer 两个方案：

```csharp
.AddAuthenticationSchemes(AuthHubSchemes.Cookie, AuthHubSchemes.Bearer)
```

ASP.NET Core 会**依次**对每个方案执行 Challenge/Forbid。而 Cookie 方案的事件
（`OnRedirectToLogin` / `OnRedirectToAccessDenied`）会写出响应体 —— 响应一旦开始，
排在后面的 OpenIddict Validation 处理器再设状态码就会抛：

```
InvalidOperationException: The status code cannot be set, the response has already started.
```

症状是**同一类失败时而正常、时而 500，取决于策略里方案的先后顺序**：
`(Bearer, Cookie)` 恰好是"不写响应体的排前面、写响应体的排最后"，看起来一切正常；
换成 `(Cookie, Bearer)` 就炸。

修法（见 `AuthHubAuthorizationResultHandler`）：让各方案只负责补自己的响应头
（如 OpenIddict 的 `WWW-Authenticate`），**谁都不写响应体**，等方案循环结束后
由统一出口一次性写出 ProblemDetails；非 `/api` 路径仍交给默认处理器，保住 302 跳登录页。

### 5. `[Produces("application/json")]` 会悄悄破坏 RFC 7807

`ProducesAttribute` 实现 `IResultFilter`，在结果过滤器阶段会 `Clear()` 并覆盖
`ObjectResult.ContentTypes`。于是：

```csharp
return new ObjectResult(problem) { ContentTypes = { "application/problem+json" } };
// 控制器上有 [Produces("application/json")] → 实际发出去的是 application/json
```

只在带 `[Produces]` 的控制器上出现，且"看起来也能用"，排查成本很高。
修法：错误响应改用自定义 `IActionResult`（`ProblemDetailsResult`）直接写响应流，
绕开内容协商 —— Content-Type 由我们说了算。

### 6. `[ValidateAntiForgeryToken]` 需要 `AddControllersWithViews()`

用 `AddControllers()` 时，`ValidateAntiforgeryTokenAuthorizationFilter` 未注册，请求直接 500：

```
No service for type 'Microsoft.AspNetCore.Mvc.ViewFeatures.Filters.ValidateAntiforgeryTokenAuthorizationFilter'
```

本项目没有任何 `.cshtml`（页面由 `Pages/HtmlPages.cs` 拼 HTML），但为了保留这个声明式、
不易遗漏的安全特性，仍走带视图的 MVC 注册。

### 7. `openid` / `offline_access` 不落 Scope 表

这两个由 OpenIddict 内部处理，写进 `OpenIddictScopeDescriptor` 是多余的。
本项目落表的只有：`profile`、`email`、`roles`、`api:read`、`api:write`、`authhub:admin`。

### 8. userinfo 对 M2M 令牌返回 403 `insufficient_access`

用客户端凭证令牌调 `/connect/userinfo`，OpenIddict 在校验阶段就拒绝
（M2M 令牌的 `sub` 是 `client_id`，无法代表一个用户），请求**根本不会 passthrough 到控制器**。
响应是 **403**、响应体为空，错误细节在 `WWW-Authenticate` 头里：

```
Bearer error="insufficient_access", error_description="The user represented by the token is not allowed to perform the requested action.", error_uri="https://documentation.openiddict.com/errors/ID2095"
```

语义与 RFC 6750 一致（令牌有效、身份/范围不足 → 403）。但错误码是 OpenIddict 自己的
`insufficient_access`，**不是** RFC 6750 里的 `insufficient_scope`。这是库的既定契约，
本项目选择照实适配而非覆写。

### 9. 密码流程默认关闭

`AllowPasswordFlow()` 只在 `AuthHub:Features:EnablePasswordFlow=true` 时启用（仅 Development 默认开）。
资源所有者密码流程在 OAuth 2.1 里已被移除，生产不应启用。

---

## 部署

### 配置（生产）

生产用**环境变量**覆盖，不要把口令写进 `appsettings.json`：

```bash
ASPNETCORE_ENVIRONMENT=Production
ConnectionStrings__DefaultConnection="Server=...;Database=AuthHub;User Id=...;Password=...;Encrypt=True;"
AuthHub__Issuer="https://authhub.example.com/"
AuthHub__Security__RequireHttps=true
AuthHub__Features__EnablePasswordFlow=false
AuthHub__Features__EnableSwagger=false
AuthHub__Seeding__Enabled=false        # 生产不要跑种子数据
AuthHub__Seeding__MigrateOnStartup=false # 迁移建议在发布流程里显式执行
AuthHub__Keys__SigningCertificatePath="/run/secrets/authhub-signing.pfx"
AuthHub__Keys__SigningCertificatePassword="..."
AuthHub__Cors__AllowedOrigins__0="https://app.example.com"
```

**签名密钥必须持久化**：`Program.cs` 会按 配置证书 → 开发证书 → 临时密钥 依次降级。
临时密钥每次重启都变，会让所有已签发令牌失效、下游缓存全废 —— 生产必须走持久化证书
（或挂载 Data Protection 密钥环）。

### 迁移

```bash
dotnet ef migrations add <Name> -p src/AuthHub.Infrastructure -s src/AuthHub.Api -o Data/Migrations --context AuthHubDbContext
dotnet ef database update -p src/AuthHub.Infrastructure -s src/AuthHub.Api
```

### 反向代理

部署在 Nginx / 网关之后时设 `AuthHub__Security__TrustForwardedHeaders=true`，
否则 `RemoteIpAddress` 与 Scheme 会错，导致审计日志里记的是代理 IP、以及 HTTPS 判断异常。

### Docker

见 [`Dockerfile`](Dockerfile)：

```bash
docker build -t authhub:local .
docker run --rm -p 8080:8080 \
  -e ConnectionStrings__DefaultConnection="..." \
  -e AuthHub__Issuer="http://localhost:8080/" \
  -e AuthHub__Security__RequireHttps=false \
  authhub:local
```

---

## 待办与扩展方向

- [ ] **禁用 PKCE 的 `plain` 模式**：OAuth 2.1 已弃用 `plain`（`code_challenge == code_verifier`，
      等于把校验值明文放在前端渠道）。本项目强制了 PKCE，但 OpenIddict 5.8 没有 builder API
      能限制 `code_challenge_method`，发现文档仍会声明 `plain`。彻底禁掉需要两步：
      ① 加一个 `ValidateAuthorizationRequestContext` 内联事件处理器，拒绝非 `S256` 的请求；
      ② 替换 `AttachCodeChallengeMethods` 发现文档处理器，只声明 `S256`。
      升级到 OpenIddict 6.x 后可直接配置，届时优先走升级路线。
- [ ] **Redis 令牌缓存**：`EnableTokenEntryValidation()` 目前每次校验都查库，
      高并发下会成为瓶颈；可把令牌状态缓存到 Redis 并订阅撤销事件。
- [ ] **分布式 JWKS 校验**：下游目前可离线验签；进一步可加公钥轮换与缓存控制头。
- [ ] **OpenTelemetry**：分布式追踪（导出到 OTLP）。
- [ ] **CI 安全扫描**：`dotnet list package --vulnerable` 与 OWASP Dependency Check
      （见 `.github/workflows/ci.yml`）。
- [ ] **管理后台 UI**：目前只有 API 与 Swagger，没有管理界面。
- [ ] **MFA 渠道补齐**：邮件与短信当前是"写日志"实现（`LoggingEmailSender` / `LoggingSmsSender`），
      接真实 SMTP / 短信服务商时替换 `IEmailSender` / `ISmsSender` 的注册即可，业务代码无需改动。
- [ ] **密钥轮换流程**：证书轮换需要在 JWKS 中短暂并存新旧公钥，目前未实现。
