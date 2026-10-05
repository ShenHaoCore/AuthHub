using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AuthHub.IntegrationTests;

/// <summary>
/// 集成测试共享上下文：同一个宿主（同一个 SQLite 库）跑完一组用例，
/// 既验证真实管道（认证中间件顺序、限流、CORS、安全头），又保持用例相互独立。
/// </summary>
public sealed class AuthHubFixture : IDisposable
{
    /// <summary>
    /// xUnit 集合名。收敛成常量而非散落的字面量，避免改名时漏改导致
    /// 悄悄退化成「每个测试类各起一个宿主」而失去共享意义。
    /// </summary>
    public const string CollectionName = "authhub";

    public AuthHubFixture()
    {
        Factory = new AuthHubWebApplicationFactory();
        Client = Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://localhost/")
        });
    }

    public AuthHubWebApplicationFactory Factory { get; }

    public HttpClient Client { get; }

    /// <summary>用客户端凭证流程取一个访问令牌。</summary>
    public async Task<string> GetClientCredentialsTokenAsync(string scopes = "api:read api:write")
    {
        var response = await Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = AuthHubWebApplicationFactory.M2mClientId,
            ["client_secret"] = AuthHubWebApplicationFactory.M2mClientSecret,
            ["scope"] = scopes
        }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadAccessTokenAsync(response);
    }

    public static async Task<string> ReadAccessTokenAsync(HttpResponseMessage response)
    {
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>构造一个带 Bearer 令牌的请求。</summary>
    public static HttpRequestMessage Authorized(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public static string? ExtractAntiforgeryToken(string html)
        => Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value is { Length: > 0 } token
            ? token
            : null;

    public void Dispose() => Factory.Dispose();

    /// <summary>便捷方法：断言返回体是 RFC 7807 形状的错误。</summary>
    public static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expected, string expectedCode)
    {
        response.StatusCode.Should().Be(expected);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        problem.GetProperty("status").GetInt32().Should().Be((int)expected);
        problem.GetProperty("code").GetString().Should().Be(expectedCode);
        problem.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
    }
}

[CollectionDefinition(AuthHubFixture.CollectionName)]
public sealed class AuthHubIntegrationSuite : ICollectionFixture<AuthHubFixture>
{
}
