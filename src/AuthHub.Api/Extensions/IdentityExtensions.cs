using AuthHub.Domain.Entities;
using AuthHub.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthHub.Api.Extensions;

/// <summary>
/// ASP.NET Core Identity，以及它在浏览器侧的三项配套：会话 Cookie、安全戳校验、防伪令牌。
///
/// 这里只负责"身份本身"。把默认认证方案从会话 Cookie 改成令牌校验的那一步**不能**并进来 ——
/// 它必须在 OpenIddict 注册之后执行，见 <see cref="AuthenticationExtensions"/>。
/// </summary>
internal static class IdentityExtensions
{
    public static IServiceCollection AddAuthHubIdentity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var requireHttps = configuration.GetValue("AuthHub:Security:RequireHttps", true);

        services.AddIdentity<ApplicationUser, ApplicationRole>(options =>
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
            options.SignIn.RequireConfirmedEmail = configuration.GetValue("AuthHub:Security:RequireConfirmedEmail", false);
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
        services.ConfigureApplicationCookie(options =>
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
        services.Configure<SecurityStampValidatorOptions>(options =>
            options.ValidationInterval = TimeSpan.FromMinutes(5));

        // 防伪令牌（登录 / 同意表单）
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "authhub.antiforgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = requireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        });

        return services;
    }

    /// <summary>
    /// 会话 Cookie 的未登录 / 无权限兜底。
    /// /api 前缀下不写任何响应体（只有 AuthHubAuthorizationResultHandler 才写），
    /// 其余请求（浏览器页面）走 Cookie 方案原本的 302 重定向。
    /// </summary>
    private static Task HandleAuthFailureAsync(RedirectContext<CookieAuthenticationOptions> context, bool isChallenge)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.Redirect(context.RedirectUri);
        }

        // /api 分支刻意留空：见上面 ConfigureApplicationCookie 处的说明，
        // 错误响应由 AuthHubAuthorizationResultHandler 统一写出。
        return Task.CompletedTask;
    }
}
