using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AuthHub.IntegrationTests;

/// <summary>OIDC 协议端点与令牌端点的集成测试。</summary>
[Collection(AuthHubFixture.CollectionName)]
public class OidcEndpointTests
{
    private readonly AuthHubFixture _fixture;

    public OidcEndpointTests(AuthHubFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Discovery_document_should_advertise_all_endpoints()
    {
        var response = await _fixture.Client.GetAsync("/.well-known/openid-configuration");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        document.GetProperty("issuer").GetString().Should().Be("http://localhost/");

        var grants = document.GetProperty("grant_types_supported").EnumerateArray().Select(e => e.GetString()).ToArray();
        grants.Should().Contain(new[] { "authorization_code", "client_credentials", "refresh_token" });

        document.GetProperty("code_challenge_methods_supported").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("S256");

        foreach (var name in new[]
                 {
                     "authorization_endpoint", "token_endpoint", "userinfo_endpoint",
                     "revocation_endpoint", "introspection_endpoint", "jwks_uri"
                 })
        {
            document.GetProperty(name).GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task Health_endpoint_should_be_healthy()
    {
        var response = await _fixture.Client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }

    /// <summary>
    /// 下游服务"不回源也能校验令牌"这个承诺，必须真的成立。
    ///
    /// 这里用公钥集里的 n / e 现场还原 RSA 公钥，再对 Access Token 的签名做一次真实的
    /// 密码学验签 —— 也就是说，任何拿到发现文档的服务都能独立完成校验，不需要 introspection。
    ///
    /// 这条之所以重要：如果令牌被加密成 JWE（OpenIddict 的默认行为），第三方根本读不出
    /// claims，这个测试会直接失败，从而守住 <c>DisableAccessTokenEncryption()</c> 这个决定。
    /// </summary>
    [Fact]
    public async Task Access_token_should_be_verifiable_offline_with_the_published_jwks()
    {
        var token = await _fixture.GetClientCredentialsTokenAsync("api:read");

        // 顺着发现文档给出的 jwks_uri 取公钥集（下游服务会把它缓存起来）
        var discovery = JsonDocument
            .Parse(await _fixture.Client.GetStringAsync("/.well-known/openid-configuration"))
            .RootElement;
        var jwksUri = discovery.GetProperty("jwks_uri").GetString()!;

        using var jwksResponse = await _fixture.Client.GetAsync(jwksUri);
        jwksResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var jwks = JsonDocument.Parse(await jwksResponse.Content.ReadAsStringAsync()).RootElement;

        var parts = token.Split('.');
        parts.Should().HaveCount(3, "Access Token 必须是 JWS（三段式），不能是加密的 JWE");

        var header = ParseJwtPart(parts[0]);
        var keyId = header.GetProperty("kid").GetString();
        header.GetProperty("alg").GetString().Should().Be("RS256");

        // 按 kid 选中签名用的那把公钥 —— 下游也必须这么选，否则密钥轮换时会验签失败
        var jwk = jwks.GetProperty("keys").EnumerateArray()
            .Single(k => k.GetProperty("kid").GetString() == keyId);

        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = Base64UrlDecode(jwk.GetProperty("n").GetString()!),
            Exponent = Base64UrlDecode(jwk.GetProperty("e").GetString()!)
        });

        var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");

        rsa.VerifyData(
                signingInput,
                Base64UrlDecode(parts[2]),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1)
            .Should().BeTrue("仅凭公钥集就应当能验签，这是下游离线校验的前提");
    }

    [Fact]
    public async Task Client_credentials_should_issue_a_readable_signed_token()
    {
        var token = await _fixture.GetClientCredentialsTokenAsync("api:read api:write");

        // 三段式 JWT（JWS），说明访问令牌未被加密 —— 下游服务可以用 jwks_uri 离线校验
        token.Split('.').Should().HaveCount(3);

        var payload = JwtPayload(token);
        payload.GetProperty("sub").GetString().Should().Be(AuthHubWebApplicationFactory.M2mClientId);
        payload.GetProperty("scope").GetString().Should().Be("api:read api:write");
        payload.GetProperty("iss").GetString().Should().Be("http://localhost/");
    }

    [Fact]
    public async Task Client_credentials_with_wrong_secret_should_be_rejected()
    {
        var response = await _fixture.Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = AuthHubWebApplicationFactory.M2mClientId,
            ["client_secret"] = "definitely-wrong",
            ["scope"] = "api:read"
        }));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("invalid_client");
    }

    [Fact]
    public async Task Client_credentials_with_unknown_client_should_be_rejected()
    {
        var response = await _fixture.Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "not-registered",
            ["client_secret"] = "whatever",
            ["scope"] = "api:read"
        }));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("invalid_client");
    }

    [Fact]
    public async Task Disabled_password_grant_should_be_rejected()
    {
        var response = await _fixture.Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = AuthHubWebApplicationFactory.M2mClientId,
            ["client_secret"] = AuthHubWebApplicationFactory.M2mClientSecret,
            ["username"] = "alice",
            ["password"] = "Alice@12345"
        }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().MatchRegex("unsupported_grant_type|unauthorized_client");
    }

    [Fact]
    public async Task Authorization_endpoint_without_session_should_redirect_to_login_page()
    {
        var response = await _fixture.Client.GetAsync(
            "/connect/authorize?client_id=spa-client" +
            "&redirect_uri=https%3A%2F%2Flocalhost%3A3000%2Fcallback" +
            "&response_type=code&scope=openid%20profile&state=abc" +
            "&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().StartWith("/account/login?returnUrl=");
    }

    [Fact]
    public async Task Login_page_should_render_html_with_an_antiforgery_token()
    {
        var response = await _fixture.Client.GetAsync("/account/login");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");

        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("登录 AuthHub");
        AuthHubFixture.ExtractAntiforgeryToken(html).Should().NotBeNullOrWhiteSpace();

        // 会话 Cookie 必须是 HttpOnly + SameSite=Lax
        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? string.Join(";", values)
            : string.Empty;
        // FluentAssertions 的 Contain 在字符串上没有 OccurrenceConstraint 重载，
        // 直接数出现次数，语义更直白。
        Regex.Count(setCookie, "httponly", RegexOptions.IgnoreCase).Should().Be(1);
    }

    /// <summary>安全响应头应由中间件统一注入（含 CSP、HSTS、防嗅探）。</summary>
    [Fact]
    public async Task Responses_should_carry_security_headers()
    {
        var response = await _fixture.Client.GetAsync("/.well-known/openid-configuration");

        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().ContainSingle().Which.Should().Be("DENY");

        var csp = string.Join(" ", response.Headers.GetValues("Content-Security-Policy"));
        csp.Should().Contain("frame-ancestors 'none'").And.Contain("form-action 'self'");
    }

    private static JsonElement JwtPayload(string token) => ParseJwtPart(token.Split('.')[1]);

    /// <summary>解析 JWT 的某一段（header / payload）：base64url → UTF-8 JSON。</summary>
    private static JsonElement ParseJwtPart(string part)
        => JsonDocument.Parse(Base64UrlDecode(part)).RootElement;

    /// <summary>
    /// base64url 解码。.NET 8 没有 <c>System.Buffers.Text.Base64Url</c>（9.0 才加入），
    /// 这里手写补齐 padding。
    /// </summary>
    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }
}
