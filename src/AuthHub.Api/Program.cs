using AuthHub.Api.Extensions;
using AuthHub.Api.Filters;
using AuthHub.Api.Middleware;
using AuthHub.Api.Models;
using AuthHub.Api.Services;
using AuthHub.Application;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using AuthHub.Domain.Entities;
using AuthHub.Infrastructure;
using AuthHub.Infrastructure.Data;
using AuthHub.Infrastructure.Data.Seed;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenIddict.Validation.AspNetCore;
using Serilog;
using static OpenIddict.Abstractions.OpenIddictConstants;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// 1. 结构化日志（Serilog：Console + File，生产可追加 Seq / ELK Sink）
// ---------------------------------------------------------------------------
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// ---------------------------------------------------------------------------
// 2. 运行期配置读取
// ---------------------------------------------------------------------------
var databaseProvider = builder.Configuration["Database:Provider"] ?? DatabaseProviders.SqlServer;
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("缺少连接字符串 ConnectionStrings:DefaultConnection。");
var requireHttps = builder.Configuration.GetValue("AuthHub:Security:RequireHttps", true);
var enablePasswordFlow = builder.Configuration.GetValue("AuthHub:Features:EnablePasswordFlow", false);
var enableApiDocs = builder.Configuration.GetValue("AuthHub:Features:EnableApiDocs", builder.Environment.IsDevelopment());
var keyDescription = "未初始化";

// ---------------------------------------------------------------------------
// 3. EF Core 数据上下文（Identity + OpenIddict + 自定义表共用一个 DbContext）
// ---------------------------------------------------------------------------
builder.Services.AddDbContext<AuthHubDbContext>(options =>
{
    if (DatabaseProviders.IsSqlite(databaseProvider))
    {
        options.UseSqlite(connectionString);
    }
    else
    {
        options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(maxRetryCount: 3));
    }

    // 注册 OpenIddict 的实体映射（Application / Authorization / Scope / Token / Device）
    options.UseOpenIddict();
});

// ---------------------------------------------------------------------------
// 4. ASP.NET Core Identity（用户、角色、密码哈希、锁定、MFA）
// ---------------------------------------------------------------------------
builder.Services.AddIdentity<ApplicationUser, ApplicationRole>(options =>
{
    // 密码策略
    options.Password.RequireDigit = true;
    options.Password.RequiredLength = 8;
    options.Password.RequireNonAlphanumeric = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireLowercase = true;
    options.Password.RequiredUniqueChars = 4;

    // 锁定策略：连续 5 次失败锁定 15 分钟
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.AllowedForNewUsers = true;

    // 用户策略
    options.User.RequireUniqueEmail = true;
    options.SignIn.RequireConfirmedEmail = builder.Configuration.GetValue("AuthHub:Security:RequireConfirmedEmail", false);
    options.SignIn.RequireConfirmedAccount = false;

    // 关键：把 Identity 的声明类型对齐到 OIDC 标准声明。
    // 否则 Identity 会产出 nameidentifier / name 这类长 URI 声明，
    // OpenIddict 无法把它们映射成 sub / name，令牌里就会缺少身份信息。
    options.ClaimsIdentity.UserIdClaimType = Claims.Subject;
    options.ClaimsIdentity.UserNameClaimType = Claims.Name;
    options.ClaimsIdentity.EmailClaimType = Claims.Email;
    options.ClaimsIdentity.RoleClaimType = Claims.Role;
})
.AddEntityFrameworkStores<AuthHubDbContext>()
.AddDefaultTokenProviders();

// 会话 Cookie 策略
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "authhub.session";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    // Lax：既要支持从下游应用跳转回本服务时携带会话（SSO），又要避免 CSRF 面扩大
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = requireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    options.LoginPath = "/account/login";
    options.LogoutPath = "/account/loggedout";
    options.AccessDeniedPath = "/account/denied";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;

    // Cookie 方案默认的“未登录 / 无权限”行为是 302 跳转到 HTML 登录页。
    // 对 /api/*（SPA、脚本、移动端）这不可用，它们需要能编程处理的 401 / 403。
    //
    // 注意这里在 /api 下**什么都不做**，而不是直接写 ProblemDetails：
    // 管理类策略同时挂了 Cookie 与 Bearer 两个方案，ASP.NET Core 会依次对每个方案
    // 执行 Challenge/Forbid。若 Cookie 先把响应体写出去，响应即已开始，
    // 后面的 OpenIddict Validation 再去设置状态码就会抛
    // "The status code cannot be set, the response has already started"。
    // 因此 /api 下的错误响应统一由 AuthHubAuthorizationResultHandler 在
    // 方案循环结束后一次性写出；这里只负责拦住 Cookie 默认的重定向行为。
    options.Events.OnRedirectToLogin = static context => HandleAuthFailureAsync(context, isChallenge: true);

    options.Events.OnRedirectToAccessDenied = static context => HandleAuthFailureAsync(context, isChallenge: false);
});

// 安全戳校验间隔：默认 30 分钟。缩短到 5 分钟可让“停用账号 / 变更角色”更快生效
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
    options.ValidationInterval = TimeSpan.FromMinutes(5));

// 防伪令牌（登录 / 同意表单）
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "authhub.antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = requireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
});

// ---------------------------------------------------------------------------
// 5. OpenIddict（OAuth 2.0 / OIDC 服务端 + 本地令牌校验）
// ---------------------------------------------------------------------------
builder.Services.AddOpenIddict()

    // 5.1 核心：把客户端 / 授权 / 令牌 / Scope 存到 EF Core
    .AddCore(options =>
    {
        options.UseEntityFrameworkCore()
               .UseDbContext<AuthHubDbContext>();
    })

    // 5.2 服务端
    .AddServer(options =>
    {
        // 端点
        options.SetAuthorizationEndpointUris("/connect/authorize")
               .SetTokenEndpointUris("/connect/token")
               .SetLogoutEndpointUris("/connect/logout")
               .SetIntrospectionEndpointUris("/connect/introspect")
               .SetRevocationEndpointUris("/connect/revoke")
               .SetUserinfoEndpointUris("/connect/userinfo");

        // 授权类型
        options.AllowAuthorizationCodeFlow()
               .AllowClientCredentialsFlow()
               .AllowRefreshTokenFlow();

        if (enablePasswordFlow)
        {
            // 资源所有者密码流程仅用于内部 / 迁移场景，生产默认关闭
            options.AllowPasswordFlow();
        }

        // 授权码流程强制 PKCE（OAuth 2.1 要求，防授权码被拦截后直接兑换）
        //
        // 注意：OpenIddict 5.x 没有 builder API 能限制 code_challenge_method，
        // 因此发现文档里会同时声明 plain 与 S256。plain（code_challenge == code_verifier）
        // 在 OAuth 2.1 里已被弃用——它把校验值明文放在前端渠道，攻击者只要读到授权请求
        // 就拿到了 verifier。本项目所有示例客户端都用 S256；要彻底禁掉 plain 需要
        // 自定义 ValidateAuthorizationRequestContext 事件处理器并替换发现文档处理器，
        // 见 README「待办与扩展方向」。
        options.RequireProofKeyForCodeExchange();

        // 访问令牌只签名、不加密。
        //
        // OpenIddict 默认会用服务端加密证书把 Access Token 加密成 JWE，这适合“只有本服务
        // 能读懂令牌”的场景；但本项目的定位是统一认证中心，下游微服务需要凭 jwks_uri
        // 在本地离线校验令牌（见第十章“分布式令牌验证 JWKS”），因此必须产出可被第三方
        // 读取的 JWS。若某些部署希望隐藏令牌内容，请移除这一行并改用 introspection。
        options.DisableAccessTokenEncryption();

        // 令牌生命周期
        options.SetAccessTokenLifetime(TimeSpan.FromHours(1));
        options.SetIdentityTokenLifetime(TimeSpan.FromMinutes(30));
        options.SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(5));
        options.SetRefreshTokenLifetime(TimeSpan.FromDays(30));

        // 刷新令牌旋转：旧令牌立即失效（宽限期 0），被盗后也只用得了一次
        options.SetRefreshTokenReuseLeeway(TimeSpan.Zero);

        // 自定义声明与 Scope 必须注册，否则会被 OpenIddict 过滤掉
        options.RegisterClaims(
            AuthHubConstants.ClaimTypes.Permission,
            AuthHubConstants.ClaimTypes.DisplayName);

        options.RegisterScopes(
            AuthHubConstants.Scopes.ApiRead,
            AuthHubConstants.Scopes.ApiWrite,
            AuthHubConstants.Scopes.Admin);

        // 显式指定 issuer，保证令牌 iss 与发现文档在反向代理后依然正确
        var issuer = builder.Configuration["AuthHub:Issuer"];
        if (!string.IsNullOrWhiteSpace(issuer))
        {
            options.SetIssuer(issuer);
        }

        // 签名 / 加密密钥（生产：持久化证书；开发：开发证书；CI：临时密钥）
        keyDescription = options.ConfigureKeys(builder.Configuration, builder.Environment);

        var aspNetCore = options.UseAspNetCore()
               .EnableAuthorizationEndpointPassthrough()
               .EnableTokenEndpointPassthrough()
               .EnableLogoutEndpointPassthrough()
               .EnableUserinfoEndpointPassthrough()
               .EnableStatusCodePagesIntegration();

        if (!requireHttps)
        {
            // 本地 http 调试 / 集成测试：关闭“端点必须走 TLS”的强制要求
            aspNetCore.DisableTransportSecurityRequirement();
        }
    })

    // 5.3 校验：本进程内自校验（同一实例既签发又校验）
    .AddValidation(options =>
    {
        options.UseLocalServer();
        options.UseAspNetCore();

        // 每次校验都检查令牌是否已被撤销：
        // 这让“管理员强制下线”对尚未过期的 JWT 也立即生效（即令牌黑名单）
        options.EnableTokenEntryValidation();
    });

// ---------------------------------------------------------------------------
// 6. 认证方案：默认 Bearer 令牌，浏览器流程显式使用 Identity Cookie
// ---------------------------------------------------------------------------
builder.Services.AddAuthentication(options =>
{
    // 默认方案是「令牌校验」：受保护 API 未携带令牌时直接返回 401，
    // 而不是被 302 到 HTML 登录页（后者对 SPA / 脚本 / 网关都是错误行为）。
    //
    // 为什么必须显式写这三项：AddIdentity() 会把
    // DefaultAuthenticateScheme 与 DefaultChallengeScheme 都指向 Identity 的会话 Cookie 方案，
    // 只改 DefaultScheme 是不够的（DefaultAuthenticateScheme 的优先级更高）。
    //
    // 为什么不能用 OpenIddict 的 *服务端* 方案：OpenIddict 会在启动时直接抛异常
    // （"cannot be used as the default scheme handler"）。服务端 handler 只负责处理
    // /connect/* 端点 —— 它实现了 IAuthenticationRequestHandler，由认证中间件的
    // request-handler 分发环节调用，与“默认方案”无关。
    options.DefaultScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
    options.DefaultAuthenticateScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
});

// ---------------------------------------------------------------------------
// 7. 授权策略、CORS、限流、MVC、API 文档、应用与基础设施服务
// ---------------------------------------------------------------------------
builder.Services.AddAuthHubAuthorization();

// 替换掉的默认实现：让 /api/* 的 401/403 由我们统一写成 ProblemDetails。
// 用 Replace 而不是直接 AddSingleton，是为了不依赖“后注册覆盖先注册”的隐式约定。
// 背景（多方案 Challenge/Forbid 导致的状态码二次写入异常）见 AuthHubAuthorizationResultHandler。
builder.Services.Replace(ServiceDescriptor.Singleton<
    IAuthorizationMiddlewareResultHandler,
    AuthHubAuthorizationResultHandler>());

builder.Services.AddAuthHubRateLimiting(builder.Configuration);

var allowedOrigins = builder.Configuration.GetSection("AuthHub:Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AuthHubPolicy", policy =>
    {
        if (allowedOrigins.Length == 0)
        {
            // 未配置跨域来源时不放开任何源（生产默认收紧到只允许同源）
            policy.WithOrigins("https://localhost").AllowAnyHeader().AllowAnyMethod().AllowCredentials();
            return;
        }

        // 注意：绝不能用 AllowAnyOrigin() 搭配 AllowCredentials()，浏览器会直接拒绝该组合
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

builder.Services.AddTransient<ExceptionHandlingMiddleware>();
builder.Services.AddTransient<SecurityHeadersMiddleware>();

// 注意用的是 AddControllersWithViews() 而不是 AddControllers()：
// 登录 / 两步验证表单上的 [ValidateAntiForgeryToken] 由 MVC ViewFeatures 里的
// ValidateAntiforgeryTokenAuthorizationFilter 实现，该服务只在带视图的 MVC 构建器中注册。
builder.Services.AddControllersWithViews(options =>
{
    // FluentValidation 自动校验（规则集中定义在 Application 层）
    options.Filters.Add<ValidationFilter>();
})
.ConfigureApiBehaviorOptions(options =>
{
    // [ApiController] 的**请求体绑定失败**默认走框架自己的 ValidationProblemDetails：
    // 英文标题、没有 code 字段、traceId 用的是 W3C traceparent。于是同一个服务里
    // 出现两套 400 契约 —— 下游按 code 分支时会静默漏掉一半。
    // 这里换成与 FluentValidation 失败、认证失败、500 完全相同的出口，见 ApiProblemWriter。
    options.InvalidModelStateResponseFactory = ApiProblemWriter.InvalidModelStateResponse;
})
.AddJsonOptions(options =>
{
    // 枚举以字符串输出，便于前端与日志阅读
    options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// 管理后台页面（Razor Pages）。
//
// 本项目的浏览器页面分两类：
//   1) 协议页（登录 / 两步验证 / 同意授权）：OIDC 流程的一部分，由 Pages/HtmlPages.cs
//      用字符串产出 HTML，零视图依赖，便于集成测试直接断言页面内容；
//   2) 管理后台：页面多、表格与表单密集，用 Razor Pages + 共享布局
//      （Pages/Shared/_AdminLayout.cshtml）。这类页面继续手写 HTML 会产生数千行
//      字符串拼接，因此这里引入视图引擎 —— 这是对早期"零视图依赖"取舍的一次修订，
//      协议页保持原样不受影响。
//
// 安全性：Razor Pages 的 POST 处理器默认自动校验防伪令牌，
// 无需逐个挂 [ValidateAntiForgeryToken]（这正是当初选 AddControllersWithViews 的原因）。
builder.Services.AddRazorPages();

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

if (enableApiDocs)
{
    builder.Services.AddAuthHubOpenApi();
}

// 分层注册
builder.Services.AddApplication();
builder.Services.AddInfrastructure();

var app = builder.Build();

// ---------------------------------------------------------------------------
// 8. 数据库初始化（建库 / 迁移 + 种子数据，由配置开关控制）
// ---------------------------------------------------------------------------
await InitializeDatabaseAsync(app);

// ---------------------------------------------------------------------------
// 9. 中间件管道
// ---------------------------------------------------------------------------
// 最外层：异常兜底 + 安全响应头（放在最前面，保证任何分支的响应都带上）
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();

if (builder.Configuration.GetValue("AuthHub:Security:TrustForwardedHeaders", false))
{
    // 部署在 Nginx / 网关之后时启用，保证 RemoteIpAddress 与 Scheme 正确
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    });
}

if (requireHttps)
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// 静态资源（wwwroot 下的 CSS / JS / favicon）。
//
// 必须显式挂载：.NET 8 的 WebApplication 不会自动注册 StaticFile 中间件，
// 少了这一行后台会变成"没有样式 + 按钮点了没反应"（所有 .css/.js 都是 404），
// 而 /admin 本身仍然返回 200 —— 只看状态码根本发现不了，因此集成测试里专门有
// 一组用例逐一断言这些资源可取。
//
// 位置刻意放在限流与请求日志之前：静态资源是终端处理、不经过认证，
// 先命中它们就不会在每次页面加载时去消耗登录 / 令牌接口的限流额度，
// 也不会把请求日志刷满 .css/.js 的噪声。
app.UseStaticFiles(new StaticFileOptions
{
    // 默认情况下 wwwroot 的资源响应不带 Cache-Control，浏览器于是按启发式规则
    // 自行决定缓存期：改了 authhub.js 之后按 F5 也拿不到新文件，必须 Ctrl+F5，
    // 页面上看起来就像"修复没生效"。这里显式要求每次重新验证 ——
    // 内容没变回 304（开销极小），变了立刻拿到新版本。
    // 与视图侧的 asp-append-version（让 URL 随内容变化）互为双保险。
    OnPrepareResponse = staticFile =>
        staticFile.Context.Response.Headers.CacheControl = "no-cache"
});

app.UseSerilogRequestLogging();

// 重要：OpenIddict 的端点由认证中间件处理（5.x 不再需要 app.UseOpenIddict()），
// 因此限流 / CORS / 安全头等中间件必须放在 UseAuthentication() 之前，
// 否则不会作用于 /connect/token 这类由 OpenIddict 自行处理的端点。
app.UseCors("AuthHubPolicy");
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// 根路径没有任何页面，直接送到后台入口。
//
// 为什么不渲染一个落地页：本站的浏览器入口只有"管理后台"与"协议页"两类，
// 多一个页面就要多维护一份视觉；而 /admin 的授权策略已经能正确分流
// （未登录 → 302 登录页，无管理权限 → 302 /account/denied），
// 单一跳转目标比在根路径上再判断一次登录态更不容易走岔。
//
// 没有这一行时，用户打开 https://host:port/ 会得到 404（根路径没有任何路由），
// 无从判断该往哪走 —— 而这是任何人第一次访问站点时最自然的入口。
//
// 这一条不进 API 文档：它是一个浏览器跳转，不是接口。
//
// 注意别把它退回成 WithTags/WithSummary 的写法 —— 那是"用文档元数据包装一个非接口"，
// 结果是 Scalar 侧边栏里凭空多出一个「站点入口」分组，点进去只有一个会 302 的 GET /。
// 摘掉它的正确做法是 ExcludeFromDescription()，与 AccountController 上的
// [ApiExplorerSettings(IgnoreApi = true)] 一个意思（那边是控制器，这边是 Minimal API）。
app.MapGet("/", () => Results.Redirect("/admin"))
    .ExcludeFromDescription();

app.MapControllers();
app.MapRazorPages();
app.MapHealthChecks("/health");

if (enableApiDocs)
{
    // OpenAPI 文档 JSON（/openapi/v1.json）+ Scalar 交互式 UI（/scalar/v1）。
    // 文档生成器、OAuth2 方案与 Scalar 的授权配置都在 OpenApiExtensions 里。
    app.MapAuthHubApiDocs();
}

app.Logger.LogInformation(
    "AuthHub 启动完成 | 环境={Environment} | 数据库={Provider} | 密钥={KeyDescription} | 强制HTTPS={RequireHttps} | 密码流程={PasswordFlow} | API文档={ApiDocs}",
    app.Environment.EnvironmentName,
    databaseProvider,
    keyDescription,
    requireHttps,
    enablePasswordFlow,
    enableApiDocs);

app.Run();

// ---------------------------------------------------------------------------
// 本地函数
// ---------------------------------------------------------------------------

// 会话 Cookie 的未登录 / 无权限兜底。
// /api 前缀下不写任何响应体（只有 AuthHubAuthorizationResultHandler 才写），
// 其余请求（浏览器页面）走 Cookie 方案原本的 302 重定向。
static Task HandleAuthFailureAsync(RedirectContext<CookieAuthenticationOptions> context, bool isChallenge)
{
    if (!context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Redirect(context.RedirectUri);
    }

    // /api 分支刻意留空：见 ConfigureApplicationCookie 处的说明，
    // 错误响应由 AuthHubAuthorizationResultHandler 统一写出。
    return Task.CompletedTask;
}

static async Task InitializeDatabaseAsync(WebApplication app)
{
    var migrateOnStartup = app.Configuration.GetValue("AuthHub:Seeding:MigrateOnStartup", false);
    var seedingEnabled = app.Configuration.GetValue("AuthHub:Seeding:Enabled", false);

    if (!migrateOnStartup && !seedingEnabled)
    {
        return;
    }

    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;

    if (migrateOnStartup)
    {
        var dbContext = services.GetRequiredService<AuthHubDbContext>();
        var provider = app.Configuration["Database:Provider"] ?? DatabaseProviders.SqlServer;

        if (DatabaseProviders.IsSqlite(provider))
        {
            // SQLite 用于开发 / 集成测试：直接按模型建表，避免维护两套迁移
            await dbContext.Database.EnsureCreatedAsync();
        }
        else
        {
            await dbContext.Database.MigrateAsync();
        }

        app.Logger.LogInformation("数据库结构已就绪（{Provider}）。", provider);
    }

    if (seedingEnabled)
    {
        await AuthHubSeeder.SeedAsync(services, app.Configuration);
    }
}

/// <summary>
/// 供集成测试的 WebApplicationFactory&lt;Program&gt; 引用
/// （顶级语句生成的 Program 类默认是 internal，测试项目需要可访问的类型）。
/// </summary>
public partial class Program
{
}
