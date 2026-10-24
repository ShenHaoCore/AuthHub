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
    private const string SecondResource = "https://other.example.com/api";
    private const string UnknownResource = "https://attacker.example.com/mcp";
    private const string TestClientId = "mcp-test-client";
    private const string TestClientSecret = "mcp-test-secret";
    private const string CodeClientId = "mcp-code-client";
    private const string CodeClientSecret = "mcp-code-secret";
    private const string CodeRedirectUri = "https://localhost:3999/callback";

    /// <summary>PKCE verifier（43 字符，RFC 7636 的下限长度，测试用固定值即可）。</summary>
    private const string CodeVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

    private static AuthHubWebApplicationFactory CreateFactory() => CreateFactory(McpResource);

    private static AuthHubWebApplicationFactory CreateFactory(params string[] resources)
    {
        // 生产上资源集合来自配置（AuthHub:Resources）；测试里用环境变量覆盖。
        // 资源集合是启动时快照，配置必须在 host 构建前就位。
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < resources.Length; index++)
        {
            settings[$"AuthHub__Resources__{index}"] = resources[index];
        }

        return new AuthHubWebApplicationFactory(settings);
    }

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

    // ------------------------------------------------------------------ ⑧ 刷新时变更受众被拒

    /// <summary>
    /// RFC 8707 §5：刷新只允许**收窄**受众。一个同时持有两个资源权限的客户端，
    /// 用为 A 签发的刷新令牌去请求 B 的令牌，必须被拒 —— 否则受众可以事后拓宽，
    /// 「发给 MCP Server 的票只能进 MCP Server」这条保证就出现回退。
    /// </summary>
    [Fact]
    public async Task Refreshing_with_a_different_resource_should_be_rejected()
    {
        using var factory = CreateFactory(McpResource, SecondResource);
        await CreateCodeTestClientAsync(factory.Services, McpResource, SecondResource);
        var (_, refreshToken) = await ObtainTokensAsync(factory, McpResource);

        using var client = factory.CreateClient();
        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = CodeClientId,
                ["client_secret"] = CodeClientSecret,
                ["refresh_token"] = refreshToken,
                ["resource"] = SecondResource
            }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).Should().Be("invalid_grant",
            because: "原授权的受众只有 MCP 资源，刷新时请求另一个资源等于变更受众，RFC 8707 §5 禁止变宽");
    }

    // ------------------------------------------------------------------ ⑨ 刷新时带原受众照常通过

    /// <summary>
    /// 对照组：MCP 客户端按规范在刷新请求里**重复**授权时的 resource 参数，
    /// 必须照常通过且新令牌受众不变 —— 收紧受众校验不能误伤规范内的正常刷新。
    /// </summary>
    [Fact]
    public async Task Refreshing_with_the_same_resource_should_keep_the_audience()
    {
        using var factory = CreateFactory(McpResource, SecondResource);
        await CreateCodeTestClientAsync(factory.Services, McpResource, SecondResource);
        var (accessToken, refreshToken) = await ObtainTokensAsync(factory, McpResource);

        using var client = factory.CreateClient();
        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = CodeClientId,
                ["client_secret"] = CodeClientSecret,
                ["refresh_token"] = refreshToken,
                ["resource"] = McpResource
            }));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "刷新时重复原授权的 resource 是 MCP 规范要求的常规写法，不能被受众校验误杀");
        var audiences = ReadAudiences(await AuthHubFixture.ReadAccessTokenAsync(response));
        audiences.Should().BeEquivalentTo(new[] { McpResource });
        accessToken.Should().NotBeNullOrEmpty();
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

    // ------------------------------------------------- 受众一致性用例（⑧⑨）的流程辅助

    /// <summary>以演示账号 alice 登录，走完整授权码流程，返回（访问令牌，刷新令牌）。</summary>
    private static async Task<(string AccessToken, string RefreshToken)> ObtainTokensAsync(
        AuthHubWebApplicationFactory factory, string resource)
    {
        using var session = new CookieSession(factory.Server);
        await LoginAsync(session);

        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(CodeVerifier)));

        var authorizeResponse = await session.GetAsync(CodeAuthorizeUrl(resource, challenge));
        authorizeResponse.StatusCode.Should().Be(HttpStatusCode.Redirect,
            because: "已登录 + 隐式同意的客户端，授权请求应直接 302 回调地址带 code");
        var location = authorizeResponse.Headers.Location!.OriginalString;
        location.Should().StartWith(CodeRedirectUri);
        var code = ExtractQueryParameter(location, "code");
        code.Should().NotBeNullOrEmpty(because: "回调地址上应携带授权码");

        using var client = factory.CreateClient();
        var tokenResponse = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = CodeClientId,
                ["client_secret"] = CodeClientSecret,
                ["code"] = code!,
                ["redirect_uri"] = CodeRedirectUri,
                ["code_verifier"] = CodeVerifier,
                ["resource"] = resource
            }));

        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "受众一致性用例的前置：授权码兑换应成功");
        var json = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync()).RootElement;
        return (json.GetProperty("access_token").GetString()!, json.GetProperty("refresh_token").GetString()!);
    }

    private static async Task LoginAsync(CookieSession session)
    {
        var response = await session.PostFormAsync("/account/login", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await AntiforgeryTokenAsync(session, "/account/login"),
            ["username"] = "alice",
            ["password"] = "Alice@12345",
            ["returnUrl"] = "/"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Found, because: "演示账号 alice 应能登录");
    }

    private static async Task<string> AntiforgeryTokenAsync(CookieSession session, string path)
    {
        var html = await CookieSession.ReadHtmlAsync(await session.GetAsync(path));
        var token = AuthHubFixture.ExtractAntiforgeryToken(html);

        token.Should().NotBeNull(because: $"{path} 上应当渲染出 __RequestVerificationToken");
        return token!;
    }

    private static string CodeAuthorizeUrl(string resource, string challenge)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = CodeClientId,
            ["redirect_uri"] = CodeRedirectUri,
            ["scope"] = "openid offline_access",
            ["state"] = "st",
            ["nonce"] = "nc",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["resource"] = resource
        };

        var queryString = string.Join("&", query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        return "/connect/authorize?" + queryString;
    }

    private static string? ExtractQueryParameter(string url, string name)
    {
        var queryStart = url.IndexOf('?');
        if (queryStart < 0)
        {
            return null;
        }

        foreach (var pair in url[(queryStart + 1)..].Split('&'))
        {
            var separator = pair.IndexOf('=');
            if (separator > 0 && Uri.UnescapeDataString(pair[..separator]) == name)
            {
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        return null;
    }

    /// <summary>造一个走授权码 + 刷新流程的测试客户端，按需授予资源权限。</summary>
    private static async Task CreateCodeTestClientAsync(IServiceProvider services, params string[] grantedResources)
    {
        using var scope = services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        if (await manager.FindByClientIdAsync(CodeClientId) is not null)
        {
            return;
        }

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = CodeClientId,
            ClientSecret = CodeClientSecret,
            DisplayName = "MCP 受众一致性测试客户端",
            ClientType = ClientTypes.Confidential,
            ConsentType = ConsentTypes.Implicit,
            RedirectUris = { new Uri(CodeRedirectUri) },
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + "openid",
                Permissions.Prefixes.Scope + "offline_access"
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange }
        };

        foreach (var resource in grantedResources)
        {
            descriptor.Permissions.Add(Permissions.Prefixes.Resource + resource);
        }

        await manager.CreateAsync(descriptor);
    }
}
