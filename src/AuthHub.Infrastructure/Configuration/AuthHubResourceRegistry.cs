using Microsoft.Extensions.Configuration;

namespace AuthHub.Infrastructure.Configuration;

/// <summary>
/// <c>AuthHub:Resources</c> 配置（RFC 8707 资源标识白名单的**种子来源**）的读取入口。
///
/// <para><b>白名单已经库表化</b>（RegisteredResources 表，见 <see cref="Data.Entities.RegisteredResource"/>）：
/// 资源校验以数据库为准、运行时增删即生效，本类只负责「启动时把配置资源播种入库」这一件事，
/// 以及保留启动期的 fail-fast 校验（配置里的非法 URI 让进程起不来，而不是等到第一个
/// MCP 客户端接入时才以 <c>invalid_target</c> 的形式出现）。</para>
///
/// <para>配置语义：<b>只做加法的幂等种子</b> —— 启动时已存在的资源不覆盖（管理员在库里的
/// 修改不动），从配置里删掉的资源也**不会**从库里移除（要下线一个资源请走管理 API 或把
/// Enabled 置 false）。</para>
///
/// <para>内置标识 <c>authhub-api</c> 刻意**不**在这里：RFC 8707 要求 resource 是绝对 URI，
/// 播种时逐个 <c>new Uri()</c>，传进去直接抛 <see cref="UriFormatException"/>。
/// 它作为 aud 由 Scope 的 Resources 带出，走的是另一条路径。</para>
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

    /// <summary>
    /// 规范化一个资源标识：<c>Uri.AbsoluteUri</c> 的形式（scheme/主机小写、默认端口收敛等）。
    ///
    /// <para><b>必须入库前做</b>：OpenIddict 对 resource 参数的校验是**序数字符串比对**
    /// （请求原文 vs 登记值），旧实现里配置资源经 <c>new Uri()</c> 注册，库里的形态是
    /// AbsoluteUri。不规范化的话，配置里写 <c>https://host:443/mcp</c> 的存量部署升级后
    /// 会出现「同一资源换了写法就 invalid_target」的行为差异。</para>
    ///
    /// <para>非法输入（非绝对 URI / 带 fragment）返回 <c>null</c>，调用方自行决定报错方式。</para>
    /// </summary>
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
