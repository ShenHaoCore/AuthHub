namespace AuthHub.Api.Extensions;

/// <summary>
/// 跨域策略。
///
/// 只注册一个命名策略，由 <see cref="WebApplicationExtensions.UseAuthHubPipeline"/> 按名启用。
/// 用命名策略而不是全局默认，是因为 CORS 中间件要显式排在认证之前（见管道处的说明）。
/// </summary>
internal static class CorsExtensions
{
    public static IServiceCollection AddAuthHubCors(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var allowedOrigins = configuration.GetSection("AuthHub:Cors:AllowedOrigins").Get<string[]>()
            ?? Array.Empty<string>();

        services.AddCors(options =>
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

        return services;
    }
}
