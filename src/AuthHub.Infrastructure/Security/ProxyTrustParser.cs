using System.Net;
using AspNetIpNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace AuthHub.Infrastructure.Security;

/// <summary>
/// 反向代理信任范围的解析。
///
/// <para><b>存在的理由</b>：<c>ForwardedHeadersOptions</c> 的 <c>KnownProxies</c> /
/// <c>KnownNetworks</c> 默认**只含 loopback**（<c>127.0.0.1/8</c> 与 <c>::1</c>）。
/// 也就是说，把 Nginx 放在另一个容器或另一台机器上时，它发来的
/// <c>X-Forwarded-For</c> / <c>X-Forwarded-Proto</c> 会被 ASP.NET Core **静默丢弃** ——
/// 不报错、不打日志，只是 <c>RemoteIpAddress</c> 变成网关地址。
/// 本项目为此提供可配置的信任范围，把"静默"变成"显式声明"。</para>
///
/// <para><b>为什么解析写在这里而不是 Api 层</b>：这样它可以被单元测试直接覆盖。
/// 解析的是安全边界，写错等于把来源 IP 的可信度交给客户端伪造，值得钉住每条边界行为。</para>
/// </summary>
public static class ProxyTrustParser
{
    /// <summary>
    /// 解析 CIDR 网段列表，如 <c>172.16.0.0/12</c>、<c>fd00::/8</c>。
    ///
    /// 刻意**要求写出前缀长度**：<c>10.0.0.5</c> 这种裸 IP 会被拒绝而不是默认补成 <c>/32</c>。
    /// 在"信任谁能伪造转发头"这件事上，让用户明确写出范围比替她猜一个更安全 ——
    /// 尤其 <c>10.0.0.0/8</c> 与 <c>10.0.0.5</c> 的授权范围相差 1600 万个地址。
    /// </summary>
    /// <exception cref="FormatException">某个值不是合法 CIDR 时抛出，消息里带上原值。</exception>
    public static IReadOnlyList<AspNetIpNetwork> ParseNetworks(IEnumerable<string>? values)
    {
        var networks = new List<AspNetIpNetwork>();

        foreach (var raw in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                // 配置节里留空行是常见的手滑，跳过而不是让进程起不来
                continue;
            }

            // 注意：这个 TryParse 的 out 参数没有 [NotNullWhen(true)] 标注，
            // 因此必须显式判空，否则可空性分析会在后面报 CS8602。
            if (!AspNetIpNetwork.TryParse(raw.Trim(), out var network) || network is null)
            {
                throw new FormatException(
                    $"无法把 \"{raw}\" 解析成 IP 网段。请使用 CIDR 写法并写出前缀长度，" +
                    "例如 172.16.0.0/12、10.0.0.0/8、::1/128、fd00::/8。" +
                    "（裸 IP 会被拒绝；前缀长度也不能超过地址位宽。）");
            }

            networks.Add(network);
        }

        return networks;
    }

    /// <summary>
    /// 解析单个代理地址列表，如 <c>10.0.0.5</c>、<c>2001:db8::1</c>。
    /// 这里收的是**裸地址**（不像网段那样要求前缀）—— 单个代理就是一台机器。
    /// </summary>
    /// <exception cref="FormatException">某个值不是合法 IP 地址时抛出，消息里带上原值。</exception>
    public static IReadOnlyList<IPAddress> ParseAddresses(IEnumerable<string>? values)
    {
        var addresses = new List<IPAddress>();

        foreach (var raw in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (!IPAddress.TryParse(raw.Trim(), out var address) || address is null)
            {
                throw new FormatException(
                    $"无法把 \"{raw}\" 解析成 IP 地址。请填写网关 / 反向代理的地址，" +
                    "例如 10.0.0.5、2001:db8::1。若想授权的是一整段地址，请改用 KnownNetworks 的 CIDR 写法。");
            }

            addresses.Add(address);
        }

        return addresses;
    }

    /// <summary>
    /// 把网段渲染成 <c>172.16.0.0/12</c> 这种可读形式，用于启动日志。
    ///
    /// 为什么不直接用 <c>ToString()</c>：<c>Microsoft.AspNetCore.HttpOverrides.IPNetwork</c>
    /// 没有重写 <c>ToString()</c>，直接打出来只会得到类型全名，日志里毫无信息量。
    /// </summary>
    public static string Describe(IEnumerable<AspNetIpNetwork> networks)
        => string.Join("、", networks.Select(n => $"{n.Prefix}/{n.PrefixLength}"));
}
