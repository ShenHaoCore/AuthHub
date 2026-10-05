using System.Threading.RateLimiting;
using AuthHub.Domain.Constants;

namespace AuthHub.Api.Extensions;

/// <summary>
/// 速率限制策略。
///
/// 分区键统一是「来源 IP + 目标路径」，避免单个 IP 拖垮整站。
///
/// 为什么 /connect/* 的分流放在全局限流器里，而不是用命名策略 + [EnableRateLimiting]：
/// 令牌与授权端点由 OpenIddict 在**认证中间件**中处理。它们在当前配置下虽然也能被
/// MVC 路由匹配到，但一旦关闭端点 passthrough 就不再经过 MVC，
/// [EnableRateLimiting] 会静默失效。放在全局限流器里按路径判断，
/// 不依赖路由匹配，也不依赖 passthrough 开关。
/// </summary>
public static class RateLimitingExtensions
{
    /// <summary>登录 / 注册等凭证提交类端点（作用于 MVC 端点）。</summary>
    public const string LoginPolicy = "authhub-login";

    public static IServiceCollection AddAuthHubRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var tokenLimit = configuration.GetValue("AuthHub:RateLimiting:TokenRequestsPerMinute", 60);
        var loginLimit = configuration.GetValue("AuthHub:RateLimiting:LoginRequestsPerMinute", 10);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // 登录类端点：窗口更长、允许次数更少
            options.AddPolicy(LoginPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: BuildPartitionKey(context),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = loginLimit,
                        Window = TimeSpan.FromMinutes(5),
                        QueueLimit = 0
                    }));

            // 全局兜底 + 凭证端点分流
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var isCredentialEndpoint = context.Request.Path.StartsWithSegments("/connect");
                var permitLimit = isCredentialEndpoint ? tokenLimit : tokenLimit * 10;

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: BuildPartitionKey(context),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    });
            });
        });

        return services;
    }

    private static string BuildPartitionKey(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var path = context.Request.Path.Value ?? "/";
        return $"{ip}|{path}";
    }
}
