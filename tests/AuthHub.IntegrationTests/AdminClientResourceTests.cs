using System.Net;
using AuthHub.Application.Interfaces;
using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AuthHub.IntegrationTests;

/// <summary>
/// 管理后台的「允许的资源」字段（RFC 8707）。
/// </summary>
/// <remarks>
/// - 资源勾选是整组覆盖语义（<c>UpdateClientAsync</c> 用提交值重建 <c>rsrc:</c> 权限）：前端一旦漏渲染
///   某个已持有的资源，管理员改个显示名就会清掉 MCP 客户端的受众权限，症状是几天后的
///   <c>invalid_target</c>，与"改显示名"毫无关联。所以既验证勾上生效，也验证白名单外的历史资源仍出现在弹窗里。
/// - 走真实表单提交而非直接调服务：这一层的价值恰好在页面与 service 的接缝（绑定名、数组绑定、单选值）。
/// </remarks>
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

    // ------------------------------------------------------------------ ⑤ 白名单真的为空时不摆空多选框
    [Fact]
    public async Task Without_any_registered_resource_the_dialog_should_explain_instead_of_showing_an_empty_group()
    {
        // 配置为空只是必要条件：白名单的事实源是 RegisteredResources 表，
        // 测试工厂的演示种子会往表里种一个资源，先清掉才是真正的空名单
        using var factory = CreateFactory(string.Empty);
        await DeleteAllRegisteredResourcesAsync(factory);

        using var session = NewSession(factory);
        await LoginAsync(session);

        var html = await ReadHtmlAsync(await session.GetAsync("/admin/clients"));

        html.Should().Contain("未登记任何资源");
        html.Should().NotContain("name=\"NewResources\"");
    }

    // ------------------------------------------------------------------ ⑥ 库里登记的资源即时出现在弹窗里

    /// <summary>
    /// 回归：客户端页的勾选项曾读 <c>AuthHub:Resources</c> 配置（种子，通常为空），
    /// 而白名单事实源 v1.4.0 起已库表化 —— 结果资源页明明有登记，客户端弹窗却说
    /// 「未登记任何资源」，与「登记即时生效」直接矛盾。
    /// </summary>
    [Fact]
    public async Task A_resource_registered_in_the_database_should_be_offered_even_when_config_is_empty()
    {
        using var factory = CreateFactory(string.Empty);

        // 走管理服务登记 —— 与管理员在资源页的操作同一条路径，且无需重启
        using (var scope = factory.Services.CreateScope())
        {
            var resources = scope.ServiceProvider.GetRequiredService<IResourceAdminService>();
            var created = await resources.CreateAsync(new AuthHub.Application.DTOs.Resources.CreateResourceRequest(
                Resource: McpResource,
                DisplayName: "库表化后的登记"));
            created.IsSuccess.Should().BeTrue(because: "前置条件：资源应能登记入库");
        }

        using var session = NewSession(factory);
        await LoginAsync(session);

        var html = await ReadHtmlAsync(await session.GetAsync("/admin/clients"));

        html.Should().Contain($"name=\"NewResources\" value=\"{McpResource}\"",
            because: "资源页登记后，客户端弹窗必须即时出现可勾选项 —— 否则「登记即时生效」在客户端侧断了");
        html.Should().NotContain("未登记任何资源",
            because: "库里已有启用中的登记，不该再显示空态提示");
    }

    // ------------------------------------------------------------------ ⑦ 停用的登记不作为新选项提供

    [Fact]
    public async Task A_disabled_resource_should_not_be_offered_as_a_new_option()
    {
        // Enabled=false 等同未登记：勾上它换来的只会是 invalid_target，
        // 因此新建弹窗不该把它列为可勾选项（已持有的旧值仍由 ② 的机制兜底往返）
        using var factory = CreateFactory(string.Empty);

        using (var scope = factory.Services.CreateScope())
        {
            var resources = scope.ServiceProvider.GetRequiredService<IResourceAdminService>();
            var created = await resources.CreateAsync(new AuthHub.Application.DTOs.Resources.CreateResourceRequest(
                Resource: McpResource));
            created.IsSuccess.Should().BeTrue();

            var disabled = await resources.UpdateAsync(
                created.Value.Id,
                new AuthHub.Application.DTOs.Resources.UpdateResourceRequest(Enabled: false));
            disabled.IsSuccess.Should().BeTrue();
        }

        using var session = NewSession(factory);
        await LoginAsync(session);

        var html = await ReadHtmlAsync(await session.GetAsync("/admin/clients"));

        html.Should().NotContain($"name=\"NewResources\" value=\"{McpResource}\"",
            because: "停用的资源等同未登记，不该作为新选项出现");
    }

    // ==================================================================== 辅助

    private static AuthHubWebApplicationFactory CreateFactory(string resource) =>
        new(new Dictionary<string, string> { ["AuthHub__Resources__0"] = resource });

    private static CookieSession NewSession(AuthHubWebApplicationFactory factory) => new(factory.Server);

    private static string NewClientId() => $"ui-rsrc-{Guid.NewGuid():N}"[..16];

    /// <summary>清空 RegisteredResources 表（含演示种子），构造真正的空白名单。</summary>
    private static async Task DeleteAllRegisteredResourcesAsync(AuthHubWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var resources = scope.ServiceProvider.GetRequiredService<IResourceAdminService>();

        foreach (var registration in await resources.GetAllAsync())
        {
            var deleted = await resources.DeleteAsync(registration.Id);
            deleted.IsSuccess.Should().BeTrue(
                because: "前置条件失败：种子登记被客户端引用（409）时无法清空白名单，空态用例就无从谈起");
        }
    }

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
