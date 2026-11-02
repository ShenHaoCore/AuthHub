using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthHub.Application.DTOs.Clients;
using AuthHub.Application.Interfaces;
using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AuthHub.IntegrationTests;

/// <summary>
/// 资源白名单库表化(issue #23)的验收用例。
/// </summary>
/// <remarks>
/// - 核心语义变化：<c>AuthHub:Resources</c> 配置从「白名单本体」降级为「幂等种子」，本体搬到
///   RegisteredResources 表 —— 资源校验每请求现查库，管理 API 变更即时生效。
/// - 两条红线：未登记(或停用)的资源一律 <c>invalid_target</c>(默认拒绝)；配置资源升级后自动入库，行为不变。
/// - 用独立宿主(不用共享 <see cref="AuthHubFixture"/>)：验收 1 需要「白名单为空」的起点，
///   而共享宿主的配置资源是固定注入的。
/// </remarks>
public class RegisteredResourceTests
{
    private const string McpResource = "https://mcp.example.com/mcp";
    private const string TestClientId = "db-rsrc-client";
    private const string TestClientSecret = "db-rsrc-secret";

    // ------------------------------------------------------------------ ① 运行时登记,不重启生效

    [Fact]
    public async Task Registering_a_resource_at_runtime_should_take_effect_without_restart()
    {
        // 空配置启动:白名单为空,带 resource 的令牌请求必须被默认拒绝
        using var factory = CreateFactory();
        await CreateTestClientAsync(factory.Services, McpResource);

        using var client = factory.CreateClient();
        (await ReadErrorAsync(await RequestTokenAsync(client, McpResource)))
            .Should().Be("invalid_target", because: "表为空 = 默认拒绝,这是不可放松的红线");

        // 管理员经 API 登记资源 —— 不改配置、不重启
        using var session = new CookieSession(factory.Server);
        await LoginAsync(session);

        var created = await session.PostJsonAsync("/api/resources", new { resource = McpResource, displayName = "MCP" });
        created.StatusCode.Should().Be(HttpStatusCode.OK, because: "登记应当成功并立即成为白名单成员");

        // 同一个 factory、同一个客户端:下一个请求就能拿到该受众
        var token = await RequestTokenAsync(client, McpResource);
        token.StatusCode.Should().Be(HttpStatusCode.OK, because: "库驱动校验下,登记后的下一个请求即生效");
        ReadAudiences(await AuthHubFixture.ReadAccessTokenAsync(token))
            .Should().BeEquivalentTo(new[] { McpResource });
    }

    // ------------------------------------------------------------------ ② 停用等同未登记(默认拒绝不放松)

    [Fact]
    public async Task Disabling_a_registered_resource_should_reject_it_again()
    {
        using var factory = CreateFactory(McpResource);

        var id = await ReadResourceIdAsync(factory, McpResource, enabled: true,
            because: "配置资源应经种子自动入库并启用");

        using var session = new CookieSession(factory.Server);
        await LoginAsync(session);

        var updated = await session.PutJsonAsync($"/api/resources/{id}", new { enabled = false });
        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        using var client = factory.CreateClient();

        // 客户端**有** rsrc: 权限也一样:白名单本身不再认它
        (await ReadErrorAsync(await RequestTokenAsync(client, McpResource)))
            .Should().Be("invalid_target", because: "Enabled=false 等同未登记,默认拒绝方向不能有任何旁路");
    }

    // ------------------------------------------------------------------ ③ 配置升级自动入库

    [Fact]
    public async Task Configured_resources_should_be_seeded_into_the_database_on_startup()
    {
        using var factory = CreateFactory(McpResource);
        await CreateTestClientAsync(factory.Services, McpResource);

        // 种子入库:行存在且启用
        await ReadResourceIdAsync(factory, McpResource, enabled: true,
            because: "老部署升级后,AuthHub:Resources 里的资源必须自动进库,不能要求人工迁移");

        // 行为等价:令牌请求与库表化之前一致
        using var client = factory.CreateClient();
        var token = await RequestTokenAsync(client, McpResource);

        token.StatusCode.Should().Be(HttpStatusCode.OK);
        ReadAudiences(await AuthHubFixture.ReadAccessTokenAsync(token))
            .Should().BeEquivalentTo(new[] { McpResource });
    }

    // ------------------------------------------------------------------ ④ API 权限

    [Fact]
    public async Task Resources_api_should_require_authentication_and_resources_manage_permission()
    {
        using var factory = CreateFactory(McpResource);
        using var client = factory.CreateClient();

        // 匿名 → 401 ProblemDetails,而不是 302 到登录页
        var anonymous = await client.GetAsync("/api/resources");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 无 resources.manage 权限的令牌 → 403(M2M 令牌只有 api:read)
        var token = await AuthHubFixture.ReadAccessTokenAsync(
            await client.PostAsync("/connect/token", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = AuthHubWebApplicationFactory.M2mClientId,
                    ["client_secret"] = AuthHubWebApplicationFactory.M2mClientSecret,
                    ["scope"] = "api:read"
                })));

        var forbidden = await client.SendAsync(AuthHubFixture.Authorized(HttpMethod.Get, "/api/resources", token));
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "资源白名单是安全边界,管理权必须显式授予");
    }

    // ------------------------------------------------------------------ ⑤ 删除策略:先收客户端权限

    [Fact]
    public async Task Deleting_a_resource_still_granted_to_a_client_should_be_rejected()
    {
        using var factory = CreateFactory(McpResource);
        await CreateTestClientAsync(factory.Services, McpResource);

        var id = await ReadResourceIdAsync(factory, McpResource, enabled: true, because: "");

        using var session = new CookieSession(factory.Server);
        await LoginAsync(session);

        // 客户端还持着 rsrc: → 禁止删除
        var blocked = await session.DeleteAsync($"/api/resources/{id}");
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict,
            because: "资源下线必须是显式的两步操作,不能一个 DELETE 打断一批客户端");

        // 收回客户端权限后,删除放行
        using (var scope = factory.Services.CreateScope())
        {
            var clients = scope.ServiceProvider.GetRequiredService<IClientAdminService>();
            var update = await clients.UpdateAsync(TestClientId, new UpdateClientRequest(
                Resources: Array.Empty<string>()));
            update.IsSuccess.Should().BeTrue();
        }

        var deleted = await session.DeleteAsync($"/api/resources/{id}");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // fail-closed:登记删掉后,请求自然回到默认拒绝
        using var client = factory.CreateClient();
        (await ReadErrorAsync(await RequestTokenAsync(client, McpResource)))
            .Should().Be("invalid_target", because: "资源已不在白名单,无论客户端权限如何");
    }

    // ------------------------------------------------------------------ 辅助

    /// <summary>
    /// 空白名单宿主。必须显式把 AuthHub__Resources__0 置空：工厂走进程级环境变量，基线字典不含这个键 ——
    /// 前一个用例设过的资源会残留进来，让"白名单为空"的起点悄悄失真。
    /// </summary>
    private static AuthHubWebApplicationFactory CreateFactory() =>
        new(new Dictionary<string, string> { ["AuthHub__Resources__0"] = string.Empty });

    private static AuthHubWebApplicationFactory CreateFactory(string resource) =>
        new(new Dictionary<string, string> { ["AuthHub__Resources__0"] = resource });

    /// <summary>按资源标识查登记行的 Id(同时断言存在与启用状态)。</summary>
    private static async Task<long> ReadResourceIdAsync(
        AuthHubWebApplicationFactory factory,
        string resource,
        bool enabled,
        string because)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRegisteredResourceStore>();
        _ = because;

        var resources = await store.GetEnabledResourcesAsync();
        if (enabled)
        {
            resources.Should().Contain(resource);
        }
        else
        {
            resources.Should().NotContain(resource);
        }

        var admin = scope.ServiceProvider.GetRequiredService<IResourceAdminService>();
        var all = await admin.GetAllAsync();
        var match = all.Single(entry => entry.Resource == resource);
        match.Enabled.Should().Be(enabled);

        return match.Id;
    }

    private static async Task CreateTestClientAsync(IServiceProvider services, params string[] grantedResources)
    {
        using var scope = services.CreateScope();
        var clients = scope.ServiceProvider.GetRequiredService<IClientAdminService>();

        if (await clients.GetAsync(TestClientId) is { IsSuccess: true })
        {
            return;
        }

        // 资源权限可以在白名单登记**之前**写入客户端记录(白名单只管服务端认哪些资源,
        // 客户端权限是另一条校验),这正是"先接客户端、后登记资源"的真实顺序
        var created = await clients.CreateAsync(new CreateClientRequest(
            ClientId: TestClientId,
            ClientSecret: TestClientSecret,
            ClientType: "confidential",
            GrantTypes: ["client_credentials"],
            Scopes: ["api:read"],
            Resources: grantedResources));
        created.IsSuccess.Should().BeTrue();
    }

    private static async Task LoginAsync(CookieSession session)
    {
        var response = await session.PostFormAsync("/account/login", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await AntiforgeryTokenAsync(session),
            ["username"] = "admin",
            ["password"] = "Admin@12345",
            ["returnUrl"] = "/"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Found, because: "管理员登录应成功");
    }

    private static async Task<string> AntiforgeryTokenAsync(CookieSession session)
    {
        var html = await CookieSession.ReadHtmlAsync(await session.GetAsync("/account/login"));
        var token = AuthHubFixture.ExtractAntiforgeryToken(html);
        token.Should().NotBeNull();
        return token!;
    }

    private static Task<HttpResponseMessage> RequestTokenAsync(HttpClient client, string resource) =>
        client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = TestClientId,
                ["client_secret"] = TestClientSecret,
                ["scope"] = "api:read",
                ["resource"] = resource
            }));

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
        var padded = payload.Replace('-', '+').Replace('_', '/');
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(
            padded.PadRight((padded.Length + 3) / 4 * 4, '=')));
        var document = JsonDocument.Parse(json).RootElement;

        if (!document.TryGetProperty("aud", out var aud))
        {
            return Array.Empty<string>();
        }

        return aud.ValueKind == JsonValueKind.Array
            ? aud.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : new[] { aud.GetString()! };
    }
}
