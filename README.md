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
- [管理后台（UI）](#管理后台ui)
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
| 11 | **管理后台 UI**（Razor Pages，ABP 版式）：仪表盘、用户、角色与权限、客户端、Scope、审计日志、我的账户 | `src/AuthHub.Api/Pages/`、`wwwroot/` |

附带：审计日志（`AuditLogs` 表 + `/api/audit-logs`）、限流、安全响应头、健康检查、OpenAPI 文档（Scalar UI）。

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
| Swashbuckle | 6.9.0 | **只用于生成 OpenAPI 文档 JSON**（引的是 `Swashbuckle.AspNetCore.SwaggerGen`，未使用其 Swagger UI） |
| Scalar.AspNetCore | 2.17 | 交互式 API 文档 UI，替代 Swagger UI；前端资源内嵌在程序集里、不依赖 CDN |
| Razor Pages | 8.0（ASP.NET Core 内置） | 管理后台的视图层：`_AdminLayout` 共享布局 + 服务端渲染的列表与表单 |
| 前端 | 原生 CSS / JS，无构建步骤 | `wwwroot/css`、`wwwroot/js`，零 npm、零打包器、零 CDN |
| xUnit + Moq + FluentAssertions | — | 单元测试 66 项、集成测试 50 项 |

---

## 架构

```
AuthHub.slnx
├── src/
│   ├── AuthHub.Domain/           8 个文件   实体、常量、角色-权限映射（不依赖任何框架）
│   ├── AuthHub.Application/     31 个文件   用例编排、接口、DTO、校验器、Result 体系
│   ├── AuthHub.Infrastructure/  23 个文件   EF Core、迁移、OpenIddict 适配、各类实现
│   └── AuthHub.Api/             34 个 .cs + 11 个 .cshtml + 5 个静态资源
│                                            控制器、协议页、管理后台（Razor Pages）、
│                                            中间件、DI 组装、Program.cs、wwwroot
├── tests/
│   ├── AuthHub.UnitTests/        4 个文件   领域规则、Result、校验器、AccountService（Moq）
│   └── AuthHub.IntegrationTests/ 9 个文件   WebApplicationFactory 起真实管道跑 OIDC / 鉴权 / 限流 / 文档端点 / 管理后台
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

**两类浏览器页面走两条不同的路**（这是刻意的，不是历史遗留）：

| | 协议页：登录 / 2FA / 同意授权 / 提示 | 管理后台：`/admin/*` |
|---|---|---|
| 产出方式 | `Pages/HtmlPages.cs` 用字符串拼装 HTML | Razor Pages（`Pages/Admin/*.cshtml` + `_AdminLayout.cshtml`） |
| 为什么 | 它们是 OIDC 流程的一环，页面内容全部来自当前授权请求。字符串拼装让"表单字段 ↔ 协议参数"的对应关系一眼可见，集成测试也能直接断言页面上出现了哪些参数与 scope | 7 个页面上百个字段，结构高度相似（工具栏 + 表格 + 模态表单）。手写字符串会变成数千行拼接；视图引擎在这里省下的成本远超它的间接层 |
| 样式 | `wwwroot/css/authhub-auth.css` | `wwwroot/css/authhub.css` |

两者共用同一份设计令牌 `wwwroot/css/authhub-tokens.css`，因此登录页与后台是同一套视觉语言
（深色侧边栏 + 浅色卡片 + 蓝色主色，ABP LeptonX 的版式）。

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
| <https://localhost:5001/admin> | **管理后台**（Razor Pages，ABP 版式；开发环境用 `admin` / `Admin@12345` 登录） |
| <https://localhost:5001/scalar/v1> | Scalar 交互式 API 文档（Development 默认开启，可交互授权）；文档 JSON 在 `/openapi/v1.json` |
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

### API 文档

| 路径 | 说明 |
|------|------|
| `GET /scalar/v1` | Scalar 交互式 API 文档 UI（受 `AuthHub:Features:EnableApiDocs` 控制，Development 默认开） |
| `GET /openapi/v1.json` | OpenAPI 文档 JSON（由 Swashbuckle 生成） |

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

## 管理后台（UI）

登录后访问 **`/admin`**（开发环境内置账号 `admin` / `Admin@12345`）。
版式参考 **ABP Framework 的 LeptonX 主题**：深色固定侧边栏 + 浅色内容区，
页面是"卡片头放工具栏、卡片体放表格、卡片尾放分页"的形态，
危险操作走模态二次确认，成功/失败用右上角轻提示。

| 页面 | 路径 | 能做什么 |
|------|------|----------|
| 仪表盘 | `/admin` | 用户 / 客户端 / Scope / 角色计数，近 7 天审计事件柱状图，协议端点速查，最近事件流 |
| 用户 | `/admin/users` | 搜索与筛选、新建、编辑资料、分配角色、锁定 / 解锁、强制下线（撤销全部令牌）、删除 |
| 角色与权限 | `/admin/roles` | 角色列表 + 权限矩阵（权限 × 内置角色）、新建角色、改说明、删除；权限树只读 |
| 客户端 | `/admin/clients` | 注册 / 编辑 OAuth 客户端、勾选授权类型与 Scope、轮换密钥（明文只显示一次）、删除 |
| Scope | `/admin/scopes` | 新建 / 编辑 / 删除 Scope、维护关联资源（进入令牌 `aud`）、查看被多少客户端引用 |
| 审计日志 | `/admin/audit-logs` | 按动作 / 用户 / 客户端 / 时间区间查询，快捷范围（今天 / 7 天 / 30 天），折叠查看 User-Agent |
| 我的账户 | `/admin/profile` | 档案与权限总览、修改密码、启用 / 关闭 TOTP 两步验证（含恢复码）、测试邮件 / 短信通道 |

### 权限模型

侧边栏按权限收敛，但**真正的访问控制在服务端策略上**，不依赖前端是否隐藏了入口
（`AuthHubConstants.Policies.Ui.*`，见 `AuthorizationPolicyExtensions.AddUiPermissionPolicy`）：

| 策略 | 要求 |
|------|------|
| `Ui.Authenticated` | 只要登录（`/admin/profile`，普通用户也要能改自己的密码） |
| `Ui.Admin` | 仪表盘：持有 6 个管理权限中的任意一个 |
| `Ui.UsersManage` / `RolesManage` / `ClientsManage` / `ScopesManage` / `TokensRevoke` / `AuditRead` | 对应的 `authhub:permission` 声明（由 `RolePermissionMap` 从角色推导） |

> **一个容易踩的坑**：`Program.cs` 把 `DefaultChallengeScheme` 指向了 OpenIddict Validation
> （为的是 `/api/*` 未带令牌时返回 401 而不是 302）。所以后台的 UI 策略**必须显式只挂会话 Cookie 方案**，
> 否则浏览器访问 `/admin` 会拿到 `401 + WWW-Authenticate`，用户看到一片空白而不是登录页。
> 集成测试里有一条用例专门钉住这个行为。

### 前端约定（改动前请先读）

- **CSP 是 `script-src 'self'`**，行内 `<script>` 与 `on*` 事件属性会被浏览器静默拦截。
  因此所有交互都写在 `wwwroot/js/authhub.js` 里，用 `data-*` 属性声明行为：
  `data-dialog-open` / `data-dialog-close`、`data-confirm`、`data-copy`、`data-tab`、`data-menu-toggle`。
  `style-src` 允许 `'unsafe-inline'`，所以行内 `style` 属性与 `<noscript><style>` 可以用。
  **唯一的例外**：`/scalar` 前缀（API 文档 UI）的 `script-src` 额外放行 `'unsafe-inline'` ——
  Scalar 的页面模板自带一段内联初始化脚本，拦掉它页面就是一片空白（返回 200、资源全通，
  纯接口断言发现不了）；.NET 集成拿不到 nonce，放宽范围被中间件限制在这一条路径上，
  且有两条护栏测试钉住"基准策略不得扩散 unsafe-inline"。
- **零外部依赖**：不引用任何 CDN（字体、图标、QR 库都没有）。图标是 `AdminNav.Icons` 里的
  24×24 SVG path 数据，直接内联进标记。集成测试会断言后台页面里不存在站外 `src` / `href`。
- **渐进增强**：脚本被禁用（或被 CSP 拦下）时，`_AdminLayout.cshtml` 里的 `<noscript><style>`
  会隐藏标签条并强制展开全部面板，内容依然可读、表单依然可提交。
- **表单走 PRG**（Post-Redirect-Get）：成功 302 回列表 + `TempData["Success"]` 轻提示；
  失败原地重渲染并用 `data-dialog-autoopen` 重新打开弹窗，保留用户已填的值。
- **一次性机密只出现一次**：客户端明文密钥、MFA 恢复码都用 `TempData`
  （Data Protection 加密、HttpOnly、读取即删）跨重定向展示一次。

### 几个在后台里被显式处理掉的陷阱

| 陷阱 | 处理方式 |
|------|----------|
| 编辑客户端时若只提交数据库里的 scope，会把 `openid` / `offline_access` 静默删掉（它们不落 Scope 表） | 把它们作为"协议 Scope"一并列出参与往返；客户端独有的 scope 也单独成组，保证不会被无声丢掉 |
| 每次保存用户资料都刷新安全戳 → 把用户踢下线 | 只在角色集合**确实变化**时才调 `AssignRolesAsync` |
| 未改动的字段也会被写进审计 → 假审计记录 | `UnchangedAsNull`：与当前值相同的字段传 `null`（服务端的"不修改"语义） |
| 权限是代码内固定映射（`RolePermissionMap`），不是数据库配置 | 权限树/矩阵只读（`disabled`），并用提示说明"为什么这里不能勾选" |
| 按天分桶的审计统计在闭区间下会漏算/重算边界毫秒 | 一天的结束取"次日零点减 1 tick"（仓储用的是 `>=` 与 `<=`） |
| 弹窗 id 里直接嵌了 Scope 名（可能含 `:`，如 `api:read`），`querySelector('#x:y')` 会抛 `SyntaxError` | `authhub.js` 的 `byIdOrSelector` 回退到 `getElementById`（它接收任意 id 字面量、不做解析） |

---

## 测试

```bash
# 全解决方案（编译须 0 警告 0 错误）
dotnet build AuthHub.slnx

# 单元测试：66 项
dotnet test tests/AuthHub.UnitTests

# 集成测试：50 项
dotnet test tests/AuthHub.IntegrationTests
```

| 项目 | 用例数 | 覆盖内容 |
|------|--------|----------|
| `AuthHub.UnitTests` | 66 | 角色-权限映射、`Result` / `PagedResult` 语义、FluentValidation 规则（含重定向 URI 白名单）、`AccountService` 的登录/注册分支（Moq 构造 `UserManager`/`SignInManager`，含"未知用户不得泄露账号存在性"） |
| `AuthHub.IntegrationTests` | 50 | 发现文档、**用公钥集对令牌做真实 RSA 离线验签**、JWS 令牌内容（sub/scope/role/iss/aud）、错误密钥与未知客户端、登录页与防伪令牌、安全响应头、未见令牌 401、会话 Cookie 不能认证 Bearer API、scope 门禁 403、M2M 令牌被 userinfo 拒绝、令牌端点与 HTML 登录表单的限流 429、**API 文档端点**（Scalar 页面及其内嵌脚本资源、OpenAPI JSON 里的 OAuth2 方案、旧 Swagger 路径已下线、未开启时不暴露）、**管理后台**（7 个页面未登录一律 302 到登录页且不发 Bearer 挑战、无权限用户 302 到 `/account/denied`、管理员逐页可访问且渲染出关键内容、页面零站外引用、5 个静态资源可取且 Content-Type 正确、协议页与后台共用同一份样式令牌、Scope 页表单完整往返一次创建与删除、审计页时间区间筛选在两种提供程序下都可用） |

集成测试用 `WebApplicationFactory<Program>` 起真实管道（认证中间件顺序、限流、CORS、
安全头都参与），数据库用独立的临时 SQLite 文件，跑完即删。

> 后台的会话用例走的是 `TestServer.CreateHandler()` + 自建 Cookie 容器
> （`Infrastructure/CookieSession.cs`），**刻意不复用 fixture 上那个共享 `Client`** ——
> 否则登录后残留的会话 Cookie 会让"未登录应返回 401"那几条断言随机失败。

> **注意**：集成测试会占用 `src/AuthHub.Api/bin` 下的 DLL。如果本机正跑着
> `dotnet run`（或在 Visual Studio 里调试），先停掉它，否则会拿到
> `MSB3021 / MSB3027: 文件被 AuthHub.Api (PID) 锁定`。
> 不想停调试时，也可以换个配置跑：`dotnet test -c Release`
> （IDE 的调试通常占用 Debug 目录，两边互不干扰）。

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
| 响应头 | `X-Content-Type-Options`、`X-Frame-Options: DENY`、CSP（`frame-ancestors 'none'`；`script-src 'self'`，仅 `/scalar` 文档 UI 因 Scalar 的内联初始化脚本额外放行 `'unsafe-inline'`） |
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

### 10. API 文档 UI 用 Scalar 而非 Swagger UI

需求里写的是 Swashbuckle 的 Swagger UI。这里**保留 Swashbuckle 生成 OpenAPI 文档**，
只把展示层换成 Scalar：

| 变化 | 说明 |
|------|------|
| 包引用 | 只引 `Swashbuckle.AspNetCore.SwaggerGen`，不再引完整的 `Swashbuckle.AspNetCore`（后者会捎带 SwaggerUI 的静态资源，已用不上） |
| 文档路径 | 从 `swagger/v1/swagger.json` 改为 `openapi/{documentName}.json` —— 这是 Scalar 的默认约定，对齐后少一处需要两边手工同步的配置 |
| UI 地址 | `/scalar/v1`（受 `AuthHub:Features:EnableApiDocs` 控制，Development 默认开） |
| 配置项改名 | `AuthHub:Features:EnableSwagger` → `AuthHub:Features:EnableApiDocs`，让名字与实现一致 |
| 旧路径 | `/swagger/index.html`、`/swagger/v1/swagger.json` 现在都返回 404 |

换 UI 的实际理由：Scalar 对 OAuth2 授权码流程的调试更顺手，且它的前端资源
**内嵌在程序集里、由本地路由提供**（`/scalar/scalar.js`），**不从 CDN 加载** ——
这对内网/离线部署是硬需求，也才不会撞上本项目自己的 CSP（`script-src 'self'`）。
集成测试里有断言守着这一点（页面不得出现 `cdn.jsdelivr.net` / `unpkg.com`）。

Scalar 在 OAuth2 上的预设：授权码流程预填 `spa-client` 并强制 PKCE(S256)，
客户端凭证流程预填 `m2m-service`。**机密客户端的密钥不写进代码**，
需要在 UI 的认证面板里手工填写。

侧边栏的分组与每个端点的摘要 / 描述都是中文标注，来自控制器上的
`[Tags]` / `[EndpointSummary]` / `[EndpointDescription]` 特性（.NET 8 官方元数据）。
两个约定：

- **新增端点记得同样标注**，否则会掉进以程序集名命名的默认分组（"AuthHub.Api"），
  与中文分组混在一起 —— `Every_operation_should_declare_group_summary_and_description`
  这条契约测试会拦住它。
- 代码里的 XML `<summary>` 面向维护者，可以写长；特性标注面向调用者，保持一行短句。
  两者同时存在时**特性优先**（`OpenApiExtensions.EndpointMetadataOperationFilter`
  负责这件事——Swashbuckle 原生会让 XML 注释覆盖特性，见该过滤器注释）。

### 11. 为管理后台引入 Razor Pages（对"零视图依赖"取舍的修订）

早期版本的注释里写着"为什么不用 Razor：本项目的定位是 OIDC 服务端 + 管理 API"。
那是只有 4 个协议页时的判断。加上 7 个管理页面之后，这个结论不再成立：

| | 协议页（4 个） | 管理后台（7 个页面、上百个字段） |
|---|---|---|
| 手写字符串 | 每个页面一个 `StringBuilder` 方法，可读性尚可 | 会变成数千行拼接，改一个字段要动好几处 |
| 集成测试断言 | 直接对 HTML 字符串断言参数与 scope，很直接 | 断言关键内容即可，不需要逐字段验证视觉 |
| 防伪令牌 | 手工 `IssueAntiforgeryToken()` | Razor Pages 的 POST 处理器**默认自动校验**，不需要逐个挂 `[ValidateAntiForgeryToken]` |

因此：**后台改用 Razor Pages，协议页保持纯字符串**。这也是 `Program.cs` 里
`AddRazorPages()` / `MapRazorPages()` 的来由。顺带一提，当初选 `AddControllersWithViews()`
而非 `AddControllers()` 就是为了让 `[ValidateAntiForgeryToken]` 能正常工作（见第 6 条），
现在两条路都受益。

### 12. `.NET 8` 的 `WebApplication` 不会自动挂 `UseStaticFiles()`

这个坑很隐蔽，值得单独记一笔：**`/admin` 会正常返回 200，但 `wwwroot` 下的
CSS / JS / favicon 全是 404**。页面结构完整、文字齐全，只是没有样式、按钮点了没反应 ——
只看状态码完全发现不了。

修复就是在管道里显式加一行 `app.UseStaticFiles()`（位置刻意放在限流与请求日志之前：
静态资源是终端处理、不经过认证，先命中就不会在每次页面加载时消耗登录接口的限流额度，
也不会把请求日志刷满 `.css` / `.js`）。集成测试里有一组用例逐一断言这 5 个资源可取。

### 13. SQLite 上 `DateTimeOffset` 的比较无法翻译

EF Core 的 SQLite 提供程序能翻译 `DateTimeOffset` 的**排序**，却翻译不了**比较**：

```
System.InvalidOperationException: The LINQ expression 'DbSet<AuditLog>()
    .Where(a => (DateTimeOffset?)a.CreatedAt >= __from_0)' could not be translated.
```

于是 `AuditLogRepository` 里"按时间区间筛选"这段代码在 SQL Server 上一切正常，
换到 SQLite 就 500（表现是"仪表盘的近 7 天统计炸了"）。集成测试跑在 SQLite 上，
所以这个缺陷在第一次跑测试时就被抓出来了。

修复：在 `AuthHubDbContext` 里对 **SQLite 专用**地把自定义实体的 `DateTimeOffset`
属性改为按 `UtcTicks`（`long`）存储 —— 语义不变（库里本来就是 UTC 时刻），
比较与排序都能被原生翻译，而 SQL Server 仍使用原生 `datetimeoffset` 列类型，
生产库结构与既有迁移完全不受影响。只处理我们自己的三张实体表，
不碰 OpenIddict 的实体（它有自己的时间存储策略）。

副作用：切换后 SQLite 上的既有数据读不出来，开发 / 测试库删掉重建即可。

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
AuthHub__Features__EnableApiDocs=false
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
- [x] **管理后台 UI**：已完成（Razor Pages，7 个页面，见 [管理后台（UI）](#管理后台ui)）。
      后续可补：审计日志导出 CSV、客户端「测试连接」、
      用户的 MFA 重置（目前需用户本人在「我的账户」里关闭后重新绑定）。
- [ ] **MFA 渠道补齐**：邮件与短信当前是"写日志"实现（`LoggingEmailSender` / `LoggingSmsSender`），
      接真实 SMTP / 短信服务商时替换 `IEmailSender` / `ISmsSender` 的注册即可，业务代码无需改动。
- [ ] **MFA 绑定的二维码**：目前「我的账户」只给出密钥与 `otpauth://` URI，让用户在 App 里手动输入。
      加二维码需要引绘图依赖（如 QRCoder）或在前端内置一个 QR 编码器，
      与当前"零外部依赖"的取舍冲突，因此暂缓。
- [ ] **密钥轮换流程**：证书轮换需要在 JWKS 中短暂并存新旧公钥，目前未实现。
