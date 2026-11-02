using OpenIddict.Validation.AspNetCore;

namespace AuthHub.Api.Extensions;

/// <summary>
/// 默认认证方案的覆盖。必须单独成一步、且排在 OpenIddict 注册之后：
/// 本方法与 <c>AddValidation().UseAspNetCore()</c> 都写 AuthenticationOptions 的默认方案，后配置者生效。
/// </summary>
internal static class AuthenticationExtensions
{
    public static IServiceCollection AddAuthHubAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(options =>
        {
            // 默认方案是「令牌校验」：受保护 API 未携带令牌时直接 401，
            // 而不是被 302 到 HTML 登录页（后者对 SPA / 脚本 / 网关都是错误行为）。
            // 必须显式写三项：AddIdentity() 会把 DefaultAuthenticateScheme / DefaultChallengeScheme
            // 都指向会话 Cookie，只改 DefaultScheme 不够（DefaultAuthenticateScheme 优先级更高）。
            // 不能用 OpenIddict 的服务端方案作默认值：它实现 IAuthenticationRequestHandler、
            // 只负责 /connect/* 端点，拿来做默认方案会在启动时直接抛异常。
            options.DefaultScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
            options.DefaultAuthenticateScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
        });

        return services;
    }
}
