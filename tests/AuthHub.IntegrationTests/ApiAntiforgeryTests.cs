using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using AuthHub.IntegrationTests.Infrastructure;
using Xunit;

namespace AuthHub.IntegrationTests;

/// <summary>
/// /api 防伪的行为契约：
/// ① 纯 Cookie 控制器（AccountApiController，源内 MVC 特性）——无令牌 400、有令牌 204；
/// ② Bearer 穿透已在 ApiAuthorizationTests 覆盖（方案感知过滤器对非 Cookie 认证直接放行）。
/// </summary>
public class ApiAntiforgeryTests
{
    [Fact]
    public async Task Cookie_mutation_without_token_should_be_rejected_with_antiforgery_problem()
    {
        using var factory = new AuthHubWebApplicationFactory();
        using var session = new CookieSession(factory.Server);
        await LoginAsAdminAsync(session);

        var response = await session.PostJsonWithoutAntiforgeryAsync("/api/account/logout", new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await response.Content.ReadAsStringAsync());
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object?>>();
        problem.Should().NotBeNull();
        problem!["code"]?.ToString().Should().Be("antiforgery_invalid");
    }

    [Fact]
    public async Task Cookie_mutation_with_token_should_succeed()
    {
        using var factory = new AuthHubWebApplicationFactory();
        using var session = new CookieSession(factory.Server);
        await LoginAsAdminAsync(session);

        var response = await session.PostJsonAsync("/api/account/logout", new { });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, because: await response.Content.ReadAsStringAsync());
    }

    private static async Task LoginAsAdminAsync(CookieSession session)
    {
        var response = await session.PostFormAsync("/account/login", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await MvcAntiforgeryTokenAsync(session),
            ["username"] = "admin",
            ["password"] = "Admin@12345",
            ["returnUrl"] = "/"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Found, because: "管理员登录应成功");
    }

    /// <summary>从登录页表单提取 MVC 防伪令牌（与 RegisteredResourceTests 同款）。</summary>
    private static async Task<string> MvcAntiforgeryTokenAsync(CookieSession session)
    {
        var html = await (await session.GetAsync("/account/login")).Content.ReadAsStringAsync();
        var start = html.IndexOf("name=\"__RequestVerificationToken\"", StringComparison.Ordinal);
        var valueStart = html.IndexOf("value=\"", start, StringComparison.Ordinal) + "value=\"".Length;
        var valueEnd = html.IndexOf('"', valueStart);
        return html[valueStart..valueEnd];
    }
}
