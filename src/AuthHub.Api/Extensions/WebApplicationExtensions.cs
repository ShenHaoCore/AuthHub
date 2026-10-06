using AuthHub.Api.Middleware;
using AuthHub.Infrastructure.Data;
using AuthHub.Infrastructure.Data.Seed;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
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
            await AuthHubSeeder.SeedAsync(services, app.Configuration);
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

        if (app.Configuration.GetValue("AuthHub:Security:TrustForwardedHeaders", false))
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
