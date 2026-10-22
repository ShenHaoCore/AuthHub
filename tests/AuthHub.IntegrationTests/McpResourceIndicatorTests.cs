using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthHub.IntegrationTests;

/// <summary>
/// RFC 8707 资源指示器 + PKCE 只留 S256 —— MCP 授权（OAuth 2.1）的两条硬要求。
///
/// 这组用例用**独立宿主**：资源集合是启动时快照的，配置必须在 host 构建前就位；
/// 需要的客户端也只在测试库里现造，不去改种子数据。
///
/// 守住的行为：
/// ① 登记过且客户端有权限的 resource → 令牌 aud **只**是它（不给多余的受众）；
/// ② 没登记的 resource → invalid_target，不能悄悄退化成"忽略这个参数"；
/// ③ 客户端没有该资源的权限 → invalid_request（登记只说明资源存在，授权看客户端权限）；
/// ④ 不带 resource 的老客户端 → aud 仍是 authhub-api，行为不变；
/// ⑤ 发现文档只声明 S256，授权请求用 plain 直接拒。
/// </summary>
public class McpResourceIndicatorTests
{
    private const string McpResource = "https://mcp.example.com/mcp";
    private const string UnknownResource = "https://attacker.example.com/mcp";
    private const string TestClientId = "mcp-test-client";
    private const string TestClientSecret = "mcp-test-secret";

    private static AuthHubWebApplicationFactory CreateFactory() =>
        new(new Dictionary<string, string>
        {
            // 生产上这一项来自配置（AuthHub:Resources）；测试里用环境变量覆盖
            ["AuthHub__Resources__0"] = McpResource
        });

    // ------------------------------------------------------------------ ① 登记的资源进 aud

    [Fact]
    public async Task Registered_resource_should_become_the_only_audience()
    {
        using var factory = CreateFactory();
        await CreateTestClientAsync(factory.Services, McpResource);
        using var client = factory.CreateClient();

        var response = await RequestTokenAsync(client, McpResource);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var audiences = ReadAudiences(await AuthHubFixture.ReadAccessTokenAsync(response));

        // 注意是"只有它"：scope api:read 本身关联 authhub-api，
        // 如果实现里把 scope 资源一起塞进 aud，这张发给 MCP Server 的票
        // 就能回头调 AuthHub 的 API —— 施害者只需要攻破 MCP Server。
        audiences.Should().BeEquivalentTo(new[] { McpResource });
    }

    // ------------------------------------------------------------------ ② 未登记的资源

    [Fact]
    public async Task Unregistered_resource_should_be_rejected_with_invalid_target()
    {
        using var factory = CreateFactory();

        // 客户端**有**这个资源的权限也没用：资源本身没在配置里登记
        await CreateTestClientAsync(factory.Services, UnknownResource);
        using var client = factory.CreateClient();

        var response = await RequestTokenAsync(client, UnknownResource);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).Should().Be("invalid_target");
    }

    // ------------------------------------------------------------------ ③ 有资源但无权限

    [Fact]
    public async Task Registered_resource_without_client_permission_should_be_rejected()
    {
        using var factory = CreateFactory();
        await CreateTestClientAsync(factory.Services);
        using var client = factory.CreateClient();

        var response = await RequestTokenAsync(client, McpResource);

        // 关键：不能因为"资源已登记"就放行 —— 登记只说明这个资源存在，
        // 具体某个客户端能不能拿它的令牌，是客户端权限说了算。
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).Should().Be("invalid_request");
    }

    // ------------------------------------------------------------------ ④ 老客户端不受影响

    [Fact]
    public async Task Token_without_resource_should_keep_the_legacy_audience()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // 种子里的 M2M 客户端，请求里不带 resource —— 升级后行为必须一模一样
        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = AuthHubWebApplicationFactory.M2mClientId,
                ["client_secret"] = AuthHubWebApplicationFactory.M2mClientSecret,
                ["scope"] = "api:read"
            }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var audiences = ReadAudiences(await AuthHubFixture.ReadAccessTokenAsync(response));
        audiences.Should().BeEquivalentTo(new[] { "authhub-api" });
    }

    // ------------------------------------------------------------------ ⑤ 发现文档只留 S256

    [Fact]
    public async Task Discovery_document_should_only_advertise_S256()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var document = JsonDocument
            .Parse(await client.GetStringAsync("/.well-known/openid-configuration"))
            .RootElement;

        var methods = document.GetProperty("code_challenge_methods_supported")
            .EnumerateArray().Select(e => e.GetString()).ToArray();

        methods.Should().Equal(new[] { "S256" },
            "声明了 plain 却拒绝它会自相矛盾；只留 S256 让文档与实际校验一致");
    }

    // ------------------------------------------------------------------ ⑥ plain 请求被拒

    [Fact]
    public async Task Authorization_request_with_plain_PKCE_should_be_rejected()
    {
        using var factory = CreateFactory();
        using var client = CreateClientWithoutRedirects(factory);

        var response = await client.GetAsync(AuthorizeUrl("plain", "0123456789abcdef0123456789abcdef"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).Should().Be("invalid_request",
            "plain 把 verifier 明文放在前端渠道，OAuth 2.1 已弃用，必须直接拒");
    }

    // ------------------------------------------------------------------ ⑦ S256 请求能通过校验

    [Fact]
    public async Task Authorization_request_with_S256_should_pass_validation()
    {
        using var factory = CreateFactory();
        using var client = CreateClientWithoutRedirects(factory);

        var verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var response = await client.GetAsync(AuthorizeUrl("S256", challenge));

        // 通过协议校验后才会走到业务分支（未登录 → 跳登录页）。
        // 只要到了这一步，就说明 S256 没被误杀。
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/account/login");
    }

    // ------------------------------------------------------------------ 工具方法

    private static HttpClient CreateClientWithoutRedirects(AuthHubWebApplicationFactory factory) =>
        factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://localhost/")
        });

    private static async Task<HttpResponseMessage> RequestTokenAsync(HttpClient client, string resource)
    {
        return await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = TestClientId,
                ["client_secret"] = TestClientSecret,
                ["scope"] = "api:read",
                ["resource"] = resource
            }));
    }

    /// <summary>造一个只走客户端凭证流程的测试客户端，按需授予资源权限。</summary>
    private static async Task CreateTestClientAsync(IServiceProvider services, params string[] grantedResources)
    {
        using var scope = services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        if (await manager.FindByClientIdAsync(TestClientId) is not null)
        {
            return;
        }

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = TestClientId,
            ClientSecret = TestClientSecret,
            DisplayName = "MCP 资源指示器测试客户端",
            ClientType = ClientTypes.Confidential,
            ConsentType = ConsentTypes.Implicit,
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.ClientCredentials,
                Permissions.Prefixes.Scope + "api:read"
            }
        };

        foreach (var resource in grantedResources)
        {
            descriptor.Permissions.Add(Permissions.Prefixes.Resource + resource);
        }

        await manager.CreateAsync(descriptor);
    }

    private static string AuthorizeUrl(string method, string challenge)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = "spa-client",
            ["redirect_uri"] = "https://localhost:3000/callback",
            ["scope"] = "openid api:read",
            ["state"] = "test-state",
            ["nonce"] = "test-nonce",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = method
        };

        var queryString = string.Join("&", query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        return "/connect/authorize?" + queryString;
    }

    /// <summary>
    /// 取错误码。令牌端点返回 JSON；授权端点在协议校验阶段失败时返回的是
    /// <c>error:invalid_request</c> 这种逐行文本（OpenIddict 的默认错误格式），两种都要能读。
    /// </summary>
    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        if (body.StartsWith("error:", StringComparison.Ordinal))
        {
            return body[6..].Split('\n')[0].Trim();
        }

        var document = JsonDocument.Parse(body).RootElement;
        return document.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    private static IReadOnlyList<string> ReadAudiences(string jwt)
    {
        var payload = jwt.Split('.')[1];
        var json = Encoding.UTF8.GetString(Base64UrlDecode(payload));
        var document = JsonDocument.Parse(json).RootElement;

        if (!document.TryGetProperty("aud", out var aud))
        {
            return Array.Empty<string>();
        }

        return aud.ValueKind == JsonValueKind.Array
            ? aud.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : new[] { aud.GetString()! };
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }
}
