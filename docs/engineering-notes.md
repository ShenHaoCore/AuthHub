# 工程笔记：ASP.NET Core 踩坑速查

本文件沉淀 AuthHub 实现过程中踩到、且**查官方文档不易直接得到**的行为级事实。与 `README.md` 的分工：README 说明本项目的设计与用法，本文件记录框架/依赖库本身的陷阱，跨项目复用。

收录原则：只收「症状 → 根因 → 修法」明确、且换项目还会再撞的条目。一次性调试记录不进这里。
分档原则：**A 档与版本无关**，可以长期信任；**B 档绑定具体版本**，升级前必须重验，过期即删；**C 档是方法**，与框架无关。

> 来源：原 `dotnet-openiddict-server` 技能（950 行，2026-10-19 归档至 `~/.agents/skills-backup/2026-10-19/`）。技能形式不适合装这类病历式内容，压缩后归入本文件。

- [A. 长期有效（框架行为）](#a-长期有效框架行为)
- [B. 有版本时效（升级前重验）](#b-有版本时效升级前重验)
- [C. 工程方法（跨项目复用）](#c-工程方法跨项目复用)

---

## A. 长期有效（框架行为，与版本无关）

### A1. 多认证方案下 Challenge/Forbid 互相踩 → 500

**症状**：`InvalidOperationException: The status code cannot be set, the response has already started`，复现与否取决于策略里**方案的先后顺序**。

**根因**：策略挂了多个方案（会话 Cookie + OpenIddict Bearer）时，校验失败会**依次**对每个方案调 Challenge/Forbid。Cookie 的事件（`OnRedirectToLogin` / `OnRedirectToAccessDenied`）默认**写响应体**；响应一旦开始，排在后面的 Validation 处理器再设状态码就炸。典型表现：`(Bearer, Cookie)` 一切正常，换成 `(Cookie, Bearer)` 就 500——恰好是「不写 body 的排前面、写 body 的排最后」。

**修法**：各方案只补自己的响应头（`WWW-Authenticate`），**谁都不写 body**；循环结束后由 `IAuthorizationMiddlewareResultHandler` 统一写 ProblemDetails。非 API 路径仍交回默认处理器，保持 302 跳登录页。

```csharp
public sealed class MyAppAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context,
        AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (!context.Request.Path.StartsWithSegments("/api")
            || (!authorizeResult.Challenged && !authorizeResult.Forbidden))
        {
            await _default.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        foreach (var scheme in policy.AuthenticationSchemes)
        {
            if (context.Response.HasStarted) break;
            if (authorizeResult.Challenged) await context.ChallengeAsync(scheme);
            else await context.ForbidAsync(scheme);
        }

        if (context.Response.HasStarted) return;
        // 自己写 RFC 7807
    }
}
```

注册时用 `Replace`，别依赖「后注册覆盖先注册」的隐式约定（`IAuthorizationMiddlewareResultHandler` 在 `Microsoft.AspNetCore.Authorization.Policy` 命名空间）。

**附带事实**：`HttpContext.ChallengeAsync/ForbidAsync` 扩展方法只接受单个方案；多方案依次调用是 `AuthenticationService` 的内部行为，要自己写循环。

### A2. `[Produces("application/json")]` 会静默破坏 RFC 7807

`ProducesAttribute` 实现 `IResultFilter`，在结果过滤器阶段 `Clear()` 并覆盖 `ObjectResult.ContentTypes`。所以下面这样写也没用，发出去仍是 `application/json`：

```csharp
return new ObjectResult(problem) { ContentTypes = { "application/problem+json" } };
```

`ControllerBase.Problem(...)` 内建实现同样是 `ObjectResult`，一起被覆盖。只在带 `[Produces]` 的控制器上出现，且「看起来也能用」，排查成本极高。

修法：错误响应改用自定义 `IActionResult` 直接写响应流，绕开内容协商。

### A3. 400 有五个出口，只修一个会出现两套契约

| 出口 | 触发方式 |
|---|---|
| `[ApiController]` 的 `InvalidModelStateResponseFactory` | 请求体**绑定期**失败（`{}` 碰非空引用类型属性 → 隐式 Required） |
| FluentValidation 的 `IAsyncActionFilter` | 值能绑定、业务规则不过 |
| `ControllerBase.Problem(...)` / 自定义辅助方法 | 控制器/服务显式返回 |
| `IAuthorizationMiddlewareResultHandler` | 策略校验失败 |
| 异常处理中间件 | 未捕获异常 |

症状极具误导性：**状态码和 Content-Type 全对，只是少了 `code` 字段**（绑定期失败走框架默认值，无 `code`、`traceId` 是 W3C `traceparent`、标题英文）。

修法：五个出口指向同一个 writer，再写一条「两条路径的顶层字段集必须完全相同」的契约测试。filter 那条要把错误灌进 `context.ModelState` 后复用同一个工厂，**不要**在里面 `new ObjectResult`。

### A4. `WriteAsJsonAsync` 按**声明类型**序列化，会丢派生属性

```csharp
// 形参声明成基类 → ValidationProblemDetails 的 errors 整个不见
Task WriteAsync(ProblemDetails p) => Response.WriteAsJsonAsync(p, options: null, contentType: ct);

// 必须显式传运行时类型
Response.WriteAsJsonAsync(p, p.GetType(), options: null, contentType: ct);
```

判据：改造后错误体变成 `{"title":...,"status":400}` 而没有 `errors` → 先查这一处。**光看代码看不出来**，要用临时 xunit 探针把三种请求形状的真实响应体打出来对比。

### A5. SQLite 上 `DateTimeOffset` 的比较无法翻译

EF Core 对 SQLite 的 `DateTimeOffset` 只支持相等，`>` `<` 运行期抛「could not be translated」。改用 `DateTime` 并明确 UTC，或改成存 ticks / 可排序字符串。

### A6. `[ValidateAntiForgeryToken]` 需要 `AddControllersWithViews()`

用 `AddControllers()` 会运行期 500（`ValidateAntiforgeryTokenAuthorizationFilter` 未注册）。即使没有任何 `.cshtml`，为了这个不易遗漏的安全特性也值得走带视图的注册。

---

## B. 有版本时效（写清版本，升级前重验）

### B1. OpenIddict 5.x

| 常见写法 | 5.x 实际 |
|---|---|
| `SetRefreshTokenReuseInterval(...)` | `SetRefreshTokenReuseLeeway(...)` |
| `SetRefreshTokenExpiration(...)` | 不存在，用 `SetRefreshTokenLifetime(...)` |
| `OpenIddictScopeDescriptor.Claims` | 不存在（Scope 只管 `Resources`；声明归属看 `RegisterClaims`） |
| `app.UseOpenIddict()` | 不需要，认证中间件承载 |
| `options.CodeChallengeMethods` | 5.x 无 builder API |

- `.AddServer(o => ...)` 的 lambda 参数是 **`OpenIddictServerBuilder`**，不是 `OpenIddictServerOptions`——后者上的属性（如 `CodeChallengeMethods`）在这里够不着
- `EnableTokenEndpointPassthrough()` 后，`authorization_code` / `refresh_token` / `client_credentials` **全部**要在控制器里显式 `SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)`；漏一个就返回 400 `unsupported_grant_type`，而**日志显示的是 "token request successfully validated"**
- Server 方案不能作默认方案（`IAuthenticationRequestHandler`，只管 `/connect/*`），默认必须指向 Validation；且 `AddIdentity()` 会把 `DefaultAuthenticateScheme` / `DefaultChallengeScheme` 一起指向 Cookie，三项都要显式覆盖
- `UseLocalServer()` 在 `OpenIddict.Validation.ServerIntegration` 包里，不引包是 `CS1061`
- JWKS 路径是 `/.well-known/jwks`（不是 `jwks.json`），从发现文档的 `jwks_uri` 取，别硬编码
- 下游要离线验签 → 必须 `DisableAccessTokenEncryption()`（默认发 JWE，第三方读不出 claims）
- `openid` / `offline_access` 不要写进 `OpenIddictScopeDescriptor`，由内部处理
- 复用已旋转的 `refresh_token` 不只是拒绝，会**撤销该授权下全部令牌**（重放防护），日志有 `were revoked to prevent a potential token replay attack`
- M2M 令牌调 `/connect/userinfo` 返回 **403 + 空响应体**，错误在 `WWW-Authenticate: Bearer error="insufficient_access"`（ID2095，不是 RFC 6750 的 `insufficient_scope`），且请求**不会** passthrough 到控制器
- `prompt=none` 且用户未同意，必须返回 `consent_required`，**不能**渲染同意页（OIDC 规范）

### B2. .NET 9 内置 OpenAPI 生成器（从 Swashbuckle 迁移时）

- 只认 ApiExplorer 里「可见」的端点，MVC 默认只把 `[ApiController]` 控制器交给它 → 故意不加 `[ApiController]` 的协议/HTML 控制器（含 `/connect/token`、`/connect/userinfo`）会**静默消失**。必须补 `services.Configure<MvcOptions>(o => o.Conventions.Add(new ApiVisibilityConvention()))`。Swashbuckle 的 `AddSwaggerGen` 以前替你做了这件事
- net9 包里**没有** XML 注释源生成器（`analyzers/` 目录都不存在，`.NET 10` 才有）→ 分组描述与 DTO 属性说明会丢，得自己写文档转换器补
- 补上约定后所有控制器都进文档，页面型控制器要 `[ApiExplorerSettings(IgnoreApi = true)]`、Minimal API 要 `.ExcludeFromDescription()`。别改成白名单约定：默认「收录」时漏收是静默故障，默认「排除」时多收一眼可见
- `X509Certificate2(path, pwd, flags)` 过时（SYSLIB0057），改 `X509CertificateLoader.LoadPkcs12FromFile`
- TFM 变更后旧 `bin/obj` 不会自己消失（`.vs/*/DesignTimeBuild/.dtbcache.v2` 也缓存旧依赖），判据是「代码与 csproj 已删干净，但 grep 磁盘 / VS 里还看得见」。但 `~/.nuget/packages/` 是机器级共享缓存，**不要**删

### B3. Scalar 替代 Swagger UI

- 前端 bundle **内嵌在 dll 里**，`/scalar/scalar.js` 约 3.8MB 走本地路由，**不走 CDN** → 内网/离线可用，也不会被 `script-src 'self'` 拦
- 页面里的文档源是相对路径 `openapi/v1.json`，由 `new URL(url, origin + basePath + '/')` 解析，根路径部署时正确落到 `/openapi/v1.json`。这是为子目录部署设计的逻辑，**不要去「修」它**
- **严格 CSP 下页面全白**（页面 200、资源全 200、`#app` 永远空）：模板自带内联 `<script type="module">`，`script-src 'self'` 一拦 initialize 就不执行。纯接口断言发现不了，要 dump-dom 或看 console（DOM 是否从 1KB 涨到几百 KB）。可行做法是给文档路径单独发一条 `script-src 'self' 'unsafe-inline'`，配两条护栏测试（该路径必须含、其余路径的 script-src 段必须不含，按指令段断言而非整串包含）
- `DisableDefaultFonts()` 必须加：默认从 `fonts.scalar.com` 拉 Inter，`font-src 'self'` 必然拦掉，内网还白等 DNS 超时
- 与 Swashbuckle **零依赖关系**（nuspec 各 TFM 依赖组全空、不引 `Microsoft.OpenApi`），唯一契约是「运行时可取的文档 JSON URL」

### B4. `[EndpointSummary]` 会被 XML 注释静默覆盖（Swashbuckle 6.7+ / net8）

Swashbuckle 先生成操作、**再**跑 OperationFilter，而 `IncludeXmlComments` 的 filter 直接给 `operation.Summary` 赋值 → 只要 action 写了 `<summary>`，特性就被吞，无任何警告。修法：自定义 IOperationFilter 从 `ActionDescriptor.EndpointMetadata` 读 `IEndpointSummaryMetadata` 回写，**注册在 IncludeXmlComments 之后**。`[Tags]` 不受影响。

判据小抄：标签是程序集名 = Minimal API 漏标 `WithTags`；摘要带句号且多行 = 被 XML 注释覆盖。

---

## C. 工程方法（跨项目复用，与框架无关）

### C1. 改视图后怎么证明没改坏

不要靠「看 diff 觉得都对」。做法：改前用真实渲染管道 dump HTML 快照 → 改后 dump → 对差异逐条声明「这是等价改写」，快照 + 声明一起进版本库。差异必须被解释完，解释不了的差异就是改坏了。

### C2. 没有前端测试运行器时验证前端行为

用 jsdom 喂**真实渲染出来的页面**（不是手写 fixture），只跑那一个 `<script>`，断言 DOM 变化。三个会让人误判的坑：脚本必须在 `window` 上取依赖、`<script type="module">` 的加载时序、jsdom 不实现布局所以 `offsetWidth` 全 0。另外**同文档锚点必须有 `hashchange` 监听**——`<a href="#x">` 只改 hash 不触发 `click`。

### C3. TagHelper 抽公共骨架的正解

用**基类 + 继承**，不用参数化模板：子类只声明「我有哪些槽位」，骨架在基类 `ProcessAsync` 里统一输出。隐性契约（子类必须覆写哪个属性、槽位渲染顺序）写进基类的 XML 注释，否则下一个改的人一定会破。

### C4. 集成测试的环境变量覆盖

`Program.cs` 若在 `builder.Build()` **之前**读配置（数据库提供程序、连接串、是否强制 HTTPS），`ConfigureAppConfiguration` 是**延迟生效**的，来不及。必须用**环境变量**覆盖，因此要 `[assembly: CollectionBehavior(DisableTestParallelization = true)]`（环境变量是进程级）。SQLite 走 `EnsureCreatedAsync()` 按模型建表，不必为它再维护一套迁移。

### C5. 日常两个坑

- `MSB3021 / MSB3027` 文件被锁不是代码问题：`bin/Debug` 被 IDE 调试占用时，用 `dotnet test -c Release` 绕开（Release 与 Debug 互不干扰），别 kill 别人的调试会话
- 起了本地服务手工验证时 `--urls` 会被 appsettings 里的 Kestrel 端点配置覆盖，改用 `Kestrel__Endpoints__Http__Url` 环境变量
- `.slnx` 需 SDK 9.0.200+；CI/Docker 里直接按项目路径 `dotnet build xxx.csproj`，完全不碰 `.slnx` 就对 SDK 版本不敏感
