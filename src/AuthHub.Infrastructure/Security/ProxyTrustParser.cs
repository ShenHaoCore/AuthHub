using System.Net;
// .NET 10 起 Microsoft.AspNetCore.HttpOverrides.IPNetwork 已弃用（ASPDEPR005），
// ForwardedHeadersOptions.KnownNetworks 改收 BCL 的 System.Net.IPNetwork。
using AspNetIpNetwork = System.Net.IPNetwork;

namespace AuthHub.Infrastructure.Security;

/// <summary>反向代理信任范围的解析。</summary>
/// <remarks>
/// - 存在的理由：ForwardedHeadersOptions 的 KnownProxies/KnownNetworks 默认只含 loopback，把 Nginx 放在
///   另一容器或另一台机器上时，X-Forwarded-For 会被 ASP.NET Core 静默丢弃（RemoteIpAddress 变成网关地址，无报错无日志）。
/// - 解析写在这里而非 Api 层，为的是能被单元测试直接钉住 —— 解析的是安全边界，写错等于把来源 IP 的可信度交给客户端伪造。
/// </remarks>
public static class ProxyTrustParser
{
    /// <summary>
    /// 解析 CIDR 网段列表，如 <c>172.16.0.0/12</c>、<c>fd00::/8</c>。刻意要求写出前缀长度，裸 IP 拒收而不默认补成 <c>/32</c> ——
    /// <c>10.0.0.0/8</c> 与 <c>10.0.0.5</c> 的授权范围相差 1600 万个地址。
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

            // System.Net.IPNetwork 是值类型，TryParse 失败时 out 参数为 default，判断返回值即可。
            if (!AspNetIpNetwork.TryParse(raw.Trim(), out var network))
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

    /// <summary>解析单个代理地址列表，如 <c>10.0.0.5</c>、<c>2001:db8::1</c>。收裸地址（不像网段要求前缀）—— 单个代理就是一台机器。</summary>
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

    /// <summary>把网段渲染成 <c>172.16.0.0/12</c> 这种可读形式，用于启动日志。</summary>
    public static string Describe(IEnumerable<AspNetIpNetwork> networks)
        => string.Join("、", networks.Select(n => n.ToString()));
}
