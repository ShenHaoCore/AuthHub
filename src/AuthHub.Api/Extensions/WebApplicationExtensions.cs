using AuthHub.Api.Middleware;
using AuthHub.Application.Interfaces;
using AuthHub.Application.Options;
using AuthHub.Infrastructure.Configuration;
using System.Data.Common;
using AuthHub.Infrastructure.Data;
using AuthHub.Infrastructure.Data.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;

namespace AuthHub.Api.Extensions;

/// <summary>
/// 应用启动阶段的三件事：初始化数据库、装配中间件管道、注册端点。
/// </summary>
/// <remarks>
/// - 各方法间的先后顺序是语义而不是风格，约束写在各自注释里。
/// </remarks>
internal static class WebApplicationExtensions
{
    /// <summary>
    /// 启动期自检：强制求值一次权限归属配置，让写错的配置在进程提供服务之前就失败。
    /// </summary>
    /// <remarks>
    /// - Options 校验链懒执行，自然触发点是第一个用户登录（症状："某个角色的菜单少了"）；
    ///   这里求值一次，错误落在启动日志第一屏。
    /// - 只取 Options、不取 <c>IRolePermissionMap</c>（后者读库）：本方法刻意排在数据库初始化之前；
    ///   读库预热见 <see cref="WarmUpAuthHubRolePermissions"/>。
    /// </remarks>
    public static WebApplication ValidateAuthHubStartupConfiguration(this WebApplication app)
    {
        _ = app.Services.GetRequiredService<IOptions<RolePermissionOptions>>().Value;
        return app;
    }

    /// <summary>
    /// 环境一致性护栏：确认这份配置与它所在的环境自洽（判定规则见 <see cref="EnvironmentGuard"/>）。
    /// Fail 项抛异常阻止启动，Warn 项只记日志。
    /// </summary>
    /// <remarks>
    /// - 刻意排在数据库初始化之前："连上了不该连的库"必须在任何连接动作发生前拦下来。
    /// - Fail 项在部署环境里没有"凑合能跑"的可能；Warn 项可能是正当的部署形态
    ///   （TLS 在网关卸载、数据库就在本机），只要求被看见。
    /// </remarks>
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
    /// 预热权限归属的生效表（<see cref="IRolePermissionMap.All"/> 懒加载，首次访问才读
    /// <c>RolePermissionOverrides</c> 表）：必须放在数据库初始化之后，否则失败会推迟到第一个用户登录才暴露。
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
    /// 资源白名单的「配置 → 库」种子（<see cref="ResourceSeeder"/>）刻意不受这两个开关门控：
    /// 它是安全配置 —— 不能因"生产没开 Seeding"就悄悄清空白名单，那等于把所有 resource 请求变成 invalid_target。
    /// </summary>
    public static async Task InitializeAuthHubDatabaseAsync(this WebApplication app)
    {
        var migrateOnStartup = app.Configuration.GetValue("AuthHub:Seeding:MigrateOnStartup", false);
        var seedingEnabled = app.Configuration.GetValue("AuthHub:Seeding:Enabled", false);

        if (!migrateOnStartup && !seedingEnabled)
        {
            // 迁移与播种都不在启动期做（表通常由外部流水线建好），资源种子仍要尝试。
            // 表不存在时降级为警告不阻断启动 —— 白名单为空是默认拒绝，方向安全。
            await TrySeedResourcesAsync(app);
            return;
        }

        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;

        if (migrateOnStartup)
        {
            var dbContext = services.GetRequiredService<AuthHubDbContext>();
            var provider = app.Configuration["Database:Provider"] ?? DatabaseProviders.SqlServer;

            if (DatabaseProviders.UsesEnsureCreated(provider))
            {
                // 没有专用迁移集的提供程序（SQLite / PostgreSQL）：直接按模型建表。
                // SQLite 省掉两套迁移；PG 的迁移集尚未生成，跑 Migrate 会把 SQL Server 形状的 DDL 发给它。
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

        // 结构已就绪（迁移或 EnsureCreated 之后），资源种子直接执行：
        // 配置非法时 fail-fast（进程起不来），与库表化之前 RegisterResources 的启动期行为一致。
        await ResourceSeeder.SeedAsync(
            services.GetRequiredService<AuthHubDbContext>(),
            app.Configuration,
            app.Logger);
    }

    /// <summary>
    /// 结构由外部流水线建的部署形态（MigrateOnStartup=false）：资源种子尽力而为，失败降级为警告不阻断启动。
    /// 失败方向是"白名单为空"（默认拒绝），宁可少放行，不会多放行。
    /// </summary>
    private static async Task TrySeedResourcesAsync(WebApplication app)
    {
        try
        {
            using var scope = app.Services.CreateScope();

            await ResourceSeeder.SeedAsync(
                scope.ServiceProvider.GetRequiredService<AuthHubDbContext>(),
                app.Configuration,
                app.Logger);
        }
        catch (DbException exception)
        {
            app.Logger.LogWarning(
                exception,
                "资源白名单未能从配置播种（数据库尚不可用或 RegisteredResources 表不存在）——" +
                "白名单为空意味着所有 resource 请求都会被 invalid_target 拒绝，待表就绪后重启即可恢复。");
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

        // 部署在 Nginx / 网关之后时启用，保证 RemoteIpAddress 与 Scheme 正确；
        // 信任范围配置与"只信任了什么"的日志见 ForwardedHeadersExtensions（默认只信任 loopback）。
        app.UseAuthHubForwardedHeaders();

        if (requireHttps)
        {
            app.UseHsts();
            app.UseHttpsRedirection();
        }

        // 静态资源（wwwroot 下的 CSS / JS / favicon）。必须显式挂载：WebApplication 不会自动注册
        // StaticFile 中间件，少了它 /admin 仍返回 200 但所有 .css/.js 都是 404（集成测试逐一断言可取）。
        // 位置刻意放在限流与请求日志之前：静态资源是终端处理、不经过认证，先命中可避免
        // 页面加载消耗登录 / 令牌接口的限流额度，也不把请求日志刷满 .css/.js 噪声。
        app.UseStaticFiles(new StaticFileOptions
        {
            // 默认 wwwroot 响应不带 Cache-Control，浏览器按启发式缓存 —— 改了 authhub.js
            // 按 F5 也拿不到新文件。这里要求每次重新验证：没变回 304，变了立刻拿到新版本；
            // 与视图侧 asp-append-version（URL 随内容变化）互为双保险。
            OnPrepareResponse = staticFile =>
                staticFile.Context.Response.Headers.CacheControl = "no-cache"
        });

        app.UseSerilogRequestLogging();

        // 重要：OpenIddict 的端点由认证中间件处理（5.x 不再需要 app.UseOpenIddict()），
        // 限流 / CORS / 安全头等中间件必须放在 UseAuthentication() 之前才能作用于 /connect/* 端点。
        app.UseCors("AuthHubPolicy");
        app.UseRateLimiter();

        // CIMD 动态客户端注册（默认关闭，见 AuthHub:Cimd:Enabled）。必须早于 UseAuthentication：
        // OpenIddict 的授权请求校验第一步按 client_id 查应用，URL 型 client_id 要在那之前先落库。
        app.UseMiddleware<CimdClientResolutionMiddleware>();

        app.UseAuthentication();
        app.UseAuthorization();

        return app;
    }

    /// <summary>
    /// 注册端点。
    /// </summary>
    public static WebApplication MapAuthHubEndpoints(this WebApplication app, bool enableApiDocs)
    {
        // 根路径没有任何页面，直接 302 到后台入口；/admin 的授权策略已能正确分流
        // （未登录 → 登录页，无管理权限 → /account/denied），不必再渲染一个落地页。
        // 不进 API 文档：它是浏览器跳转，不是接口。要摘掉用 ExcludeFromDescription()，
        // 别退回成 WithTags/WithSummary —— 那会在 Scalar 侧边栏凭空多出一个只含 302 GET / 的分组。
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
