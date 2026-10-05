using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;

namespace AuthHub.IntegrationTests;

/// <summary>
/// 受保护资源与授权策略的集成测试。
/// 重点验证：未认证返回 401（而不是 302 到登录页）、登录了但缺权限返回 403、
/// 缺 scope 时写操作被拒。
/// </summary>
[Collection(AuthHubFixture.CollectionName)]
public class ApiAuthorizationTests
{
    private readonly AuthHubFixture _fixture;

    public ApiAuthorizationTests(AuthHubFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("/api/profile")]
    [InlineData("/api/users")]
    [InlineData("/api/clients")]
    [InlineData("/api/audit-logs")]
    public async Task Protected_api_without_token_should_return_401_problem_details(string path)
    {
        var response = await _fixture.Client.GetAsync(path);

        await AuthHubFixture.AssertProblemAsync(response, HttpStatusCode.Unauthorized, "Unauthorized");
    }

    [Fact]
    public async Task Session_cookie_does_not_authenticate_bearer_only_api_calls()
    {
        // /api/account/me 使用会话 Cookie 方案，未登录时必须返回 401 JSON 而不是 302
        var response = await _fixture.Client.GetAsync("/api/account/me");

        await AuthHubFixture.AssertProblemAsync(response, HttpStatusCode.Unauthorized, "Unauthorized");
    }

    [Fact]
    public async Task Bearer_token_with_api_read_scope_should_access_profile()
    {
        var token = await _fixture.GetClientCredentialsTokenAsync("api:read");

        var response = await _fixture.Client.SendAsync(
            AuthHubFixture.Authorized(HttpMethod.Get, "/api/profile", token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Read_only_token_should_be_forbidden_from_write_endpoint()
    {
        var token = await _fixture.GetClientCredentialsTokenAsync("api:read");

        var request = AuthHubFixture.Authorized(HttpMethod.Post, "/api/profile/echo", token);
        request.Content = JsonContent.Create(new { message = "hello" });

        var response = await _fixture.Client.SendAsync(request);

        await AuthHubFixture.AssertProblemAsync(response, HttpStatusCode.Forbidden, "Forbidden");
    }

    [Fact]
    public async Task Write_scope_should_allow_the_write_endpoint()
    {
        var token = await _fixture.GetClientCredentialsTokenAsync("api:read api:write");

        var request = AuthHubFixture.Authorized(HttpMethod.Post, "/api/profile/echo", token);
        request.Content = JsonContent.Create(new { message = "hello" });

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Token_without_management_permission_should_not_reach_admin_api()
    {
        // M2M 令牌带 api:read，但没有任何 authhub:permission 声明
        var token = await _fixture.GetClientCredentialsTokenAsync("api:read");

        var response = await _fixture.Client.SendAsync(
            AuthHubFixture.Authorized(HttpMethod.Get, "/api/users", token));

        await AuthHubFixture.AssertProblemAsync(response, HttpStatusCode.Forbidden, "Forbidden");
    }

    [Fact]
    public async Task Client_credentials_token_should_be_rejected_by_userinfo()
    {
        // userinfo 面向"用户身份"。OpenIddict 在校验阶段就要求令牌能代表一个用户，
        // M2M（client_credentials）令牌的 sub 是 client_id 而不是用户，
        // 因此请求在 passthrough 到控制器之前就被拒了，我们的 Userinfo() 根本不会被调用。
        //
        // 语义与 RFC 6750 一致：令牌本身有效、只是身份/范围不足 → 403（不是 401）。
        // 错误细节放在 WWW-Authenticate 头里、响应体为空。
        // 注意错误码是 OpenIddict 自己的 insufficient_access（ID2095），
        // 而非 RFC 6750 里的 insufficient_scope —— 这是库的既定契约，不去覆写它。
        var token = await _fixture.GetClientCredentialsTokenAsync("api:read");

        var response = await _fixture.Client.SendAsync(
            AuthHubFixture.Authorized(HttpMethod.Get, "/connect/userinfo", token));

        var challenge = string.Join(" ", response.Headers.WwwAuthenticate.Select(h => h.ToString()));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, because: $"WWW-Authenticate：{challenge}");
        challenge.Should().Contain("insufficient_access");
    }

    [Fact]
    public async Task Unknown_route_should_return_404()
    {
        var response = await _fixture.Client.GetAsync("/api/does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
