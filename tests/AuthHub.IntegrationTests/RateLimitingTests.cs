using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using System.Net;

namespace AuthHub.IntegrationTests;

/// <summary>
/// 限流测试：需要一个额度很小的宿主，因此不复用共享 fixture，
/// 单独构造一个 <c>TokenRequestsPerMinute = 2</c> 的实例。
/// </summary>
public class RateLimitingTests
{
    [Fact]
    public async Task Token_endpoint_should_return_429_after_exceeding_the_quota()
    {
        using var factory = new AuthHubWebApplicationFactory(new Dictionary<string, string>
        {
            ["AuthHub__RateLimiting__TokenRequestsPerMinute"] = "2"
        });

        using var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = AuthHubWebApplicationFactory.M2mClientId,
                    ["client_secret"] = AuthHubWebApplicationFactory.M2mClientSecret,
                    ["scope"] = "api:read"
                }));

            statuses.Add(response.StatusCode);
        }

        // 前两次放行，第三次被固定窗口限流器拒绝
        statuses.Take(2).Should().AllBeEquivalentTo(HttpStatusCode.OK);
        statuses[2].Should().Be(HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// 浏览器登录表单（<c>POST /account/login</c>）是爆破的主要入口，必须挂上登录限流。
    ///
    /// 这里刻意用"不带防伪令牌的 POST"来验证：全局限流额度是登录额度的 10 倍，
    /// 因此第 3 次就 429 只可能来自 LoginPolicy 本身，而不是全局兜底。
    /// 前两次返回 400（防伪校验失败）也正说明请求确实走到了 MVC。
    /// </summary>
    [Fact]
    public async Task Html_login_form_should_be_rate_limited()
    {
        using var factory = new AuthHubWebApplicationFactory(new Dictionary<string, string>
        {
            ["AuthHub__RateLimiting__LoginRequestsPerMinute"] = "2",
            ["AuthHub__RateLimiting__TokenRequestsPerMinute"] = "1000"
        });

        using var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["username"] = "alice",
                    ["password"] = "wrong-password"
                }));

            statuses.Add(response.StatusCode);
        }

        statuses.Take(2).Should().NotContain(HttpStatusCode.TooManyRequests);
        statuses[2].Should().Be(HttpStatusCode.TooManyRequests);
    }
}
