using Microsoft.Extensions.Configuration;

namespace AuthHub.Infrastructure.Configuration;

/// <summary>
/// <c>AuthHub:Resources</c>（RFC 8707 资源标识白名单）的唯一读取入口。
///
/// <para><b>为什么要收成一个入口</b>：这份白名单有两个消费者 ——
/// ① OpenIddict 的 <c>RegisterResources</c>（决定哪些 resource 参数合法）；
/// ② CIMD 自动注册客户端时授予哪些 <c>rsrc:</c> 权限（决定某个客户端能请求哪些资源）。
/// 两处各读一遍配置，迟早会出现"白名单加了资源，但自动注册的客户端还是被拒"这种
/// 只在运行期才暴露的不一致。</para>
///
/// <para>内置标识 <c>authhub-api</c> 刻意**不**在这里：RFC 8707 要求 resource 是绝对 URI，
/// 而 <c>RegisterResources</c> 会逐个 <c>new Uri()</c>，传进去直接抛
/// <see cref="UriFormatException"/>。它作为 aud 由 Scope 的 Resources 带出，走的是另一条路径。</para>
/// </summary>
public static class AuthHubResourceRegistry
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "AuthHub:Resources";

    /// <summary>读取并规范化资源白名单（去空白项、去重、保持配置顺序）。</summary>
    public static string[] Load(IConfiguration configuration)
    {
        var configured = configuration.GetSection(SectionName).Get<string[]>() ?? [];

        return configured
            .Where(resource => !string.IsNullOrWhiteSpace(resource))
            .Select(resource => resource.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 读取并校验白名单。配置错误在**启动期**就抛出来，而不是等第一个 MCP 客户端接入时
    /// 才以 <c>invalid_target</c> 的形式出现 —— 那种症状很难指回配置文件。
    /// </summary>
    /// <exception cref="InvalidOperationException">存在非绝对 URI 的项。</exception>
    public static string[] LoadValidated(IConfiguration configuration)
    {
        var resources = Load(configuration);

        foreach (var resource in resources)
        {
            if (!Uri.TryCreate(resource, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException(
                    $"{SectionName} 中的 \"{resource}\" 不是绝对 URI。" +
                    "RFC 8707 要求资源标识是绝对 URI（例如 https://mcp.example.com/mcp）。");
            }
        }

        return resources;
    }
}
