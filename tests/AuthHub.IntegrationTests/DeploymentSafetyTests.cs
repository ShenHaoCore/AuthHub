using System.Net;
using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace AuthHub.IntegrationTests;

/// <summary>
/// 只在容器 / 反向代理部署下才会暴露的两类配置。
///
/// 共同点是**配错了不会报错**：密钥环换个位置只是让所有人被登出、
/// 信任范围漏配只是让转发头被静默丢弃。因此这里的用例不是"验证功能"，
/// 而是把"这两种静默失效"钉成可执行的断言。
/// </summary>
public class DeploymentSafetyTests
{
    // ------------------------------------------------------------------ Data Protection

    [Fact]
    public void Data_protection_keys_should_be_persisted_to_the_configured_directory()
    {
        var keysPath = Path.Combine(Path.GetTempPath(), $"authhub-dp-{Guid.NewGuid():N}");

        try
        {
            using var factory = new AuthHubWebApplicationFactory(new Dictionary<string, string>
            {
                ["AuthHub__Security__DataProtectionKeysPath"] = keysPath
            });

            using var scope = factory.Services.CreateScope();
            var provider = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();

            // 密钥环是**按需**创建的：不真的保护一次数据，目录里什么都不会有。
            // 少了这一步，用例会因为"目录还是空的"而假失败。
            var protector = provider.CreateProtector("AuthHub.DeploymentSafetyTests");
            protector.Unprotect(protector.Protect("payload")).Should().Be("payload");

            Directory.Exists(keysPath).Should().BeTrue(
                because: "配置了密钥环目录就应当被创建（目录不存在时 Data Protection 需要一个可写位置）");

            Directory.GetFiles(keysPath, "key-*.xml").Should().NotBeEmpty(
                because: "密钥必须真的落盘 —— 否则容器重建后密钥环换代，" +
                         "所有会话 Cookie、防伪令牌与 MFA 票据会一起失效");
        }
        finally
        {
            TryDeleteDirectory(keysPath);
        }
    }

    [Fact]
    public void Two_instances_sharing_the_key_directory_should_decrypt_each_others_payloads()
    {
        // 这条用例模拟的是多实例 / 滚动更新的真实场景：
        // A 实例签发的会话 Cookie 必须能被 B 实例解开，否则用户在负载均衡后面会被反复登出。
        // 它同时钉住 SetApplicationName 的常量值 —— 默认隔离键由内容根路径派生，
        // 两个实例只要从不同路径启动就认不到同一个环，共享密钥目录也救不回来。
        var keysPath = Path.Combine(Path.GetTempPath(), $"authhub-dp-{Guid.NewGuid():N}");

        try
        {
            var overrides = new Dictionary<string, string>
            {
                ["AuthHub__Security__DataProtectionKeysPath"] = keysPath
            };

            string ciphertext;
            using (var instanceA = new AuthHubWebApplicationFactory(overrides))
            {
                var providerA = instanceA.Services
                    .GetRequiredService<IDataProtectionProvider>();

                ciphertext = providerA.CreateProtector("AuthHub.Shared").Protect("session-ticket");
            }

            using (var instanceB = new AuthHubWebApplicationFactory(overrides))
            {
                var providerB = instanceB.Services
                    .GetRequiredService<IDataProtectionProvider>();

                providerB.CreateProtector("AuthHub.Shared").Unprotect(ciphertext)
                    .Should().Be("session-ticket",
                        because: "两个实例共享密钥目录时必须能互相解密，这是滚动更新不掉线的底线");
            }
        }
        finally
        {
            TryDeleteDirectory(keysPath);
        }
    }

    // ------------------------------------------------------------------ 反向代理信任范围

    [Fact]
    public void Configured_trust_ranges_should_be_accepted_at_startup()
    {
        using var factory = new AuthHubWebApplicationFactory(new Dictionary<string, string>
        {
            ["AuthHub__Security__TrustForwardedHeaders"] = "true",
            ["AuthHub__Security__KnownNetworks__0"] = "172.16.0.0/12",
            ["AuthHub__Security__KnownProxies__0"] = "10.0.0.5"
        });

        // 能建出客户端即说明管道装配通过；反代场景下不该因为多配了信任范围而起不来
        using var client = factory.CreateClient();
        client.Should().NotBeNull();
    }

    [Fact]
    public void Malformed_trust_range_should_fail_startup_instead_of_silently_trusting_nothing()
    {
        // 关键取舍：解析失败**不**降级成"空信任范围继续跑"。
        // 那样会退化成"转发头全被忽略"这个最难排查的症状 ——
        // 审计日志里的 IP 全变成网关地址，而没有任何一处日志说明原因。
        using var factory = new AuthHubWebApplicationFactory(new Dictionary<string, string>
        {
            ["AuthHub__Security__TrustForwardedHeaders"] = "true",
            ["AuthHub__Security__KnownNetworks__0"] = "办公室网段"
        });

        var exception = Record.Exception(() => factory.CreateClient());

        exception.Should().NotBeNull(because: "非法的网段配置必须让进程起不来");
        exception!.ToString().Should().Contain("办公室网段",
            because: "错误信息要带上出错的原值，否则运维得把配置逐条试一遍");
    }

    [Fact]
    public async Task Trust_any_proxy_should_not_break_the_running_app()
    {
        // 容器编排里上游地址常常不固定，TrustAnyProxy 是给这种情况的显式出口。
        // 这里只确认它能正常启动并处理请求（安全性由网络层保证，代码侧只负责不静默）。
        using var factory = new AuthHubWebApplicationFactory(new Dictionary<string, string>
        {
            ["AuthHub__Security__TrustForwardedHeaders"] = "true",
            ["AuthHub__Security__TrustAnyProxy"] = "true"
        });

        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://localhost/")
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        request.Headers.Add("X-Forwarded-Proto", "https");

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 文件仍被占用时忽略：临时目录由操作系统回收
        }
    }
}
