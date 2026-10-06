using OpenIddict.Validation.AspNetCore;

namespace AuthHub.Api.Extensions;

/// <summary>
/// 默认认证方案的覆盖。
///
/// <para>
/// 必须单独成一步、并且**排在 OpenIddict 注册之后**：本方法用
/// <c>services.Configure&lt;AuthenticationOptions&gt;</c> 写入默认方案，而 OpenIddict 的
/// <c>AddValidation().UseAspNetCore()</c> 也会配置同一组选项 —— 谁后配置谁生效。
/// 把这两步合并或调换顺序，都可能让默认方案又变回会话 Cookie。
/// </para>
/// </summary>
internal static class AuthenticationExtensions
{
    public static IServiceCollection AddAuthHubAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(options =>
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

        return services;
    }
}
