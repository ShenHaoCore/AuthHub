using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using StackExchange.Redis;

namespace AuthHub.IntegrationTests;

/// <summary>
/// Redis 令牌条目缓存（AuthHub:Redis）的端到端行为：开启缓存后，"撤销立刻生效"的语义一分不丢
/// —— 撤销用例都断言"撤销后的下一次校验立刻 401"而不是等 TTL，这正是缓存层存在的意义：降本不降语义。
/// </summary>
/// <remarks>
/// - 令牌级撤销（管理器 TryRevokeAsync，底层 UpdateAsync → 缓存精确删键）。
/// - 批量撤销（RevokeBySubjectAsync，缓存层递增全局版本号，旧条目立刻判过期）。
/// - 关闭开关时行为与引入缓存之前完全一致。
/// - 用真实 Redis 而不是假实现：版本号比对、管线化读、键删除这些语义用假 IDatabase 测不出来；
///   而部署形态（WSL 本机 / CI 服务容器）里 Redis 都在 localhost:6379。连不上就跳过 —— 缓存是可选组件。
/// - 隔离：每个工厂用独立的 KeyPrefix（GUID），用例只碰自己的键，绝不 flush 开发环境里的 Redis。
/// </remarks>
public class RedisTokenCacheTests
{
    private const string M2mClientId = AuthHubWebApplicationFactory.M2mClientId;
    private const string M2mClientSecret = AuthHubWebApplicationFactory.M2mClientSecret;
    private const string ApiPath = "/api/profile";

    // ------------------------------------------------------------------ ① 撤销立刻生效（精确删键）

    [Fact]
    public async Task Revoked_token_should_fail_validation_immediately()
    {
        var host = CreateEnabledHost();
        if (host is null)
        {
            return;
        }

        var (factory, client) = host.Value;
        using var _ = factory;
        using var __ = client;

        var token = await GetClientCredentialsTokenAsync(client);

        // 第一次校验：未命中 → 查库 → 条目写入缓存；顺带证明缓存真的被填充了
        var first = await CallApiAsync(client, token);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        CachedKeys(factory).Should().NotBeEmpty("首次校验应把令牌条目写进 Redis");

        // 生产上令牌级撤销（管理后台 / api/tokens）经管理器 TryRevokeAsync → UpdateAsync
        // → 存储装饰器精确删键。这里走同一条链（/connect/revoke 端点未启用，与缓存无关）。
        using (var scope = factory.Services.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            var victims = new List<object>();
            await foreach (var entity in manager.FindBySubjectAsync(M2mClientId))
            {
                victims.Add(entity!);
            }

            victims.Should().ContainSingle("测试里 m2m 客户端只签发了一个令牌");
            (await manager.TryRevokeAsync(victims[0])).Should().BeTrue();
        }

        var second = await CallApiAsync(client, token);
        second.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "撤销经管理器 UpdateAsync → 缓存精确删键，下一次校验必须回库看到 Revoked");
    }

    // ------------------------------------------------------------------ ② 批量撤销立刻生效（版本号）

    [Fact]
    public async Task Bulk_revocation_should_invalidate_cached_entries_immediately()
    {
        var host = CreateEnabledHost();
        if (host is null)
        {
            return;
        }

        var (factory, client) = host.Value;
        using var _ = factory;
        using var __ = client;

        var token = await GetClientCredentialsTokenAsync(client);
        (await CallApiAsync(client, token)).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = factory.Services.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            (await manager.RevokeBySubjectAsync(M2mClientId)).Should().BeGreaterThan(0);
        }

        var second = await CallApiAsync(client, token);
        second.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "批量撤销无法逐键失效，版本号递增必须让全部旧条目立刻判为过期");
    }

    // ------------------------------------------------------------------ ③ 关闭时回归

    [Fact]
    public async Task Token_validation_should_work_without_redis()
    {
        using var factory = CreateFactory(enabled: false);
        using var client = CreateClient(factory);

        var token = await GetClientCredentialsTokenAsync(client);

        (await CallApiAsync(client, token)).StatusCode.Should().Be(HttpStatusCode.OK,
            "关闭缓存后走原有查库路径，令牌校验行为不变");
        factory.Services.GetService<IConnectionMultiplexer>().Should().BeNull(
            "关闭开关时连 Redis 连接都不该创建 —— 这是「默认关闭 = 零新依赖」的承诺");
    }

    // ------------------------------------------------------------------ 工具方法

    /// <summary>
    /// Redis 不可达时返回 null，调用方提前结束 —— 本机没有 Redis 的开发环境不至于红一片
    /// （xunit 2.9 没有顺手的运行时 skip API，"提前返回"比"硬红"友好；CI 的服务容器保证始终真实执行）。
    /// </summary>
    private static (AuthHubWebApplicationFactory Factory, HttpClient Client)? CreateEnabledHost()
    {
        if (!TryPingRedis())
        {
            return null;
        }

        var factory = CreateFactory(enabled: true);
        return (factory, CreateClient(factory));
    }

    /// <summary>各用例独立前缀，只碰自己的键；连不上 Redis 直接跳过（缓存是可选组件）。</summary>
    private static AuthHubWebApplicationFactory CreateFactory(bool enabled) =>
        new(new Dictionary<string, string>
        {
            ["AuthHub__Redis__Enabled"] = enabled ? "true" : "false",
            ["AuthHub__Redis__KeyPrefix"] = $"test:{Guid.NewGuid():N}:"
        });

    private static HttpClient CreateClient(AuthHubWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://localhost/")
        });

    private static bool TryPingRedis()
    {
        try
        {
            ConnectionMultiplexer.Connect("localhost:6379,abortConnect=true,connectTimeout=2000").Dispose();
            return true;
        }
        catch (RedisConnectionException)
        {
            return false;
        }
    }

    private static async Task<string> GetClientCredentialsTokenAsync(HttpClient client)
    {
        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = M2mClientId,
            ["client_secret"] = M2mClientSecret,
            ["scope"] = "api:read"
        }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("access_token").GetString()!;
    }

    private static async Task<HttpResponseMessage> CallApiAsync(HttpClient client, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ApiPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    /// <summary>列出本用例前缀下的全部缓存键（证明条目确实进了 Redis）。</summary>
    private static List<string> CachedKeys(AuthHubWebApplicationFactory factory)
    {
        var multiplexer = factory.Services.GetRequiredService<IConnectionMultiplexer>();
        var prefix = factory.Services.GetRequiredService<IOptionsMonitor<AuthHub.Application.Options.RedisOptions>>()
            .CurrentValue.EffectiveKeyPrefix;
        var database = multiplexer.GetDatabase();

        return multiplexer.GetServers()
            .SelectMany(server => server.Keys(database.Database, $"{prefix}*"))
            .Select(key => key.ToString())
            .ToList();
    }
}
