using System.Net;
using AuthHub.Application.Interfaces;
using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AuthHub.IntegrationTests;

/// <summary>
/// 管理后台的「允许的资源」字段（RFC 8707）。
///
/// <para><b>为什么这条链路值得单独钉住</b>：资源的勾选是**整组覆盖**语义
/// （<c>UpdateClientAsync</c> 用提交值重建 <c>rsrc:</c> 权限）。前端一旦漏渲染某个已持有的
/// 资源，管理员改个显示名就会把 MCP 客户端的受众权限清掉，而症状出现在几天后的
/// <c>invalid_target</c> 上 —— 与"改显示名"这件事毫无关联。所以这里既验证勾上会生效，
/// 也验证白名单外的历史资源仍会出现在弹窗里。</para>
///
/// <para>选择走真实表单提交而不是直接调服务：这一层的价值恰好在页面与 service 的接缝
/// （绑定名、数组绑定、单选值），直接调 service 就把接缝绕过去了。</para>
/// </summary>
public class AdminClientResourceTests
{
    private const string McpResource = "https://mcp.example.com/mcp";

    // ------------------------------------------------------------------ ① 勾选即生效

    [Fact]
    public async Task Creating_a_client_with_a_checked_resource_should_grant_rsrc_permission()
    {
        using var factory = CreateFactory(McpResource);
        using var session = NewSession(factory);
        await LoginAsync(session);

        var clientId = NewClientId();

        var created = await session.PostFormAsync("/admin/clients?handler=create", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await AntiforgeryTokenAsync(session, "/admin/clients"),
            ["NewClientId"] = clientId,
            ["NewDisplayName"] = "资源字段冒烟",
            ["NewApplicationType"] = "web",
            ["NewClientType"] = "confidential",
            ["NewRedirectUris"] = "https://app.example.com/callback",
            ["NewGrantTypes"] = "authorization_code",
            ["NewScopes"] = "openid",
            ["NewResources"] = McpResource,
            ["NewRequirePkce"] = "true"
        });

        created.StatusCode.Should().Be(HttpStatusCode.Found, because: "创建成功后应回到列表页");

        var client = await ReadClientAsync(factory, clientId);

        client.AllowedResources.Should().Contain(McpResource,
            because: "勾选资源后必须落成 rsrc: 权限，否则客户端带 resource 的授权请求会被 invalid_target 拒绝");
    }

    // ------------------------------------------------------------------ ② 白名单外的历史资源不会被静默丢弃

    [Fact]
    public async Task Edit_dialog_should_still_list_a_resource_that_left_the_whitelist()
    {
        // 白名单已空（部署侧撤掉了资源），但客户端记录里还带着它 ——
        // 这正是"改了个显示名，MCP 突然 invalid_target"的现场
        using var factory = CreateFactory(string.Empty);
        var clientId = NewClientId();

        using (var scope = factory.Services.CreateScope())
        {
            var clients = scope.ServiceProvider.GetRequiredService<IClientAdminService>();
            var seeded = await clients.CreateAsync(new AuthHub.Application.DTOs.Clients.CreateClientRequest(
                ClientId: clientId,
                ApplicationType: "web",
                ClientType: "confidential",
                RedirectUris: ["https://app.example.com/callback"],
                GrantTypes: ["authorization_code"],
                Scopes: ["openid"],
                Resources: [McpResource]));
            seeded.IsSuccess.Should().BeTrue(because: "资源本身不在白名单里也应允许写入客户端记录");
        }

        using var session = NewSession(factory);
        await LoginAsync(session);

        var html = await ReadHtmlAsync(await session.GetAsync("/admin/clients"));

        html.Should().Contain($"id=\"dlg-edit-client-{clientId}\"");
        html.Should().Contain($"value=\"{McpResource}\" checked",
            because: "已持有但不在白名单里的资源也要渲染成已勾选，否则保存一次就被清掉");
        html.Should().Contain("已不在白名单",
            because: "这种资源需要显式提示，让管理员知道它不是当前配置的一部分");
    }

    // ------------------------------------------------------------------ ③ 清空是显式行为

    [Fact]
    public async Task Saving_without_any_checked_resource_should_clear_the_permission()
    {
        using var factory = CreateFactory(McpResource);
        using var session = NewSession(factory);
        await LoginAsync(session);

        var clientId = NewClientId();

        await session.PostFormAsync("/admin/clients?handler=create", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await AntiforgeryTokenAsync(session, "/admin/clients"),
            ["NewClientId"] = clientId,
            ["NewApplicationType"] = "web",
            ["NewClientType"] = "confidential",
            ["NewRedirectUris"] = "https://app.example.com/callback",
            ["NewGrantTypes"] = "authorization_code",
            ["NewScopes"] = "openid",
            ["NewResources"] = McpResource,
            ["NewRequirePkce"] = "true"
        });

        var saved = await session.PostFormAsync("/admin/clients?handler=save", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await AntiforgeryTokenAsync(session, "/admin/clients"),
            ["EditClientId"] = clientId,
            ["EditDisplayName"] = "只改了显示名",
            ["EditRedirectUris"] = "https://app.example.com/callback",
            ["EditGrantTypes"] = "authorization_code",
            ["EditScopes"] = "openid",
            ["EditRequirePkce"] = "true"
            // 刻意不提交 EditResources：整组覆盖语义下等于"取消全部勾选"
        });

        saved.StatusCode.Should().Be(HttpStatusCode.Found);

        var client = await ReadClientAsync(factory, clientId);

        client.AllowedResources.Should().BeEmpty(
            because: "表单是整组覆盖：没勾的资源就要移除，否则界面上取消勾选会变成一个静默无效的操作");

        // 回归：这条路径以前对 confidential 客户端直接 500 —— 空描述符把密钥清空，
        // OpenIddict 的校验在"事后恢复"之前就抛了 ValidationException。
        client.HasSecret.Should().BeTrue(
            because: "只改资料不该让 confidential 客户端失去密钥，否则它下一次 client_credentials 就会失败");
    }

    // ------------------------------------------------------------------ ④ 密钥在编辑后仍可认证

    [Fact]
    public async Task Editing_a_confidential_client_should_keep_its_secret_working()
    {
        using var factory = CreateFactory(McpResource);
        var clientId = NewClientId();
        const string secret = "Edit-Keeps-Secret-2026!";

        using (var scope = factory.Services.CreateScope())
        {
            var clients = scope.ServiceProvider.GetRequiredService<IClientAdminService>();
            var created = await clients.CreateAsync(new AuthHub.Application.DTOs.Clients.CreateClientRequest(
                ClientId: clientId,
                ClientSecret: secret,
                ClientType: "confidential",
                GrantTypes: ["client_credentials"],
                Scopes: ["api:read"]));
            created.IsSuccess.Should().BeTrue();
        }

        using var session = NewSession(factory);
        await LoginAsync(session);

        var saved = await session.PostFormAsync("/admin/clients?handler=save", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await AntiforgeryTokenAsync(session, "/admin/clients"),
            ["EditClientId"] = clientId,
            ["EditDisplayName"] = "改了显示名",
            ["EditGrantTypes"] = "client_credentials",
            ["EditScopes"] = "api:read"
        });
        saved.StatusCode.Should().Be(HttpStatusCode.Found,
            because: "confidential 客户端的编辑曾经直接 500（空描述符清空密钥后校验抛异常）");

        // 只断言 HasSecret 不够：它只说明字段非空。这里真的拿原密钥换一次令牌。
        var token = await session.PostFormAsync("/connect/token", new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["scope"] = "api:read"
        });

        token.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "改显示名不该让客户端失去密钥；这里失败说明口令哈希被重建或清掉了");
    }

    // ------------------------------------------------------------------ ⑤ 没配资源时不摆空多选框
    [Fact]
    public async Task Without_any_configured_resource_the_dialog_should_explain_instead_of_showing_an_empty_group()
    {
        using var factory = CreateFactory(string.Empty);
        using var session = NewSession(factory);
        await LoginAsync(session);

        var html = await ReadHtmlAsync(await session.GetAsync("/admin/clients"));

        html.Should().Contain("未登记任何资源");
        html.Should().NotContain("name=\"NewResources\"");
    }

    // ==================================================================== 辅助

    private static AuthHubWebApplicationFactory CreateFactory(string resource) =>
        new(new Dictionary<string, string> { ["AuthHub__Resources__0"] = resource });

    private static CookieSession NewSession(AuthHubWebApplicationFactory factory) => new(factory.Server);

    private static string NewClientId() => $"ui-rsrc-{Guid.NewGuid():N}"[..16];

    private static async Task LoginAsync(CookieSession session)
    {
        var response = await session.PostFormAsync("/account/login", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await AntiforgeryTokenAsync(session, "/account/login"),
            ["username"] = "admin",
            ["password"] = "Admin@12345",
            ["returnUrl"] = "/admin/clients"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Found, because: "管理员登录应成功");
    }

    private static async Task<AuthHub.Application.DTOs.Clients.ClientDto> ReadClientAsync(
        AuthHubWebApplicationFactory factory,
        string clientId)
    {
        using var scope = factory.Services.CreateScope();
        var clients = scope.ServiceProvider.GetRequiredService<IClientAdminService>();

        var result = await clients.GetAsync(clientId);
        result.IsSuccess.Should().BeTrue(because: $"客户端 {clientId} 应已写入");

        return result.Value;
    }

    private static async Task<string> AntiforgeryTokenAsync(CookieSession session, string path)
    {
        var html = await ReadHtmlAsync(await session.GetAsync(path));
        var token = AuthHubFixture.ExtractAntiforgeryToken(html);

        token.Should().NotBeNull(because: $"{path} 上应当渲染出 __RequestVerificationToken");
        return token!;
    }

    private static async Task<string> ReadHtmlAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }
}
