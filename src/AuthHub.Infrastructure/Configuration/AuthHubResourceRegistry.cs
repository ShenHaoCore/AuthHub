using Microsoft.Extensions.Configuration;

namespace AuthHub.Infrastructure.Configuration;

/// <summary><c>AuthHub:Resources</c> 配置（RFC 8707 资源标识白名单的种子来源）的读取入口。</summary>
/// <remarks>
/// - 白名单已库表化（<c>AuthHub.Domain.Entities.RegisteredResource</c> 实体）：校验以数据库为准、运行时增删即生效；
///   本类只负责启动时把配置资源播种入库，并保留启动期 fail-fast 校验（非法 URI 让进程起不来，而不是等第一个
///   MCP 客户端接入时才以 <c>invalid_target</c> 出现）。
/// - 配置语义是只做加法的幂等种子：已存在的不覆盖，从配置删掉的也不会从库里移除（下线走管理 API 或 Enabled=false）。
/// - 内置标识 authhub-api 刻意不在这里：RFC 8707 要求绝对 URI，播种时逐个 <c>new Uri()</c> 会直接抛
///   <see cref="UriFormatException"/>；它作为 aud 由 Scope 的 Resources 带出，走另一条路径。
/// </remarks>
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

    /// <summary>读取并校验白名单：配置错误在启动期就抛出，而不是等第一个 MCP 客户端接入时才以 <c>invalid_target</c> 出现。</summary>
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

    /// <summary>规范化一个资源标识为 <c>Uri.AbsoluteUri</c> 的形式（scheme/主机小写、默认端口收敛等）；非法输入返回 <c>null</c>。</summary>
    /// <remarks>
    /// - 必须入库前做：OpenIddict 对 resource 的校验是序数字符串比对（请求原文 vs 登记值），不规范化的话，
    ///   配置里写 <c>https://host:443/mcp</c> 的存量部署升级后会出现「同一资源换了写法就 invalid_target」。
    /// - 非法输入（非绝对 URI / 带 fragment）返回 <c>null</c>，调用方自行决定报错方式。
    /// </remarks>
    public static string? NormalizeResource(string resource)
    {
        if (string.IsNullOrWhiteSpace(resource) ||
            !Uri.TryCreate(resource.Trim(), UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        return uri.AbsoluteUri;
    }
}
