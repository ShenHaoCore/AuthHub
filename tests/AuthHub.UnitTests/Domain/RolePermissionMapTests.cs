using AuthHub.Application.Options;
using AuthHub.Domain.Constants;
using AuthHub.Infrastructure.Services;
using AuthHub.UnitTests.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AuthHub.UnitTests.Domain;

/// <summary>
/// 「角色 → 权限」归属的规则测试 —— 也就是 <c>LayeredRolePermissionMap</c> 的**叠加语义**。
///
/// 三层来源：出厂默认 ← 配置 ← 运行时覆盖（数据库）。这里把后两层都做成可控输入：
///   · 配置层用 <see cref="RolePermissionOptions"/> 直接给（等价于部署时 appsettings 写了什么）；
///   · 覆盖层用 <see cref="FakeRolePermissionOverrideStore"/>（等价于后台界面上改了什么）。
/// 两者都不写就等价于"老部署、既没配过也没改过"——升级前后行为必须逐字一致的基线。
/// </summary>
public class RolePermissionMapTests
{
    /// <summary>用出厂默认配置 + 给定的运行时覆盖构造映射。</summary>
    private static LayeredRolePermissionMap CreateMap(
        RolePermissionOptions? options = null,
        FakeRolePermissionOverrideStore? store = null)
        => new(
            Options.Create(options ?? new RolePermissionOptions()),
            store ?? new FakeRolePermissionOverrideStore(),
            NullLogger<LayeredRolePermissionMap>.Instance);

    [Fact]
    public void Administrator_should_own_every_declared_permission()
    {
        var permissions = CreateMap().ResolvePermissions([AuthHubConstants.Roles.Administrator]);

        permissions.Should().BeEquivalentTo(AuthHubConstants.Permissions.All);
        permissions.Should().HaveCount(AuthHubConstants.Permissions.All.Count);
    }

    [Fact]
    public void User_role_should_own_no_management_permission()
    {
        var map = CreateMap();

        map.GetPermissions(AuthHubConstants.Roles.User).Should().BeEmpty();
        map.ResolvePermissions([AuthHubConstants.Roles.User]).Should().BeEmpty();
    }

    [Theory]
    [InlineData(AuthHubConstants.Roles.UserManager, AuthHubConstants.Permissions.UsersManage)]
    [InlineData(AuthHubConstants.Roles.UserManager, AuthHubConstants.Permissions.TokensRevoke)]
    [InlineData(AuthHubConstants.Roles.Auditor, AuthHubConstants.Permissions.AuditRead)]
    public void Built_in_roles_should_map_to_expected_permissions(string role, string permission)
    {
        CreateMap().RoleHasPermission(role, permission).Should().BeTrue();
    }

    [Fact]
    public void UserManager_should_not_own_audit_permission()
    {
        CreateMap().RoleHasPermission(AuthHubConstants.Roles.UserManager, AuthHubConstants.Permissions.AuditRead)
            .Should().BeFalse();
    }

    [Fact]
    public void Unknown_role_should_yield_empty_permissions_instead_of_throwing()
    {
        var map = CreateMap();

        map.GetPermissions("not-a-real-role").Should().BeEmpty();
        map.IsBuiltInRole("not-a-real-role").Should().BeFalse();
        map.IsCustomized("not-a-real-role").Should().BeFalse();
    }

    [Fact]
    public void ResolvePermissions_should_expand_and_deduplicate_multiple_roles()
    {
        // Auditor 只给 audit.read，UserManager 给 users.manage + tokens.revoke
        var permissions = CreateMap().ResolvePermissions([
            AuthHubConstants.Roles.Auditor,
            AuthHubConstants.Roles.UserManager,
            AuthHubConstants.Roles.Auditor
        ]);

        permissions.Should().BeEquivalentTo(new[]
        {
            AuthHubConstants.Permissions.AuditRead,
            AuthHubConstants.Permissions.UsersManage,
            AuthHubConstants.Permissions.TokensRevoke
        });
        permissions.Should().HaveCount(3, because: "同一个角色出现两次不应产生重复权限");
    }

    [Fact]
    public void Every_permission_should_be_reachable_by_at_least_one_built_in_role()
    {
        // 防止「定义了一个权限却没有任何角色拥有它」——那意味着该权限永远无法通过策略校验
        var map = CreateMap();

        var granted = AuthHubConstants.Roles.All
            .SelectMany(map.GetPermissions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        granted.Should().BeEquivalentTo(AuthHubConstants.Permissions.All);
    }

    [Fact]
    public void Built_in_role_detection_should_cover_all_declared_roles()
    {
        var map = CreateMap();

        foreach (var role in AuthHubConstants.Roles.All)
        {
            map.IsBuiltInRole(role).Should().BeTrue($"{role} 应为内置角色");
        }
    }

    // ------------------------------------------------------------------ 第二层：配置覆盖出厂默认

    [Fact]
    public void Configured_role_should_override_the_default_mapping()
    {
        var options = new RolePermissionOptions();
        options.Roles[AuthHubConstants.Roles.Auditor] =
        [
            AuthHubConstants.Permissions.AuditRead,
            AuthHubConstants.Permissions.UsersManage
        ];

        var map = CreateMap(options);

        map.GetPermissions(AuthHubConstants.Roles.Auditor).Should().BeEquivalentTo(new[]
        {
            AuthHubConstants.Permissions.AuditRead,
            AuthHubConstants.Permissions.UsersManage
        });
    }

    [Fact]
    public void Overriding_one_role_should_not_disturb_the_others()
    {
        var options = new RolePermissionOptions();
        options.Roles[AuthHubConstants.Roles.Auditor] = [AuthHubConstants.Permissions.UsersManage];

        var map = CreateMap(options);

        map.GetPermissions(AuthHubConstants.Roles.UserManager).Should().BeEquivalentTo(new[]
        {
            AuthHubConstants.Permissions.UsersManage,
            AuthHubConstants.Permissions.TokensRevoke
        }, because: "配置只覆盖它列出的角色，其余沿用出厂默认");
    }

    [Fact]
    public void Empty_permission_list_should_revoke_everything_for_that_role()
    {
        var options = new RolePermissionOptions();
        options.Roles[AuthHubConstants.Roles.Auditor] = [];

        var map = CreateMap(options);

        map.GetPermissions(AuthHubConstants.Roles.Auditor).Should().BeEmpty();
        map.ResolvePermissions([AuthHubConstants.Roles.Auditor]).Should().BeEmpty();
    }

    [Fact]
    public void Custom_role_granted_permissions_should_still_not_count_as_built_in()
    {
        // 判据是编译期的角色常量，而不是"配置里出现过" ——
        // 否则给自定义角色补一条映射就会顺手把它变成不可删除的系统角色。
        var options = new RolePermissionOptions();
        options.Roles["ReportViewer"] = [AuthHubConstants.Permissions.AuditRead];

        var map = CreateMap(options);

        map.RoleHasPermission("ReportViewer", AuthHubConstants.Permissions.AuditRead).Should().BeTrue();
        map.IsBuiltInRole("ReportViewer").Should().BeFalse();
        map.All.Should().ContainKey("ReportViewer");
    }

    // ------------------------------------------------------------------ 第三层：运行时覆盖（后台界面写入）

    [Fact]
    public void Runtime_override_should_beat_both_baseline_and_configuration()
    {
        var options = new RolePermissionOptions();
        options.Roles[AuthHubConstants.Roles.Auditor] = [AuthHubConstants.Permissions.AuditRead];

        var store = new FakeRolePermissionOverrideStore(
            (AuthHubConstants.Roles.Auditor, [AuthHubConstants.Permissions.UsersManage]));

        var map = CreateMap(options, store);

        map.GetPermissions(AuthHubConstants.Roles.Auditor)
            .Should().Equal(AuthHubConstants.Permissions.UsersManage);
        map.IsCustomized(AuthHubConstants.Roles.Auditor).Should().BeTrue();
    }

    [Fact]
    public void Runtime_override_with_empty_list_should_revoke_everything_and_must_not_fall_back()
    {
        // 这是整套语义里最容易写错的一条：
        // 「有覆盖行 + 空数组」= 显式收回全部权限，**不能**回落配置。
        // 它也是"关系表方案"被否掉的原因 —— 关系表里"没有行"同时表示这两件事。
        var options = new RolePermissionOptions();
        options.Roles[AuthHubConstants.Roles.UserManager] =
        [
            AuthHubConstants.Permissions.UsersManage,
            AuthHubConstants.Permissions.TokensRevoke
        ];

        var store = new FakeRolePermissionOverrideStore((AuthHubConstants.Roles.UserManager, []));

        var map = CreateMap(options, store);

        map.GetPermissions(AuthHubConstants.Roles.UserManager).Should().BeEmpty(
            because: "空数组表示显式收回全部权限，而不是没覆盖过");
        map.IsCustomized(AuthHubConstants.Roles.UserManager).Should().BeTrue();
    }

    [Fact]
    public void Removing_the_override_and_invalidating_should_fall_back_to_the_baseline()
    {
        var store = new FakeRolePermissionOverrideStore(
            (AuthHubConstants.Roles.Auditor, [AuthHubConstants.Permissions.UsersManage]));

        var map = CreateMap(store: store);
        map.GetPermissions(AuthHubConstants.Roles.Auditor)
            .Should().Equal(AuthHubConstants.Permissions.UsersManage);

        // 「恢复默认」= 删掉覆盖行 + 让缓存失效
        store.Remove(AuthHubConstants.Roles.Auditor);
        map.Invalidate();

        map.GetPermissions(AuthHubConstants.Roles.Auditor)
            .Should().Equal(AuthHubConstants.Permissions.AuditRead);
        map.IsCustomized(AuthHubConstants.Roles.Auditor).Should().BeFalse();
    }

    [Fact]
    public void Overriding_one_role_should_not_disturb_the_others_either()
    {
        var store = new FakeRolePermissionOverrideStore(
            (AuthHubConstants.Roles.Auditor, [AuthHubConstants.Permissions.UsersManage]));

        var map = CreateMap(store: store);

        map.GetPermissions(AuthHubConstants.Roles.UserManager).Should().BeEquivalentTo(new[]
        {
            AuthHubConstants.Permissions.UsersManage,
            AuthHubConstants.Permissions.TokensRevoke
        }, because: "覆盖是按角色整体替换，不涉及别的角色");
    }

    [Fact]
    public void Snapshot_should_be_cached_until_invalidated()
    {
        var store = new FakeRolePermissionOverrideStore();

        var map = CreateMap(store: store);

        map.GetPermissions(AuthHubConstants.Roles.Auditor).Should().NotBeNull();
        map.GetPermissions(AuthHubConstants.Roles.Administrator).Should().NotBeNull();
        store.LoadCount.Should().Be(1, because: "单例快照，读路径不应每次都回库");

        map.Invalidate();
        map.GetPermissions(AuthHubConstants.Roles.Auditor).Should().NotBeNull();
        store.LoadCount.Should().Be(2, because: "失效后应重新读取一次");
    }

    [Fact]
    public void Override_should_apply_to_a_custom_role_that_the_baseline_never_mentions()
    {
        var store = new FakeRolePermissionOverrideStore(
            ("ReportViewer", [AuthHubConstants.Permissions.AuditRead]));

        var map = CreateMap(store: store);

        map.RoleHasPermission("ReportViewer", AuthHubConstants.Permissions.AuditRead).Should().BeTrue();
        map.IsBuiltInRole("ReportViewer").Should().BeFalse();
        map.All.Should().ContainKey("ReportViewer");
    }
}
