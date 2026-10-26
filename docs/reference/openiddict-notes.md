# 与原始需求 / OpenIddict 实际 API 的差异

> 从 [README](../../README.md) 拆出。写这节是因为**照着文档写会编译不过或运行期才炸**。
> 以下每一条都是实际踩过的。
>
> **版本说明**：本文件记录的是 **OpenIddict 5.8** 时代的事实；项目现已升级到 **7.7.2**。
> 升级时确认过的变更见 [README 路线图](../../README.md#已完成)（logout→end-session、
> `HasPrompt`→`HasPromptValue`、`Prompts`→`PromptValues`、userinfo 方法名大小写、
> `Type` 列长 50→150）。其余条目（刷新令牌参数、Discover 文档处理器的内部细节等）
> 在 7.7.2 上**仍然成立**，已逐条复核。
>
> 与 [engineering-notes.md](engineering-notes.md) 的分工：那份按「症状 → 根因 → 修法」
> 讲框架行为、并按可信度分 A/B/C 三档，跨项目可复用；本文件按「本项目要做的功能 → 
> OpenIddict 的实际 API 长什么样」组织，讲的是**写这个认证中心时会撞到的具体差异**，
> 两条路径引用同一事实时以 engineering-notes 为准。

## 目录

1. [OpenIddict 5.8 的 API 与常见示例不一致](#1-openiddict-58-的-api-与常见示例不一致)
2. [passthrough 的真实语义：每个 grant type 都要应用自己收尾](#2-passthrough-的真实语义每个-grant-type-都要应用自己收尾)
3. [OpenIddict 的 Server 方案不能作为默认方案](#3-openiddict-的-server-方案不能作为默认方案)
4. [多认证方案的 Challenge/Forbid 会互相踩](#4-多认证方案的-challengeforbid-会互相踩本项目真实修复的坑)
5. [`[Produces("application/json")]` 会悄悄破坏 RFC 7807](#5-producesapplicationjson-会悄悄破坏-rfc-7807)
6. [`[ValidateAntiForgeryToken]` 需要 `AddControllersWithViews()`](#6-validateantiforgerytoken-需要-addcontrollerswithviews)
7. [`openid` / `offline_access` 不落 Scope 表](#7-openid--offline_access-不落-scope-表)
8. [userinfo 对 M2M 令牌返回 403 `insufficient_access`](#8-userinfo-对-m2m-令牌返回-403-insufficient_access)
9. [密码流程默认关闭](#9-密码流程默认关闭)
10. [API 文档：Scalar + 框架内置生成器](#10-api-文档scalar--框架内置生成器swashbuckle-已整体移除)
11. [为管理后台引入 Razor Pages](#11-为管理后台引入-razor-pages对零视图依赖取舍的修订)
12. [`.NET 8` 的 `WebApplication` 不会自动挂 `UseStaticFiles()`](#12-net-8-的-webapplication-不会自动挂-usestaticfiles)
13. [SQLite 上 `DateTimeOffset` 的比较无法翻译](#13-sqlite-上-datetimeoffset-的比较无法翻译)
14. [`ClaimTypes.Name` 查不到用户名](#14-claimtypesname-查不到用户名本项目实际存在过的缺陷)
15. [本机 SQL Server 的 TCP/IP 协议默认是关闭的](#15-本机-sql-server-的-tcpip-协议默认是关闭的)

---

## 1. OpenIddict 5.8 的 API 与常见示例不一致

| 常见写法 | 5.8 实际 | 说明 |
|----------|----------|------|
| `SetRefreshTokenReuseInterval(...)` | `SetRefreshTokenReuseLeeway(...)` | 只是改名，语义相同 |
| `SetRefreshTokenExpiration(...)`、`TokenExpiration` | 不存在 | 改用 `SetRefreshTokenLifetime(...)` |
| `OpenIddictScopeDescriptor.Claims` | 不存在 | Scope 只管 `Resources`；声明归属由 `RegisterClaims` 决定 |
| `OpenIddictValidationBuilder.UseLocalServer()` | 需要额外的包 | 属于 `OpenIddict.Validation.ServerIntegration`，**不在主包里**，不加 `PackageReference` 就报 `CS1061` |
| `app.UseOpenIddict()` | 不需要 | 5.x 由认证中间件承载，端点处理挂在 request-handler 环节 |

## 2. passthrough 的真实语义：每个 grant type 都要应用自己收尾

官方文档原话是"校验通过后，请求继续走后续管道"。**容易误读成"OpenIddict 会顺手处理掉"**。
实际是：只要开了 `EnableTokenEndpointPassthrough()`，`authorization_code`、`refresh_token`、
`client_credentials` **全部**都要在你的控制器里显式
`SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)` 才算完。
少写任何一个，那个流程就会返回 `400 unsupported_grant_type`，而且日志里显示的是
"token request successfully validated" —— 一开始极具误导性。

`AuthorizationController.Exchange()` 因此按 grant type 拆成了三个分支。

## 3. OpenIddict 的 Server 方案不能作为默认方案

把 `DefaultChallengeScheme` 设成 `OpenIddictServerAspNetCoreDefaults.AuthenticationScheme`，
启动时直接抛：

```
The OpenIddict ASP.NET Core server handler cannot be used as the default scheme handler.
```

Server handler 实现了 `IAuthenticationRequestHandler`，只负责 `/connect/*`，与"默认方案"无关。
默认方必须指向 **Validation** 方案。另外 `AddIdentity()` 会把
`DefaultAuthenticateScheme` / `DefaultChallengeScheme` 都指向会话 Cookie，
**只改 `DefaultScheme` 是不够的**（`DefaultAuthenticateScheme` 优先级更高），三项都要显式覆盖。

## 4. 多认证方案的 Challenge/Forbid 会互相踩（本项目真实修复的坑）

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

## 5. `[Produces("application/json")]` 会悄悄破坏 RFC 7807

`ProducesAttribute` 实现 `IResultFilter`，在结果过滤器阶段会 `Clear()` 并覆盖
`ObjectResult.ContentTypes`。于是：

```csharp
return new ObjectResult(problem) { ContentTypes = { "application/problem+json" } };
// 控制器上有 [Produces("application/json")] → 实际发出去的是 application/json
```

只在带 `[Produces]` 的控制器上出现，且"看起来也能用"，排查成本很高。
修法：错误响应改用自定义 `IActionResult`（`ProblemDetailsResult`）直接写响应流，
绕开内容协商 —— Content-Type 由我们说了算。

**这个坑踩了两次**：第一次修的是控制器（`ApiControllerBase`）与认证层
（`AuthHubAuthorizationResultHandler`）；`ValidationFilter` 当时仍留着
`new ObjectResult(problem) { ContentTypes = { "application/problem+json" } }`，
于是校验失败的响应一直是 `application/json`，而状态码、错误码、文案全对，
只有 Content-Type 不对 —— 谁也不会想到去查它。现在三处统一走 `ApiProblemWriter`，
`ApiErrorContractTests` 把 Content-Type 也钉住了。

## 6. `[ValidateAntiForgeryToken]` 需要 `AddControllersWithViews()`

用 `AddControllers()` 时，`ValidateAntiforgeryTokenAuthorizationFilter` 未注册，请求直接 500：

```
No service for type 'Microsoft.AspNetCore.Mvc.ViewFeatures.Filters.ValidateAntiforgeryTokenAuthorizationFilter'
```

本项目里**协议页**（登录 / 两步验证 / 同意授权）不走 Razor（见 `Pages/HtmlPages.cs`），
但管理后台是 Razor Pages，且为了留住这个声明式、不易遗漏的安全特性，仍走带视图的 MVC 注册。

## 7. `openid` / `offline_access` 不落 Scope 表

这两个由 OpenIddict 内部处理，写进 `OpenIddictScopeDescriptor` 是多余的。
本项目落表的只有：`profile`、`email`、`roles`、`api:read`、`api:write`、`authhub:admin`。

## 8. userinfo 对 M2M 令牌返回 403 `insufficient_access`

用客户端凭证令牌调 `/connect/userinfo`，OpenIddict 在校验阶段就拒绝
（M2M 令牌的 `sub` 是 `client_id`，无法代表一个用户），请求**根本不会 passthrough 到控制器**。
响应是 **403**、响应体为空，错误细节在 `WWW-Authenticate` 头里：

```
Bearer error="insufficient_access", error_description="The user represented by the token is not allowed to perform the requested action.", error_uri="https://documentation.openiddict.com/errors/ID2095"
```

语义与 RFC 6750 一致（令牌有效、身份/范围不足 → 403）。但错误码是 OpenIddict 自己的
`insufficient_access`，**不是** RFC 6750 里的 `insufficient_scope`。这是库的既定契约，
本项目选择照实适配而非覆写。

## 9. 密码流程默认关闭

`AllowPasswordFlow()` 只在 `AuthHub:Features:EnablePasswordFlow=true` 时启用（仅 Development 默认开）。
资源所有者密码流程在 OAuth 2.1 里已被移除，生产不应启用。

## 10. API 文档：Scalar + 框架内置生成器（Swashbuckle 已整体移除）

需求里写的是 Swashbuckle 的 Swagger UI。这里分两步走：先把**展示层**换成 Scalar
（那时生成仍靠 Swashbuckle，只删了它的 Swagger UI 一半）；目标框架升到 net9.0 之后，
**生成器**也换成了框架内置的 `AddOpenApi` / `MapOpenApi`，Swashbuckle 依赖整体删除：

| 变化 | 说明 |
|------|------|
| 包引用 | `Swashbuckle.AspNetCore.SwaggerGen` → `Microsoft.AspNetCore.OpenApi`（.NET 9 起随框架提供的 NuGet 包） |
| 生成入口 | `AddSwaggerGen` + `UseSwagger` → `AddOpenApi` + `MapOpenApi`；文档标题 / OAuth2 方案改由文档转换器声明（见 `OpenApiExtensions`） |
| 文档路径 | `/openapi/{documentName}.json` —— 内置生成器的默认约定与 Scalar 的默认约定相同，两边共用 `OpenApiExtensions.DocumentRoutePattern` 一个常量 |
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

### 文档只收录面向调用方的接口（页面与站点跳转不在其中）

侧边栏里只有三类内容：`/api/*` 各业务接口、`/api/account/*` 账号自助接口、
`/connect/*` OIDC 协议端点。

产出**浏览器页面**的端点被有意排除。文档的读者是 API 调用方，而登录页 / 两步验证页
需要会话 + 防伪令牌，既不能在 Scalar 里直接发起，也不该在接口清单里冒充"接口"：

| 排除的内容 | 位置 | 做法 |
|---|---|---|
| `/account/login`、`/account/2fa`、`/account/logout`、`/account/denied`、`/account/loggedout` | `AccountController` | `[ApiExplorerSettings(IgnoreApi = true)]` |
| `GET /`（302 跳到 `/admin`） | `Extensions/WebApplicationExtensions.cs` | `ExcludeFromDescription()` |

两点容易走错：

- **别用「去掉 `ApiVisibilityConvention`」来实现这件事。** 那个约定是专门给 `/connect/*`
  用的（见下节 ①），去掉它连 OIDC 主端点一起丢。框架的 `ApiVisibilityConvention`
  只在控制器与动作的 `IsVisible` **都为 null** 时才点亮，所以控制器上的显式 `false`
  不会被它覆盖 —— 两件事互不干扰，各自留在声明处。
- **程序化入口是 `/api/account/*`**，与页面的 `/account/*` 是两套（前者 JSON、
  后者 HTML + 表单）。摘掉页面端点不影响任何调用方。

`Page_endpoints_should_stay_out_of_the_document` 守着这条边界：既断言页面没混进来，
也断言 `/connect/token` 还在 —— 两侧共用同一个可见性开关，很容易一刀切过头。

### 换生成器时踩到的两个隐式行为（都有契约测试守着）

**① ApiExplorer 的可见性。** 内置生成器只认 ApiExplorer 里「可见」的端点，而 MVC 默认
只把标注了 `[ApiController]` 的控制器交给 ApiExplorer。本项目有两个控制器**故意不加**
`[ApiController]`（`AccountController` / `AuthorizationController` 要返回 HTML 与 302，
加了会改变运行时行为），于是它们的端点会**静默**从文档里消失 —— 而其余断言全部照常通过。
当年一次丢的是 14 个（7 个 `/account/*` + 7 个 `/connect/*`）。
以前看不到这个坑，是因为 Swashbuckle 在 `AddSwaggerGen` 里注册了同样作用的约定
（其源码原话："Add Mvc convention to ensure ApiExplorer is enabled for all actions"）。
现在由 `OpenApiExtensions.AddAuthHubOpenApi` 显式补上框架的 `ApiVisibilityConvention`
（`IActionModelConvention`，只在 `IsVisible` 为 null 时点亮，所以不会覆盖显式的 false），
`OpenApi_document_should_cover_non_api_controllers_too` 这条测试守着它。

三种配置下的操作总数（去掉约定再生成一次对比即得，别凭印象）：
不补约定 → 45 个、`/connect/*` 整组消失；补约定、还没摘页面 → 60 个；
现在（补约定 + 摘页面）→ 52 个。因此**约定只对 `/connect/*` 有净贡献（7 个）**，
摘页面的开关独立于它，改哪个都不会顺手把另一个弄坏。

**② XML 注释不再被读取。** Swashbuckle 在运行时解析程序集的 `.xml` 注释文件；
内置生成器不读它（随源生成器提供的 XML 注释支持要到 .NET 10 才有）。
影响面实测如下：

| 内容 | 影响 |
|------|------|
| 端点摘要 / 描述 | 无 —— 来自 `[EndpointSummary]` / `[EndpointDescription]`，内置生成器原生支持 |
| 分组描述（Scalar 侧边栏分组标题下的说明） | 会丢，由 `ApplyTagDescriptions` 从控制器 `<summary>` 补回，`Every_tag_should_have_a_description` 守着 |
| DTO 属性说明、参数说明 | 本来就没有（DTO 的 XML 在另一个程序集里，Swashbuckle 当年也只加载了 Api 程序集自己的 .xml） |

侧边栏的分组与每个端点的摘要 / 描述都是中文标注，来自控制器上的
`[Tags]` / `[EndpointSummary]` / `[EndpointDescription]` 特性（.NET 8 起的官方端点元数据，
内置生成器原生读取 —— 因此也不再需要当年为「特性 vs XML 注释优先级」写的过滤器）。
三个约定：

- **新增端点记得同样标注**，否则会掉进以程序集名命名的默认分组（"AuthHub.Api"），
  与中文分组混在一起 —— `Every_operation_should_declare_group_summary_and_description`
  这条契约测试会拦住它。
- **新增的页面型端点记得关掉可见性**（控制器上 `[ApiExplorerSettings(IgnoreApi = true)]`，
  Minimal API 用 `ExcludeFromDescription()`）。这里默认是"收录"，忘了关只会多几个噪音端点，
  不会静默丢接口 —— 有意选了这个方向：漏收是看不见的故障，多收是一眼能看出来的。
- 代码里的 XML `<summary>` 面向维护者，可以写长；特性标注面向调用者，保持一行短句。
  控制器的 `<summary>` 同时充当文档里的分组描述。

## 11. 为管理后台引入 Razor Pages（对"零视图依赖"取舍的修订）

早期版本的注释里写着"为什么不用 Razor：本项目的定位是 OIDC 服务端 + 管理 API"。
那是只有 4 个协议页时的判断。加上 8 个管理页面之后，这个结论不再成立：

| | 协议页（4 个） | 管理后台（8 个页面、上百个字段） |
|---|---|---|
| 手写字符串 | 每个页面一个 `StringBuilder` 方法，可读性尚可 | 会变成数千行拼接，改一个字段要动好几处 |
| 集成测试断言 | 直接对 HTML 字符串断言参数与 scope，很直接 | 断言关键内容即可，不需要逐字段验证视觉 |
| 防伪令牌 | 手工 `IssueAntiforgeryToken()` | Razor Pages 的 POST 处理器**默认自动校验**，不需要逐个挂 `[ValidateAntiForgeryToken]` |

因此：**后台改用 Razor Pages，协议页保持纯字符串**。这也是
`Extensions/WebApiExtensions.cs` 里 `AddRazorPages()`、`Extensions/WebApplicationExtensions.cs`
里 `MapRazorPages()` 的来由。顺带一提，当初选 `AddControllersWithViews()`
而非 `AddControllers()` 就是为了让 `[ValidateAntiForgeryToken]` 能正常工作（见第 6 条），
现在两条路都受益。

## 12. `.NET 8` 的 `WebApplication` 不会自动挂 `UseStaticFiles()`

这个坑很隐蔽，值得单独记一笔：**`/admin` 会正常返回 200，但 `wwwroot` 下的
CSS / JS / favicon 全是 404**。页面结构完整、文字齐全，只是没有样式、按钮点了没反应 ——
只看状态码完全发现不了。

修复就是在管道里显式加一行 `app.UseStaticFiles()`（位置刻意放在限流与请求日志之前：
静态资源是终端处理、不经过认证，先命中就不会在每次页面加载时消耗登录接口的限流额度，
也不会把请求日志刷满 `.css` / `.js`）。集成测试里有一组用例逐一断言这 5 个资源可取。

## 13. SQLite 上 `DateTimeOffset` 的比较无法翻译

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
生产库结构与既有迁移完全不受影响。只处理我们自己的四张实体表
（`ApplicationUser` / `ApplicationRole` / `AuditLog` / `RolePermissionOverride`），
不碰 OpenIddict 的实体（它有自己的时间存储策略）。

> **新增带 `DateTimeOffset` 的实体时，必须把它加进那个类型数组** ——
> 漏掉的症状是"SQL Server 上完全正常、SQLite 上 500"，只有跑集成测试才发现。

副作用：切换后 SQLite 上的既有数据读不出来，开发 / 测试库删掉重建即可。

## 14. `ClaimTypes.Name` 查不到用户名（本项目实际存在过的缺陷）

`IdentityExtensions` 把 `IdentityOptions.ClaimsIdentity.UserNameClaimType` 设成了
**OpenIddict 的 `"name"`**（`Claims.Name`），而不是 .NET 那个长 URI
（`http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name`）。于是任何
`Principal.FindFirst(ClaimTypes.Name)` 都恒为 `null`。

危害是**静默**的：`ICurrentUser.UserName` 一直是空，所有审计记录少了"操作人"
（而 `UserId` 还在，页面只是渲染成「（无用户名）」，长时间没人注意）。直到后台改权限
需要显示操作人时才暴露。

修复：`HttpContextCurrentUser.UserName` 按 `ClaimTypes.Name` → `OpenIddictConstants.Claims.Name`
→ `PreferredUsername` 依次回退。集成测试里有一条用例断言审计表格渲染出 `admin` 字样钉住它。

> 教训：认证方案改了 `UserNameClaimType` 之后，**所有查声明的地方都要跟着核对**，
> 编译器不会提醒。

## 15. 本机 SQL Server 的 TCP/IP 协议默认是关闭的

把连接串写成 `Server=localhost,1433` 连本机实例，会得到**连接超时**（而不是"找不到实例"
这种有指向性的报错），容易误判成实例没启动。实测本机安装的 SQL Server 2022 默认实例：
`sqlservr.exe` 在跑、服务是 Running，但 `netstat` 里 **1433 没有任何监听** ——
因为 TCP/IP 协议是安装后的默认关闭状态（注册表
`MSSQL16.MSSQLSERVER\MSSQLServer\SuperSocketNetLib\Tcp` 的 `Enabled=0`）。

> 容易看错的中间现象：`1434` 端口确实有监听，但那是 **DAC（专用管理员连接）/ Browser**
> 的端口，不是实例的查询端口。看到 1434 有反应就以为"实例正常、端口通了"是错的。

SqlClient 的连接协议是按服务名解析的，同样写"本机"结果不同：

| 连接串里的 Server | 走的协议 | 本机 TCP 关闭时 |
|---|---|---|
| `(local)` 或 `.` | 共享内存 | **可以连** |
| `localhost` / `127.0.0.1` / `机器名` | TCP | 连接超时 |

所以开发配置用 `Server=(local)`。共享内存只对本机进程可用 —— 开发够用，但**不要**把它
当成部署方案（容器跨主机必须走 TCP）。确实需要标准端口时，用 SQL Server 配置管理器
启用 TCP/IP（`IPAll` 的 TCP 端口填 1433）并重启服务；重启会掐断该实例上所有其他库的连接，
多项目共用一台实例时要先确认。

> 判断实例是否可连，别只看进程在不在。可靠的做法是直接用 `sqlcmd` 连一次：
> ```
> sqlcmd -S "(local)" -E -C -Q "SELECT @@VERSION; SELECT IS_SRVROLEMEMBER('sysadmin');"
> ```
> 第二条顺便告诉你当前 Windows 账号是不是 `sysadmin` —— 不是的话建库会失败。