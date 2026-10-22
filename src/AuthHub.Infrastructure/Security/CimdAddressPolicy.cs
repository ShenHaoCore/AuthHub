using System.Net;
using System.Net.Sockets;

namespace AuthHub.Infrastructure.Security;

/// <summary>
/// CIMD 元数据拉取的**出站地址策略** —— 本仓库唯一一处"由匿名请求驱动出站连接"的地方，
/// 因此把判断逻辑抽成纯函数，逐条钉死在单元测试里。
///
/// <para><b>威胁模型</b>：攻击者把 <c>client_id</c> 填成 <c>https://169.254.169.254/latest/meta-data/</c>
/// 或 <c>https://internal.corp/…</c>，诱导授权服务器替他请求内网地址，再根据响应差异
/// （状态码、耗时、错误信息）推断内网拓扑。仅校验 URL 字符串不够 ——
/// 一个公网域名可以解析到内网 IP（DNS rebinding），所以真正生效的检查必须发生在
/// **连接建立时**，针对已解析出的 IP 地址（见 <c>CimdMetadataFetcher.ConnectPublicOnlyAsync</c>）。</para>
///
/// <para><b>为什么"要么全公网、要么全拒"</b>：DNS 返回多个地址时，只挑公网那个虽然也能防住
/// 直接访问内网，但会让"这个域名有时能通、有时不通"变成常态，且难以解释。全拒的语义清楚：
/// 元数据文档必须托管在一个干净的、纯公网的地址上。</para>
/// </summary>
public static class CimdAddressPolicy
{
    /// <summary>
    /// 元数据 URL 允许使用的端口。CIMD 面向生产客户端，没有理由走非标准端口；
    /// 收窄到 443 让"出站连接的目标"在日志里一眼可辨。
    /// </summary>
    public const int AllowedPort = 443;

    /// <summary>
    /// 判断一个已解析的 IP 是否属于"可从公网访问"的地址。
    /// 保留地址、私网、回环、链路本地、组播、文档/测试网段一律为 <c>false</c>。
    /// </summary>
    public static bool IsPublic(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        // ::ffff:10.0.0.1 这类 IPv4 映射地址若直接按 IPv6 判断会被漏掉，先还原
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicIPv4(bytes),
            AddressFamily.InterNetworkV6 => IsPublicIPv6(bytes),
            // Unix domain socket 等其它族不在考虑范围内
            _ => false
        };
    }

    /// <summary>
    /// 判断元数据 URL 本身是否可接受：必须是无凭据、无片段、标准端口的 HTTPS 绝对地址。
    ///
    /// <para>片段会被浏览器/HTTP 客户端剥离后再请求，导致"校验的字符串"与"实际请求的地址"
    /// 不是同一个东西；凭据（<c>https://user@host</c>）是经典的 URL 混淆手法。两者都直接拒。</para>
    /// </summary>
    public static bool IsAllowedMetadataUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return false;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(uri.Host)
               && string.IsNullOrEmpty(uri.UserInfo)
               && string.IsNullOrEmpty(uri.Fragment)
               && uri.Port == AllowedPort;
    }

    /// <summary>
    /// 回环地址判定（用于 native 客户端的回调地址校验）。
    ///
    /// <para>RFC 8252 §7.3 允许 native 应用使用任意端口的 loopback 回调，
    /// 因此这里只看"是不是本机"，不看端口。</para>
    /// </summary>
    public static bool IsLoopbackHost(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.IsLoopback
        && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    private static bool IsPublicIPv4(byte[] bytes) =>
        !(bytes[0] is 0 or 10 or 127                        // 0.0.0.0/8、10/8、127/8
          || (bytes[0] == 100 && (bytes[1] & 0xC0) == 64)   // 100.64/10 运营商级 NAT
          || (bytes[0] == 169 && bytes[1] == 254)           // 169.254/16 链路本地（云元数据服务就在这里）
          || (bytes[0] == 172 && (bytes[1] & 0xF0) == 16)   // 172.16/12
          || (bytes[0] == 192 && bytes[1] == 168)           // 192.168/16
          || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2)  // 192.0.0/24 协议分配、192.0.2/24 文档网段
          || (bytes[0] == 198 && (bytes[1] & 0xFE) == 18)   // 198.18/15 基准测试网段
          || (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)  // 198.51.100/24
          || (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)   // 203.0.113/24
          || (bytes[0] & 0xE0) == 224                       // 224/4 组播 + 240/4 保留（含 255.255.255.255 广播）
          );

    private static bool IsPublicIPv6(byte[] bytes) =>
        !(bytes.All(static value => value == 0)                  // :: 未指定
          || (bytes[0] & 0xFE) == 0xFC                            // fc00::/7 唯一本地地址
          || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)      // fe80::/10 链路本地
          || bytes[0] == 0xFF                                     // ff00::/8 组播
          || (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8)  // 2001:db8::/32
          || (bytes[0] == 0x20 && bytes[1] == 0x02)               // 2002::/16 6to4（可封装私网）
          || (bytes[0] == 0x01 && bytes[1] == 0x00)               // 100::/64 丢弃前缀
          || (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B)  // 64:ff9b::/96 NAT64
          );
}
