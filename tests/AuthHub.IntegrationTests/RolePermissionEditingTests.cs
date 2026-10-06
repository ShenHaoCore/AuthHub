using System.Net;
using System.Text.RegularExpressions;
using AuthHub.Domain.Constants;
using AuthHub.Domain.Enums;
using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace AuthHub.IntegrationTests;

/// <summary>
/// 后台「角色与权限」页的**可编辑**契约 —— 权限归属从只读展示改成落库可改之后新增的一组用例。
///
/// 盯住四件事：
///   1) 权限弹窗渲染成可提交的复选框（而不是 <c>disabled</c> 的只读展示）；
///   2) 提交后**真的生效**（回读页面能看到勾选状态变了）并**落了审计**；
///   3) 「恢复默认」能把数据库里的覆盖行删掉、回落到配置 / 出厂默认；
///   4) 两道服务端护栏：未声明的权限名、以及"删掉最后一个 roles.manage 持有者"都会被拒。
///
/// 这些用例共用同一个宿主（同一个 SQLite 库），因此凡是对状态有写入的用例都用 try/finally
/// 把角色复位 —— 否则后面的用例会在"上一条留下的权限"上做出错误判断。
/// </summary>
[Collection(AuthHubFixture.CollectionName)]
public class RolePermissionEditingTests
{
    private readonly AuthHubFixture _fixture;

    public RolePermissionEditingTests(AuthHubFixture fixture) => _fixture = fixture;

    // ================================================================ 渲染

    [Fact]
    public async Task Permissions_dialog_should_render_submittable_checkboxes()
    {
        using var session = NewSession();
        await LoginAsync(session, "admin", "Admin@12345");

        var html = await RolesPageAsync(session);

        var checkboxes = Regex.Matches(html, "<input type=\"checkbox\"[^>]*>")
            .Select(match => match.Value)
            .ToArray();

        checkboxes.Should().NotBeEmpty(because: "权限弹窗要有权限点可勾选");
        checkboxes.Should().OnlyContain(
            box => box.Contains("name=\"SelectedPermissions\"", StringComparison.Ordinal),
            because: "复选框必须带字段名，否则表单提交上去是空的");
        checkboxes.Should().OnlyContain(
            box => !box.Contains("disabled", StringComparison.Ordinal),
            because: "权限树已从只读展示改为可勾选，不能再有 disabled");
    }

    [Fact]
    public async Task Permissions_dialog_should_expose_the_reset_action_only_when_customized()
    {
        using var session = NewSession();
        await LoginAsync(session, "admin", "Admin@12345");

        try
        {
            var before = DialogBlock(await RolesPageAsync(session), "dlg-perm-Auditor");
            before.Should().NotContain(
                "handler=resetPermissions",
                because: "还没有自定义过，「恢复默认」不该出现");

            await SaveAsync(session, AuthHubConstants.Roles.Auditor,
                AuthHubConstants.Permissions.AuditRead,
                AuthHubConstants.Permissions.UsersManage);

            var after = DialogBlock(await RolesPageAsync(session), "dlg-perm-Auditor");
            after.Should().Contain(
                "handler=resetPermissions",
                because: "已经自定义过，应当能一键回到配置 / 出厂默认");
        }
        finally
        {
            await ResetAsync(session, AuthHubConstants.Roles.Auditor);
        }
    }

    // ================================================================ 保存与生效

    [Fact]
    public async Task Saving_permissions_should_change_what_the_role_effectively_owns_and_write_an_audit()
    {
        using var session = NewSession();
        await LoginAsync(session, "admin", "Admin@12345");

        try
        {
            // 出厂默认里 Auditor 只有 audit.read，而它没有 users.manage
            var before = DialogBlock(await RolesPageAsync(session), "dlg-perm-Auditor");
            IsChecked(before, AuthHubConstants.Permissions.UsersManage).Should().BeFalse();
            IsChecked(before, AuthHubConstants.Permissions.AuditRead).Should().BeTrue();

            var response = await SaveAsync(session, AuthHubConstants.Roles.Auditor,
                AuthHubConstants.Permissions.AuditRead,
                AuthHubConstants.Permissions.UsersManage);

            response.StatusCode.Should().Be(HttpStatusCode.Found);

            var after = DialogBlock(await RolesPageAsync(session), "dlg-perm-Auditor");
            IsChecked(after, AuthHubConstants.Permissions.UsersManage).Should().BeTrue(
                because: "勾选后应当立刻生效，无需重启");
            IsChecked(after, AuthHubConstants.Permissions.AuditRead).Should().BeTrue();

            // 审计：权限归属的变更必须留痕，否则谁都说不清"这个权限是什么时候给出去的"
            var audit = await CookieSession.ReadHtmlAsync(
                await session.GetAsync($"/admin/audit-logs?action={AuditActionType.RolePermissionsUpdated}"));

            audit.Should().Contain(
                $"value=\"{AuditActionType.RolePermissionsUpdated}\"",
                because: "筛选下拉里应当能选中这个新动作");
            audit.Should().Contain(
                "class=\"ah-cell-main\">admin</div>",
                because: "审计必须记到人。少了这一条，UserName 会恒为空，"
                         + "页面渲染成「（无用户名）」—— 而 UserId 还在，所以这个缺陷不容易被注意到");
        }
        finally
        {
            await ResetAsync(session, AuthHubConstants.Roles.Auditor);
        }
    }

    [Fact]
    public async Task Saving_an_empty_selection_should_revoke_everything_rather_than_fall_back()
    {
        using var session = NewSession();
        await LoginAsync(session, "admin", "Admin@12345");

        try
        {
            // 一个都不勾选 —— 这是「显式收回全部权限」，与「没改过（回落默认）」是两件事
            var response = await SaveAsync(session, AuthHubConstants.Roles.Auditor);
            response.StatusCode.Should().Be(HttpStatusCode.Found);

            var dialog = DialogBlock(await RolesPageAsync(session), "dlg-perm-Auditor");

            IsChecked(dialog, AuthHubConstants.Permissions.AuditRead).Should().BeFalse(
                because: "空集合表示收回全部权限，不该回落到默认的 audit.read");
            dialog.Should().Contain(
                $"value=\"{AuthHubConstants.Permissions.AuditRead}\"",
                because: "权限点本身仍然列在那里，只是没被勾选");
        }
        finally
        {
            await ResetAsync(session, AuthHubConstants.Roles.Auditor);
        }
    }

    [Fact]
    public async Task Resetting_should_fall_back_to_the_configured_defaults()
    {
        using var session = NewSession();
        await LoginAsync(session, "admin", "Admin@12345");

        try
        {
            await SaveAsync(session, AuthHubConstants.Roles.Auditor,
                AuthHubConstants.Permissions.UsersManage);

            IsChecked(DialogBlock(await RolesPageAsync(session), "dlg-perm-Auditor"),
                AuthHubConstants.Permissions.UsersManage).Should().BeTrue();

            var reset = await ResetAsync(session, AuthHubConstants.Roles.Auditor);
            reset.StatusCode.Should().Be(HttpStatusCode.Found);

            var after = DialogBlock(await RolesPageAsync(session), "dlg-perm-Auditor");

            IsChecked(after, AuthHubConstants.Permissions.UsersManage).Should().BeFalse(
                because: "恢复到出厂默认后，Auditor 不该再有 users.manage");
            IsChecked(after, AuthHubConstants.Permissions.AuditRead).Should().BeTrue(
                because: "出厂默认里 Auditor 有 audit.read");
            after.Should().NotContain("handler=resetPermissions");
        }
        finally
        {
            await ResetAsync(session, AuthHubConstants.Roles.Auditor);
        }
    }

    // ================================================================ 服务端护栏

    [Fact]
    public async Task Saving_a_permission_that_does_not_exist_should_be_rejected()
    {
        using var session = NewSession();
        await LoginAsync(session, "admin", "Admin@12345");

        // 表单是用户可以改的：界面只列目录里的权限，但服务端不能因此就信任提交上来的值
        var response = await SaveAsync(session, AuthHubConstants.Roles.Auditor, "not.a.declared.permission");

        response.StatusCode.Should().Be(HttpStatusCode.OK, because: "校验失败是原地重渲染，不是 500");
        var html = await CookieSession.ReadHtmlAsync(response);
        html.Should().Contain("not.a.declared.permission");
    }

    [Fact]
    public async Task Removing_the_last_roles_manage_holder_should_be_rejected()
    {
        using var session = NewSession();
        await LoginAsync(session, "admin", "Admin@12345");

        // Administrator 是唯一持有 roles.manage 的内置角色。
        // 一旦允许取消，谁都进不了这个页面 —— 恢复只能去改数据库。
        var withoutRolesManage = AuthHubConstants.Permissions.All
            .Where(p => !string.Equals(p, AuthHubConstants.Permissions.RolesManage, StringComparison.Ordinal))
            .ToArray();

        var response = await SaveAsync(session, AuthHubConstants.Roles.Administrator, withoutRolesManage);

        response.StatusCode.Should().Be(HttpStatusCode.OK, because: "被护栏拦下时是原地重渲染");
        var html = await CookieSession.ReadHtmlAsync(response);

        html.Should().Contain(
            AuthHubConstants.Permissions.RolesManage,
            because: "错误文案要指名道姓地说清是哪个权限不能丢");
        DialogHeader(html, "dlg-perm-Administrator").Should().Contain(
            "data-dialog-autoopen=\"true\"",
            because: "失败时应当重开出错的那个弹窗，用户填过的内容不丢");

        // 状态未被改动
        var after = DialogBlock(await RolesPageAsync(session), "dlg-perm-Administrator");
        IsChecked(after, AuthHubConstants.Permissions.RolesManage).Should().BeTrue(
            because: "被拒绝的变更不能对数据产生任何影响");
    }

    // ================================================================ 辅助

    private CookieSession NewSession() => new(_fixture.Factory.Server);

    private static async Task LoginAsync(CookieSession session, string userName, string password)
    {
        var token = await AntiforgeryTokenAsync(session, "/account/login");

        var response = await session.PostFormAsync("/account/login", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["username"] = userName,
            ["password"] = password,
            ["returnUrl"] = "/"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Found, because: $"{userName} 的登录应成功并跳转");
    }

    private static async Task<string> AntiforgeryTokenAsync(CookieSession session, string path)
    {
        var html = await CookieSession.ReadHtmlAsync(await session.GetAsync(path));
        var token = AuthHubFixture.ExtractAntiforgeryToken(html);

        token.Should().NotBeNull(because: $"{path} 上应当渲染出 __RequestVerificationToken");
        return token!;
    }

    private static async Task<string> RolesPageAsync(CookieSession session)
        => await CookieSession.ReadHtmlAsync(await session.GetAsync("/admin/roles"));

    /// <summary>提交权限归属。复选框是同一字段名出现多次，所以要用 KVP 序列而不是字典。</summary>
    private static async Task<HttpResponseMessage> SaveAsync(
        CookieSession session,
        string roleName,
        params string[] permissions)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", await AntiforgeryTokenAsync(session, "/admin/roles")),
            new("PermissionRoleName", roleName)
        };

        fields.AddRange(permissions.Select(p => new KeyValuePair<string, string>("SelectedPermissions", p)));

        return await session.PostFormAsync("/admin/roles?handler=permissions", fields);
    }

    private static async Task<HttpResponseMessage> ResetAsync(CookieSession session, string roleName)
        => await session.PostFormAsync("/admin/roles?handler=resetPermissions", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await AntiforgeryTokenAsync(session, "/admin/roles"),
            ["PermissionRoleName"] = roleName
        });

    /// <summary>
    /// 截取某个弹窗的 HTML 片段（从它的 id 属性到闭合标签）。
    /// 页面里每个角色都有一个权限弹窗，靠整体 Contains 断言会串台到别的角色上。
    /// </summary>
    private static string DialogBlock(string html, string dialogId)
    {
        var start = html.IndexOf($"id=\"{dialogId}\"", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, because: $"页面里应当渲染出 {dialogId}");

        var end = html.IndexOf("</dialog>", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, because: $"{dialogId} 应当是一个完整的弹窗");

        return html[start..end];
    }

    /// <summary>弹窗的**开标签**部分（含 <c>class</c> 之后的 <c>data-dialog-autoopen</c> 等属性）。</summary>
    private static string DialogHeader(string html, string dialogId)
    {
        var block = DialogBlock(html, dialogId);
        return block[..block.IndexOf('>')];
    }

    /// <summary>该弹窗里某个权限点是否处于勾选状态。属性顺序无关，避免被 Razor 的渲染顺序绑死。</summary>
    private static bool IsChecked(string dialogHtml, string permission)
        => Regex.IsMatch(
            dialogHtml,
            $"<input type=\"checkbox\"[^>]*value=\"{Regex.Escape(permission)}\"[^>]*checked",
            RegexOptions.IgnoreCase);
}
