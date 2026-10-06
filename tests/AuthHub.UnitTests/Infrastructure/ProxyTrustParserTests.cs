using System.Net;
using AuthHub.Infrastructure.Security;
using FluentAssertions;

namespace AuthHub.UnitTests.Infrastructure;

/// <summary>
/// 反向代理信任范围的解析。
///
/// 这批用例守的是**安全边界**：信任范围写错的后果是客户端可以伪造来源 IP，
/// 进而污染审计日志、并让按 IP 分区的限流形同虚设。因此这里对"边界值"的断言比对正常值更多。
/// </summary>
public class ProxyTrustParserTests
{
    [Fact]
    public void Valid_ipv4_cidr_should_be_parsed_with_its_prefix()
    {
        var networks = ProxyTrustParser.ParseNetworks(new[] { "172.16.0.0/12" });

        networks.Should().HaveCount(1);
        networks[0].Prefix.Should().Be(IPAddress.Parse("172.16.0.0"));
        networks[0].PrefixLength.Should().Be(12);
    }

    [Fact]
    public void Valid_ipv6_cidr_should_be_parsed_too()
    {
        // 容器网络在 IPv6 或双栈环境下同样常见，不能只支持 IPv4
        var networks = ProxyTrustParser.ParseNetworks(new[] { "fd00::/8" });

        networks.Should().HaveCount(1);
        networks[0].PrefixLength.Should().Be(8);
    }

    [Fact]
    public void Bare_ip_without_prefix_should_be_rejected()
    {
        // 这是刻意不做的"便利"：替用户把 10.0.0.5 猜成 /32 看着贴心，
        // 但同一个输入手滑成 10.0.0.0 就变成 /32 还是 /8 全靠猜，而两者授权范围天差地别。
        var act = () => ProxyTrustParser.ParseNetworks(new[] { "10.0.0.5" });

        act.Should().Throw<FormatException>()
            .WithMessage("*10.0.0.5*")
            .WithMessage("*CIDR*");
    }

    [Fact]
    public void Prefix_longer_than_the_address_width_should_be_rejected()
    {
        var act = () => ProxyTrustParser.ParseNetworks(new[] { "10.0.0.0/33" });

        act.Should().Throw<FormatException>().WithMessage("*10.0.0.0/33*");
    }

    [Fact]
    public void Garbage_should_be_rejected_with_the_offending_value_in_the_message()
    {
        // 运维配错了要能一眼看到是哪个值错了 —— 配置里通常一次列好几段
        var act = () => ProxyTrustParser.ParseNetworks(new[] { "172.16.0.0/12", "办公室网段" });

        act.Should().Throw<FormatException>().WithMessage("*办公室网段*");
    }

    [Fact]
    public void Blank_entries_should_be_skipped_instead_of_failing_startup()
    {
        // 环境变量里留空行是常见手滑（`KnownNetworks__1=`），
        // 这属于"没有配置"而不是"配置错了"，不该让进程起不来
        var networks = ProxyTrustParser.ParseNetworks(new[] { "10.0.0.0/8", "", "   ", "192.168.0.0/16" });

        networks.Should().HaveCount(2);
    }

    [Fact]
    public void Null_input_should_yield_an_empty_list()
    {
        ProxyTrustParser.ParseNetworks(null).Should().BeEmpty();
        ProxyTrustParser.ParseAddresses(null).Should().BeEmpty();
    }

    [Fact]
    public void Order_should_be_preserved_so_the_log_matches_the_configuration()
    {
        var networks = ProxyTrustParser.ParseNetworks(new[] { "10.0.0.0/8", "172.16.0.0/12" });

        ProxyTrustParser.Describe(networks).Should().Be("10.0.0.0/8、172.16.0.0/12");
    }

    [Fact]
    public void Describe_should_render_cidr_not_the_type_name()
    {
        // Microsoft.AspNetCore.HttpOverrides.IPNetwork 是**引用类型且没有重写 ToString()**，
        // 直接把它插进日志模板只会打出类型全名，那一行日志就等于没有信息。
        // 这条用例专门钉住"启动日志里能读懂到底信任了哪些网段"。
        var described = ProxyTrustParser.Describe(ProxyTrustParser.ParseNetworks(new[] { "172.16.0.0/12" }));

        described.Should().Be("172.16.0.0/12");
        described.Should().NotContain("IPNetwork");
    }

    [Fact]
    public void Valid_proxy_addresses_should_be_parsed_without_requiring_a_prefix()
    {
        // 单个代理就是一台机器，收裸地址（与网段的规则刻意不同）
        var addresses = ProxyTrustParser.ParseAddresses(new[] { "10.0.0.5", "2001:db8::1" });

        addresses.Should().Equal(IPAddress.Parse("10.0.0.5"), IPAddress.Parse("2001:db8::1"));
    }

    [Fact]
    public void Invalid_proxy_address_should_be_rejected_with_a_hint_about_networks()
    {
        // 用户很可能是想写一整段，错误消息要把人引到 KnownNetworks 上去
        var act = () => ProxyTrustParser.ParseAddresses(new[] { "10.0.0.0/8" });

        act.Should().Throw<FormatException>()
            .WithMessage("*10.0.0.0/8*")
            .WithMessage("*KnownNetworks*");
    }
}
