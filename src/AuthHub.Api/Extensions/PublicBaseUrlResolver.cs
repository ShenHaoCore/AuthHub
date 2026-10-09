namespace AuthHub.Api.Extensions;

/// <summary>
/// 解析邮件链接等对外绝对地址基址，避免直接信任请求 Host（密码重置链接投毒）。
/// </summary>
internal static class PublicBaseUrlResolver
{
    /// <summary>
    /// 优先级：<c>AuthHub:PublicBaseUrl</c> → <c>AuthHub:Issuer</c> → 当前请求的 Scheme+Host。
    /// </summary>
    public static string Resolve(IConfiguration configuration, HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(request);

        var configured = configuration["AuthHub:PublicBaseUrl"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = configuration["AuthHub:Issuer"];
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        return $"{request.Scheme}://{request.Host}";
    }
}
