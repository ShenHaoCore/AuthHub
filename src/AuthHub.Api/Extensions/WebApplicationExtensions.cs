using AuthHub.Api.Middleware;
using AuthHub.Application.Interfaces;
using AuthHub.Application.Options;
using AuthHub.Infrastructure.Configuration;
using AuthHub.Infrastructure.Data;
using AuthHub.Infrastructure.Data.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;

namespace AuthHub.Api.Extensions;

/// <summary>
/// 应用启动阶段的三件事：初始化数据库、装配中间件管道、注册端点。
///
/// 顺序在这里是**语义**而不是风格 —— 每个方法内部的注释写明了各自的约束。
/// </summary>
internal static class WebApplicationExtensions
{
    /// <summary>
    /// 启动期自检：**强制求值**一次权限归属配置，让写错的配置在进程正式提供服务之前就失败。
    ///
    /// 为什么要主动求值：Options 是懒加载的，挂在它校验链上的 RolePermissionOptionsValidator
    /// 要等到有人第一次取 <c>IOptions&lt;RolePermissionOptions&gt;.Value</c> 才会执行 ——
    /// 而自然发生的那次通常在第一个用户登录时。于是"配置写错"的症状会变成"某个角色的菜单少了"，
    /// 排查方向很难第一时间指回配置文件。在这里求值一次，错误就落在启动日志的第一屏。
    ///
    /// 注意这里**只取 Options，不取 IRolePermissionMap** —— 后者会去读数据库里的覆盖表，
    /// 而本方法刻意安排在数据库初始化之前（配置错误应该比数据库问题更早、更清楚地报出来）。
    /// 需要读库的那部分预热在 <see cref="WarmUpAuthHubRolePermissions"/>。
    /// </summary>
    public static WebApplication ValidateAuthHubStartupConfiguration(this WebApplication app)
    {
        _ = app.Services.GetRequiredService<IOptions<RolePermissionOptions>>().Value;
        return app;
    }

    /// <summary>
    /// 环境一致性护栏：确认这份配置与它所在的环境自洽（判定规则见
    /// <see cref="EnvironmentGuard"/>）。
    ///
    /// 安排在这里的两个理由：
    /// <list type="number">
    /// <item>在数据库初始化**之前** —— 环境串用最典型的表现就是"连上了不该连的库"，
    /// 必须在任何连接动作发生前拦下来；</item>
    /// <item>在配置自检**之后** —— 权限配置写错属于"同一个环境的配置本身有问题"，
    /// 与环境无关，先报它更接近问题的边界。</item>
    /// </list>
    ///
    /// Fail 项直接抛异常让进程起不来：这些取值在部署环境里没有"凑合能跑"的可能，
    /// 放它起来只会换来一个更难定位的故障。Warn 项写日志 —— 它们可能是正当的部署形态
    /// （TLS 在网关卸载、数据库就在本机），所以只要求"被看见"。
    /// </summary>
    public static WebApplication ValidateAuthHubEnvironment(this WebApplication app)
    {
        var findings = EnvironmentGuard.Evaluate(app.Configuration, app.Environment.EnvironmentName);
        if (findings.Count == 0)
        {
            return app;
        }

        foreach (var warning in findings.Where(finding => finding.Severity == GuardSeverity.Warning))
        {
            app.Logger.LogWarning(
                "环境一致性警告 | {ConfigKey} | {Message}",
                warning.Key,
                warning.Message);
        }

        var failures = findings
            .Where(finding => finding.Severity == GuardSeverity.Failure)
            .ToList();

        if (failures.Count == 0)
        {
            return app;
        }

        var detail = string.Join(
            System.Environment.NewLine,
            failures.Select(finding => $"  · {finding.Key}{System.Environment.NewLine}    {finding.Message}"));

        throw new InvalidOperationException(
            $"环境={app.Environment.EnvironmentName} 的配置自检未通过，已阻止启动：" +
            $"{System.Environment.NewLine}{detail}{System.Environment.NewLine}" +
            "若这些取值确实是有意为之，把 AuthHub:Security:GuardMode 设为 Warn（降级为日志）或 Off（跳过检查）。");
    }

    /// <summary>
    /// 预热权限归属的生效表（<see cref="IRolePermissionMap.All"/> 是懒加载的，
    /// 首次访问才会去读 <c>RolePermissionOverrides</c> 表）。
    ///
    /// 必须放在数据库初始化**之后**调用：表还没建出来的话，这里会立刻抛，
    /// 而放在登录路径上懒加载就会变成"第一个登录的人拿到 500"。
    /// 顺带把生效结果打进启动日志 —— 排查授权问题时这是第一手依据。
    /// </summary>
    public static WebApplication WarmUpAuthHubRolePermissions(this WebApplication app)
    {
        var map = app.Services.GetRequiredService<IRolePermissionMap>();

        app.Logger.LogInformation(
            "角色权限归属已就绪 | 角色数={RoleCount} | 其中有自定义覆盖={CustomizedCount} 个 | 覆盖来自 {Source}",
            map.All.Count,
            map.All.Keys.Count(map.IsCustomized),
            "数据库（RolePermissionOverrides），优先级高于配置 AuthHub:RolePermissions");

        return app;
    }

    /// <summary>
    /// 建库 / 迁移 + 种子数据，由 <c>AuthHub:Seeding</c> 下的开关控制，默认全关。
    /// </summary>
    public static async Task InitializeAuthHubDatabaseAsync(this WebApplication app)
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
            // 把"是不是生产"传进去：播种内容含演示账号与源码里公开的演示密钥，
            // 生产环境必须由 AuthHub:Seeding:AllowInProduction=true 再确认一次（见 AuthHubSeeder.Decide）。
            await AuthHubSeeder.SeedAsync(services, app.Configuration, app.Environment.IsProduction());
        }
    }

    /// <summary>
    /// 装配中间件管道。
    /// </summary>
    public static WebApplication UseAuthHubPipeline(this WebApplication app, bool requireHttps)
    {
        // 最外层：异常兜底 + 安全响应头（放在最前面，保证任何分支的响应都带上）
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        app.UseMiddleware<SecurityHeadersMiddleware>();

        // 部署在 Nginx / 网关之后时启用，保证 RemoteIpAddress 与 Scheme 正确。
        // 信任范围的配置与"只信任了什么"的日志都在 ForwardedHeadersExtensions 里 ——
        // 默认只信任 loopback 这件事必须被说出来，否则转发头会被静默忽略。
        app.UseAuthHubForwardedHeaders();

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

        return app;
    }

    /// <summary>
    /// 注册端点。
    /// </summary>
    public static WebApplication MapAuthHubEndpoints(this WebApplication app, bool enableApiDocs)
    {
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

        return app;
    }
}
