using AuthHub.Domain.Entities;
using AuthHub.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthHub.Api.Extensions;

/// <summary>
/// ASP.NET Core Identity 及其在浏览器侧的三项配套：会话 Cookie、安全戳校验、防伪令牌。
/// 只负责"身份本身"；默认方案改写成令牌校验必须在 OpenIddict 注册之后，见 <see cref="AuthenticationExtensions"/>。
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
            options.Password.RequireDigit = true;
            options.Password.RequiredLength = 8;
            options.Password.RequireNonAlphanumeric = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireLowercase = true;
            options.Password.RequiredUniqueChars = 4;

            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.AllowedForNewUsers = true;

            options.User.RequireUniqueEmail = true;
            options.SignIn.RequireConfirmedEmail = configuration.GetValue("AuthHub:Security:RequireConfirmedEmail", false);
            options.SignIn.RequireConfirmedAccount = false;

            // 把 Identity 的声明类型对齐到 OIDC 标准声明：否则 Identity 产出长 URI 声明，
            // OpenIddict 无法映射成 sub / name，令牌里会缺少身份信息。
            options.ClaimsIdentity.UserIdClaimType = Claims.Subject;
            options.ClaimsIdentity.UserNameClaimType = Claims.Name;
            options.ClaimsIdentity.EmailClaimType = Claims.Email;
            options.ClaimsIdentity.RoleClaimType = Claims.Role;
        })
        .AddEntityFrameworkStores<AuthHubDbContext>()
        .AddDefaultTokenProviders();

        // 邮箱确认 / 密码重置等 DataProtection 令牌共用此时限；与邮件正文「1 小时内有效」对齐。
        services.Configure<DataProtectionTokenProviderOptions>(options =>
            options.TokenLifespan = TimeSpan.FromHours(1));

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

            // Cookie 方案默认对未登录 / 无权限 302 跳 HTML 登录页，对 /api/* 不可用（需要可编程的 401/403）。
            // 这里在 /api 下刻意什么都不做而不写 ProblemDetails：多方案策略会依次对每个方案 Challenge/Forbid，
            // Cookie 若先写出响应体，后面的 OpenIddict Validation 再设状态码就会抛
            // "The status code cannot be set, the response has already started"；
            // /api 的错误响应统一由 AuthHubAuthorizationResultHandler 在方案循环结束后写出。
            options.Events.OnRedirectToLogin = static context => HandleAuthFailureAsync(context, isChallenge: true);

            options.Events.OnRedirectToAccessDenied = static context => HandleAuthFailureAsync(context, isChallenge: false);
        });

        // 安全戳校验间隔：默认 30 分钟。缩短到 5 分钟可让“停用账号 / 变更角色”更快生效
        services.Configure<SecurityStampValidatorOptions>(options =>
            options.ValidationInterval = TimeSpan.FromMinutes(5));

        // 防伪令牌（登录 / 同意表单 + /api 的 Cookie 认证变更端点）
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "authhub.antiforgery";
            // HttpOnly=false 是刻意的：脚本类消费方要读它换请求令牌（GET /api/antiforgery/token）。
            // 该 Cookie 单独拿到并不能通过校验 —— 必须与请求令牌配对，不是会话凭证。
            options.Cookie.HttpOnly = false;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = requireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        });

        return services;
    }

    /// <summary>
    /// 会话 Cookie 的未登录 / 无权限兜底：/api 下不写任何响应体（由 AuthHubAuthorizationResultHandler 统一写出），
    /// 其余请求（浏览器页面）走 Cookie 方案原本的 302 重定向。
    /// </summary>
    private static Task HandleAuthFailureAsync(RedirectContext<CookieAuthenticationOptions> context, bool isChallenge)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.Redirect(context.RedirectUri);
        }

        // /api 分支刻意留空：错误响应由 AuthHubAuthorizationResultHandler 统一写出（见上面 ConfigureApplicationCookie 处的说明）。
        return Task.CompletedTask;
    }
}
