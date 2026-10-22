using System.Net;
using AuthHub.Infrastructure.Security;
using FluentAssertions;

namespace AuthHub.UnitTests.Infrastructure;

/// <summary>
/// CIMD 元数据拉取的地址策略 —— 本仓库唯一"由匿名请求驱动出站连接"的入口。
///
/// 这批用例守的是 SSRF 边界：漏掉任何一个地址段，AuthHub 就变成一把能探测内网的梯子。
/// 因此断言的重点在**边界值**（每个保留网段的首尾地址），而不是挑几个好看的值。
/// </summary>
public class CimdAddressPolicyTests
{
    // ------------------------------------------------------------------ 必须拒绝的地址

    [Theory]
    // IPv4 私网与保留段
    [InlineData("0.0.0.0")]           // 0.0.0.0/8
    [InlineData("10.0.0.1")]          // 10/8
    [InlineData("100.64.0.1")]        // 100.64/10 运营商级 NAT
    [InlineData("100.127.255.254")]   // 100.64/10 的上界
    [InlineData("127.0.0.1")]         // 环回
    [InlineData("169.254.169.254")]   // 链路本地 —— 云元数据服务的经典目标
    [InlineData("172.16.0.1")]        // 172.16/12 下界
    [InlineData("172.31.255.254")]    // 172.16/12 上界
    [InlineData("192.0.0.1")]         // 协议分配
    [InlineData("192.0.2.1")]         // TEST-NET-1
    [InlineData("192.168.1.1")]       // 192.168/16
    [InlineData("198.18.0.1")]        // 基准测试网段
    [InlineData("198.19.255.254")]    // 198.18/15 上界
    [InlineData("198.51.100.1")]      // TEST-NET-2
    [InlineData("203.0.113.1")]       // TEST-NET-3
    [InlineData("224.0.0.1")]         // 组播
    [InlineData("240.0.0.1")]         // 保留
    [InlineData("255.255.255.255")]   // 广播
    // IPv6
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fc00::1")]           // 唯一本地地址下界
    [InlineData("fdff::1")]           // fc00::/7 上界
    [InlineData("fe80::1")]           // 链路本地
    [InlineData("ff02::1")]           // 组播
    [InlineData("2001:db8::1")]       // 文档网段
    [InlineData("2002:a00:1::1")]     // 6to4（可封装 10.0.0.1）
    [InlineData("64:ff9b::a00:1")]    // NAT64（可封装 10.0.0.1）
    // IPv4 映射地址必须还原后再判断，不能因为"看起来是 IPv6"就放过
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    public void Non_public_addresses_should_be_rejected(string address)
    {
        CimdAddressPolicy.IsPublic(IPAddress.Parse(address)).Should().BeFalse(
            $"{address} 属于私网 / 保留 / 链路本地地址，出站连接必须拒绝");
    }

    // ------------------------------------------------------------------ 允许的地址

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("172.15.255.255")]    // 紧邻 172.16/12 下界之外
    [InlineData("172.32.0.1")]        // 紧邻 172.16/12 上界之外
    [InlineData("100.63.255.255")]    // 紧邻 100.64/10 下界之外
    [InlineData("100.128.0.1")]       // 紧邻 100.64/10 上界之外
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    public void Public_addresses_should_be_accepted(string address)
    {
        CimdAddressPolicy.IsPublic(IPAddress.Parse(address)).Should().BeTrue();
    }

    [Fact]
    public void Null_address_should_be_rejected()
    {
        CimdAddressPolicy.IsPublic(null).Should().BeFalse();
    }

    // ------------------------------------------------------------------ 元数据 URL 的形态

    [Theory]
    [InlineData("https://client.example.com/oauth/client-metadata.json", true)]
    [InlineData("https://client.example.com:443/meta.json", true)]        // 显式写默认端口等价
    [InlineData("https://client.example.com:8443/meta.json", false)]      // 非 443：收窄出站目标
    [InlineData("http://client.example.com/meta.json", false)]            // 明文 http
    [InlineData("https://user:pass@client.example.com/meta.json", false)] // 凭据（URL 混淆）
    [InlineData("https://client.example.com/meta.json#frag", false)]      // 片段不会随请求发出
    [InlineData("file:///etc/passwd", false)]
    public void Metadata_uri_shape_should_be_validated(string uri, bool expected)
    {
        CimdAddressPolicy.IsAllowedMetadataUri(new Uri(uri)).Should().Be(expected);
    }

    [Fact]
    public void Relative_uri_should_be_rejected()
    {
        CimdAddressPolicy.IsAllowedMetadataUri(new Uri("/meta.json", UriKind.Relative)).Should().BeFalse();
    }

    [Fact]
    public void Null_uri_should_be_rejected()
    {
        CimdAddressPolicy.IsAllowedMetadataUri(null).Should().BeFalse();
    }
}
