# AuthHub

[![CI](https://github.com/ShenHaoCore/AuthHub/actions/workflows/ci.yml/badge.svg)](https://github.com/ShenHaoCore/AuthHub/actions/workflows/ci.yml)
[![CodeQL](https://github.com/ShenHaoCore/AuthHub/actions/workflows/codeql.yml/badge.svg)](https://github.com/ShenHaoCore/AuthHub/actions/workflows/codeql.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

基于 **.NET 10 + OpenIddict 7.7** 的统一认证授权中心（Identity Provider）。

为下游多个应用提供一套账号体系与登录入口：一处登录，多处通行（SSO）；下游服务既能用
Bearer 令牌调用 API，也能凭 `jwks_uri` 在本地离线校验令牌，无需回源。

## 📖 文档索引

本 README 是门面：只放"这是什么、怎么跑起来、有什么功能"。深入内容按主题拆在
[`docs/`](docs/) 下 —— 改动细节时去对应文档，不要往 README 里堆。

**两份笔记的分工**：[`docs/*-notes.md`](docs/)（`openiddict-notes` / `engineering-notes`）
记的是**框架与依赖库本身怎么坑人**，换项目还会再撞；其余文档讲**本项目**的设计与用法。
两者不重复 —— 前者按「症状 → 根因 → 修法」组织、按可信度分档，后者按主题组织。

| 文档 | 内容 |
|------|------|
| [docs/architecture.md](docs/architecture.md) | 分层架构、两类页面为什么走两条路、有意保留的分层取舍 |
| [docs/configuration.md](docs/configuration.md) | 四个配置来源、三份文件分工、启动期环境护栏、`dotnet ef` 的连接串来源 |
| [docs/admin-ui.md](docs/admin-ui.md) | 后台 7 个页面、**权限模型（三层授权）**、前端约定、`<ah-*>` TagHelper、被显式处理掉的陷阱 |
| [docs/api-reference.md](docs/api-reference.md) | 端点清单与权限要求、错误响应约定（RFC 7807 统一出口） |
| [docs/security-design.md](docs/security-design.md) | 安全设计要点逐项（PKCE、令牌轮换、限流、CSRF、CSP…） |
| [docs/deployment.md](docs/deployment.md) | 生产配置清单、容器 / 反代的两个隐性依赖、迁移、Docker |
| [docs/testing.md](docs/testing.md) | 测试覆盖清单、端到端脚本、Postman 集合 |
| [docs/openiddict-notes.md](docs/openiddict-notes.md) | **踩坑记录**：照 OpenIddict 官方文档写会编译不过 / 运行期才炸的 15 处差异 |
| [docs/engineering-notes.md](docs/engineering-notes.md) | **框架行为陷阱速查**：ASP.NET Core / OpenIddict 侧的病历式记录，分 A/B/C 三档维护，跨项目复用 |

---

## 功能范围

| # | 能力 | 实现位置 |
|---|------|----------|
| 1 | 用户注册 / 登录（含密码策略、锁定、安全戳） | `AccountController`、`AccountApiController`、`AccountService` |
| 2 | 授权码流程（强制 PKCE）、同意页 | `AuthorizationController.Authorize` |
| 3 | 客户端凭证流程（M2M） | `AuthorizationController.Exchange` |
| 4 | 刷新令牌 + 旋转 + 重放防护（自动撤销整条授权） | `AuthorizationController.Exchange` |
| 5 | 单点登录（`prompt=none` 静默签发）与全局登出 | `AuthorizationController` |
| 6 | MFA：TOTP（验证器 App）、邮箱 / 短信验证码 | `AccountApiController`、`AccountService`、`ITwoFactorChannel`（各通道自管目标解析与文案） |
| 7 | RBAC：角色 → 权限 → 令牌内的 `authhub:permission` 声明（归属三级叠加：出厂默认 / 配置 / 后台落库覆盖，启动期校验） | [权限模型](docs/admin-ui.md#权限模型)、`AuthorizationPolicyExtensions`、`RolePermissionOptions` + `RolePermissionMap.CreateDefaultRoles()`、`LayeredRolePermissionMap`、`RolePermissionOverrides` 表 |
| 8 | 客户端 CRUD（密钥哈希存储、支持轮换） | `ClientsController`、`OpenIddictClientAdminService` |
| 9 | Scope 管理 | `ScopesController`、`ScopeService` |
| 10 | 令牌撤销与黑名单（未过期 JWT 立即失效） | `TokensController`、`EnableTokenEntryValidation` |
| 11 | **管理后台 UI**（Razor Pages，ABP 版式）：仪表盘、用户、角色与权限、客户端、Scope、审计日志、我的账户 | [docs/admin-ui.md](docs/admin-ui.md)、`src/AuthHub.Api/Pages/`、`wwwroot/` |

附带：审计日志（`AuditLogs` 表 + `/api/audit-logs`）、限流、安全响应头、健康检查、OpenAPI 文档（Scalar UI）。

## 技术栈

| 组件 | 版本 | 说明 |
|------|------|------|
| .NET / ASP.NET Core | 10.0（LTS） | `net10.0`，`LangVersion 14`，可空引用开启 |
| OpenIddict | **7.7.2** | Server + Validation + EF Core 集成 |
| Entity Framework Core | 10.0.13 | Identity / OpenIddict / 自定义表共用一个 `DbContext` |
| ASP.NET Core Identity | 10.0 | 用户、角色、密码哈希、锁定、MFA |
| 数据库 | SQL Server（生产）/ SQLite（开发与测试） | 二者切换只需改配置 |
| 实体 → DTO 映射 | — | 手写扩展方法（`EntityToDtoMapper`）。曾经用 AutoMapper 13.x，因 13.x 存在无补丁的高危漏洞（CVE-2026-32933）且 15+ 改为 RPL/商业双许可而移除——映射只有三张扁平表，手写成本更低 |
| FluentValidation | 11.x | 校验规则集中在 Application 层，经 `ValidationFilter` 自动执行 |
| Serilog | 8.x | Console + 按天滚动文件，可平滑接入 Seq / ELK |
| Microsoft.AspNetCore.OpenApi | 10.0（框架内置） | 生成 OpenAPI 文档 JSON（`AddOpenApi` / `MapOpenApi`），原生读取 `[Tags]` / `[EndpointSummary]` / `[EndpointDescription]`；.NET 10 起连 XML 注释也一并读取 |
| Scalar.AspNetCore | 2.17 | 交互式 API 文档 UI，替代 Swagger UI；前端资源内嵌在程序集里、不依赖 CDN |
| Razor Pages | 10.0（ASP.NET Core 内置） | 管理后台的视图层：`_AdminLayout` 共享布局 + 服务端渲染的列表与表单 |
| 前端 | 原生 CSS / JS，无构建步骤 | `wwwroot/css`、`wwwroot/js`，零 npm、零打包器、零 CDN |
| xUnit + Moq + FluentAssertions | — | 单元测试 159 项、集成测试 133 项 |

## 架构

```
AuthHub.slnx
├── src/
│   ├── AuthHub.Domain/           实体、常量、出厂默认的角色-权限映射（不依赖任何框架）
│   ├── AuthHub.Application/      用例编排、接口、DTO、校验器、Result 体系、配置绑定与校验
│   ├── AuthHub.Infrastructure/   EF Core、迁移、OpenIddict 适配、各类实现
│   └── AuthHub.Api/              控制器、协议页、管理后台（Razor Pages）、服务注册与管道
├── tests/
│   ├── AuthHub.UnitTests/        领域规则、Result、校验器、权限映射三层的叠加语义
│   └── AuthHub.IntegrationTests/ WebApplicationFactory 起真实管道跑 OIDC / 鉴权 / 限流 / 错误契约 / 文档端点 / 后台
├── docs/                         深度文档（见上方索引）+ Postman 集合
├── .github/workflows/ci.yml      编译 + 单元/集成测试 + 依赖漏洞检查
└── Dockerfile                    多阶段构建，非 root 运行
```

依赖方向：

```
Api ──▶ Application ──▶ Domain
 │                        ▲
 └──▶ Infrastructure ─────┘   （Api → Infrastructure 仅用于 DI 注册，不直接使用其类型）
```

分层的详细构成、**协议页与管理后台为什么刻意走两条不同的路**，以及几处"看起来不干净但有意
保留"的分层取舍，见 [docs/architecture.md](docs/architecture.md)。

## 快速开始

### 前置条件

- **.NET SDK 10.0.100 及以上**。运行目标框架是 `net10.0`（见 `Directory.Build.props`），
  解决方案文件用的是 `.slnx` 格式。
  不想升级 SDK 的话，也可以按项目路径操作：`dotnet build src/AuthHub.Api/AuthHub.Api.csproj`
  （CI 与 Dockerfile 就是这么做的，因此它们对 SDK 版本不敏感）。
- 数据库，任选其一：
  - **本机 SQL Server 默认实例** —— `appsettings.Development.json` 的默认值，连接串是 `Server=(local)`；
  - SQL Server LocalDB —— 把连接串换成 `Server=(localdb)\MSSQLLocalDB` 即可，其余不变；
  - SQLite —— 零依赖，见 [切到 SQLite](#切到-sqlite零依赖跑起来)。
- 开发用 HTTPS 证书（`dotnet dev-certs https --trust`）

### 本机运行

```bash
git clone https://github.com/ShenHaoCore/AuthHub.git
cd AuthHub

dotnet dev-certs https --trust          # 首次：启用开发证书
dotnet ef database update -p src/AuthHub.Infrastructure -s src/AuthHub.Api   # 建库
dotnet run --project src/AuthHub.Api
```

> **为什么写 `Server=(local)` 而不是 `localhost,1433`。**
> 本机 SQL Server 实例的 **TCP/IP 协议默认是关闭的**（`SQL Server 配置管理器 → SQL Server
> 网络配置 → 协议`），1433 根本没有在监听 —— 这是安装时的默认状态，不是故障。
> SqlClient 对 `(local)` / `.` 走**共享内存**，对 `localhost` 走 **TCP**，所以前者能连上、
> 后者会得到"连接超时"。
>
> 共享内存只对本机进程可用，开发完全够用。真需要标准端口（外部工具、容器连接）时，
> 去配置管理器里把 TCP/IP 置为启用（IPAll 的 TCP 端口填 1433）并**重启 SQL Server 服务** ——
> 注意重启会掐断这台机器上其他库的连接。
>
> 认证用 Windows 认证（`Trusted_Connection=True`）而不是 `sa`：开发机当前账号通常已经是
> `sysadmin`（用 `SELECT IS_SRVROLEMEMBER('sysadmin')` 确认），省掉一处明文口令。
> 库名 `AuthHub.Dev` 会在首次运行时自动创建，日志里能看到 `数据库结构已就绪（SqlServer）。`
>
> 完整经过见 [docs/openiddict-notes.md 第 15 条](docs/openiddict-notes.md#15-本机-sql-server-的-tcpip-协议默认是关闭的)。

启动后：

| 地址 | 说明 |
|------|------|
| <https://localhost:5001/admin> | **管理后台**（Razor Pages，ABP 版式；开发环境用 `admin` / `Admin@12345` 登录） |
| <https://localhost:5001/scalar/v1> | Scalar 交互式 API 文档（Development 默认开启，可交互授权）；文档 JSON 在 `/openapi/v1.json` |
| <https://localhost:5001/.well-known/openid-configuration> | OIDC 发现文档 |
| <https://localhost:5001/health> | 健康检查 |
| <http://localhost:5000> | 同端口组的 HTTP 端点 |

> **调试启动的默认落点是站点根。** `launchSettings.json` 里刻意不设 `launchUrl`，
> 所以 F5（或 `dotnet run --launch-profile https`）打开的是 `https://localhost:5001/`，
> 由 `GET /` 302 到 `/admin`，未登录再跳登录页 —— 与"F5 就是打开这个站点"的直觉一致。
> API 文档不是站点门面，需要时手工打开 `/scalar/v1`；想改回"启动即开文档"，
> 给两个 profile 各加一行 `"launchUrl": "scalar/v1"` 即可。

`appsettings.Development.json` 默认：`Seeding.Enabled=true`、`MigrateOnStartup=true`、
`RequireHttps=true`、`EnablePasswordFlow=true`。**生产环境这三项都不要直接沿用**，见
[docs/deployment.md](docs/deployment.md)。

### 切到 SQLite（零依赖跑起来）

```jsonc
{
  "Database": { "Provider": "Sqlite" },
  "ConnectionStrings": { "DefaultConnection": "Data Source=authhub.db" }
}
```

SQLite 路径下启动时走 `EnsureCreatedAsync()` 按模型建表（不套用 SQL Server 迁移），
因此不需要为 SQLite 另维护一套迁移文件。**SQLite 仅用于开发与集成测试，不要上生产。**

## 种子数据

仅在 `AuthHub:Seeding:Enabled=true` 时执行（幂等，先查后建）。所有口令/密钥都可被
配置项 `AuthHub:Seed:*` 覆盖，代码里的是**仅限开发的**默认值。

| client_id | 类型 | 密钥 | 允许的流程 | 用途 |
|-----------|------|------|-----------|------|
| `spa-client` | public | 无 | 授权码 + PKCE、刷新令牌 | 前端 SPA（回调 `https://localhost:3000/callback`） |
| `web-client` | confidential | `web-secret` | 授权码 + PKCE、刷新令牌 | 服务端 Web 应用（回调 `https://localhost:5002/signin-oidc`） |
| `m2m-service` | confidential | `m2m-secret` | 仅客户端凭证 | 后端服务间调用（`api:read` / `api:write`） |

> `spa-client` 设为 public 客户端（无密钥），安全边界完全由 PKCE 承担；
> 代码里 `ClientValidator` 会拒绝"public 客户端携带密钥"这种自相矛盾的配置。

| 用户名 | 密码 | 角色 |
|--------|------|------|
| `admin` | `Admin@12345` | `Administrator` |
| `alice` | `Alice@12345` | `User` |

| 角色 | 出厂默认权限 |
|------|------|
| `Administrator` | `clients.manage`、`scopes.manage`、`users.manage`、`roles.manage`、`tokens.revoke`、`audit.read` |
| `UserManager` | `users.manage`、`tokens.revoke` |
| `Auditor` | `audit.read` |
| `User` | 无管理权限 |

表里是**代码里的出厂默认**，实际生效值可以被两层覆盖：`AuthHub:RolePermissions`
配置节（部署期基线）与后台勾选落库的运行时覆盖（见 [权限模型](docs/admin-ui.md#权限模型)），
后者优先级更高。仓库里 `appsettings.json` 的默认值与这张表逐字一致，
有一条集成测试钉住两者不许漂移。

单元测试里有一条断言专门守着"每个已声明的权限至少被一个内置角色覆盖"，
防止新增权限后忘记挂到角色上（那会变成谁也拿不到的幽灵权限）。

## 测试与持续集成

```bash
dotnet build AuthHub.slnx              # 须 0 警告 0 错误
dotnet test AuthHub.slnx -c Release    # 单元 159 + 集成 133
```

流水线在 GitHub Actions 上（`.github/workflows/`），全部跑在 `ubuntu-latest`：

| 工作流 | 触发 | 内容 |
|--------|------|------|
| `ci.yml` | push / PR 到 main、手动 | Release 编译（`-warnaserror`，警告即失败）、单元测试、集成测试、Dockerfile 构建校验（防止镜像烂掉）、`dotnet list package --vulnerable` 依赖漏洞扫描 |
| `codeql.yml` | push / PR 到 main、每周一 | CodeQL 静态分析（C#），结果进仓库的 Security 标签页 |
| `.github/dependabot.yml` | 每周 | NuGet 与 GitHub Actions 版本的依赖自动更新 PR |

分支约定：`main` 开启了轻量保护——**禁止强推与删除**；日常提交直接 push 即可。

测试覆盖清单、端到端脚本与 Postman 集合见 [docs/testing.md](docs/testing.md)。
安全漏洞的报告方式见 [SECURITY.md](SECURITY.md)（不要开公开 Issue）。

## 待办与扩展方向

### 已完成

- [x] **管理后台 UI**（Razor Pages，7 个页面，见 [docs/admin-ui.md](docs/admin-ui.md)）。
      后续可补：审计日志导出 CSV、客户端「测试连接」、
      用户的 MFA 重置（目前需用户本人在「我的账户」里关闭后重新绑定）。
- [x] **角色→权限映射可配置**：已完成。授权拆成三层（能力目录 / 授权策略 / 授权关系），
      ② 授权策略既可在 `AuthHub:RolePermissions` 里配（配错在启动期就失败），
      也可在后台 `/admin/roles` 勾选落库（即时生效）；③ 授权关系本来就走后台 UI。
      详见 [权限模型](docs/admin-ui.md#权限模型)。
- [x] **权限归属的后台编辑**：已完成。`/admin/roles` 的权限树改成可勾选提交，落
      `RolePermissionOverrides` 表（一行一角色的**完整权限快照**，空数组=显式收回全部），
      `IRolePermissionMap` 变成"出厂默认 ← 配置 ← 数据库覆盖"三级叠加 + 快照缓存 + `Invalidate()`。
      带防自锁护栏（不许摘掉最后一个持有 `roles.manage` 的角色）、服务端二次校验（ordinal 比对权限名）、
      每次变更写审计，并在角色列表上标「已自定义」、只在真被覆盖过时露出「恢复默认」。
      **已知边界**：缓存失效只覆盖本进程，多实例部署时其他实例靠缓存自然过期（它们各自只影响自己的登录签发）；
      权限点本身仍改不了（挂在端点的 `[Authorize]` 上，属程序集）。

### 待办

- [x] **升级到 .NET 10（LTS）**：已完成。net9.0 的支持在 2026-11-10 结束，而认证中心跑在
      无补丁的运行时上风险偏高，因此把 TFM 升到 `net10.0`（LTS，支持到 2028-11）、
      `LangVersion` 14、全部 `Microsoft.*` 包升到 10.0.13、`Dockerfile` 两个基础镜像换成 10.0。
      一并处理了迁移代价（实测，不是照文档猜的）：
      ① **OpenIddict 5.8 → 7.7.2**（7.x 才有 `net10.0` 专属 TFM）：logout 端点更名 end-session
      （`SetEndSessionEndpointUris` / `Permissions.Endpoints.EndSession`）、`HasPrompt` →
      `HasPromptValue`、`Prompts` → `PromptValues`、`SetUserinfoEndpointUris` →
      `SetUserInfoEndpointUris`（大写 I）；
      ② **数据库 schema 变更**：`OpenIddictTokens.Type` 列长 50 → 150，新增迁移
      `UpgradeToOpenIddict7`（加宽，无数据丢失风险；回滚时才会截断）；
      ③ **`Microsoft.OpenApi` 2.x**：`Microsoft.OpenApi.Models` 命名空间合并进根命名空间、
      `document.SecurityRequirements` → `document.Security`、安全方案引用改用
      `OpenApiSecuritySchemeReference`；
      ④ **.NET 10 弃用项**：`Microsoft.AspNetCore.HttpOverrides.IPNetwork` → `System.Net.IPNetwork`、
      `ForwardedHeadersOptions.KnownNetworks` → `KnownIPNetworks`、
      `Microsoft.Extensions.*` 显式包引用触发 NU1510（已随共享框架自动可用，移除）。

### 待办

- [ ] **禁用 PKCE 的 `plain` 模式**：OAuth 2.1 已弃用 `plain`（`code_challenge == code_verifier`，
      等于把校验值明文放在前端渠道）。本项目强制了 PKCE，但 **OpenIddict 7.7.2 依然没有 builder API**
      能限制 `code_challenge_method`（已核对 7.7.2 的公开 API，`AttachCodeChallengeMethods` 仍是内部处理器），
      发现文档会同时声明 `plain` 与 `S256`。彻底禁掉需要两步：
      ① 加一个 `ValidateAuthorizationRequestContext` 内联事件处理器，拒绝非 `S256` 的请求；
      ② 替换 `AttachCodeChallengeMethods` 发现文档处理器，只声明 `S256`。
      原先预期"升到 6.x 就能直接配置"，升级后实测不成立，仍需走事件处理器路线。
- [ ] **Redis 令牌缓存**：`EnableTokenEntryValidation()` 目前每次校验都查库，
      高并发下会成为瓶颈；可把令牌状态缓存到 Redis 并订阅撤销事件。
- [ ] **分布式 JWKS 校验**：下游目前可离线验签；进一步可加公钥轮换与缓存控制头。
- [ ] **OpenTelemetry**：分布式追踪（导出到 OTLP）。
- [ ] **CI 安全扫描**：`dotnet list package --vulnerable` 与 OWASP Dependency Check
      （见 `.github/workflows/ci.yml`）。
- [ ] **MFA 渠道补齐**：邮件与短信当前是"写日志"实现（`LoggingEmailSender` / `LoggingSmsSender`），
      接真实 SMTP / 短信服务商时替换 `IEmailSender` / `ISmsSender` 的注册即可，业务代码无需改动。
      **加一条新通道**（如企业微信、WhatsApp）则是实现一个 `ITwoFactorChannel`
      （自己解析投递目标 + 自己写文案）再注册一行；`AccountService` 不认识任何通道名。
      通道的 `Provider` 必须与 Identity 的 TwoFactorTokenProvider 名逐字一致，
      否则 `GenerateTwoFactorTokenAsync` 找不到 provider —— 有一条单元测试钉住这两个字符串。
- [ ] **MFA 绑定的二维码**：目前「我的账户」只给出密钥与 `otpauth://` URI，让用户在 App 里手动输入。
      加二维码需要引绘图依赖（如 QRCoder）或在前端内置一个 QR 编码器，
      与当前"零外部依赖"的取舍冲突，因此暂缓。
- [ ] **密钥轮换流程**：证书轮换需要在 JWKS 中短暂并存新旧公钥，目前未实现。

---

> 需要读更多细节时，请看 [`docs/`](docs/) 下的专题文档；发现文档与实际实现不一致，
> 以代码为准并顺手修正文档。