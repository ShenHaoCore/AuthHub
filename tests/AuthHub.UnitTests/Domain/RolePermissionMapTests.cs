using AuthHub.Application.Options;
using AuthHub.Domain.Constants;
using AuthHub.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AuthHub.UnitTests.Domain;

/// <summary>
/// 「角色 → 权限」归属的规则测试。
///
/// 映射已从代码内常量搬到配置（见 RolePermissionOptions），这里用**出厂默认**构造 ——
/// 它等价于"部署时 appsettings 没有覆盖任何角色"的情形，也是升级前后行为必须逐字一致的基线。
/// </summary>
public class RolePermissionMapTests
{
    /// <summary>用出厂默认配置构造映射。</summary>
    private static ConfiguredRolePermissionMap CreateMap()
        => new(Options.Create(new RolePermissionOptions()));

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
        permissions.Should().OnlyHaveUniqueItems();
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

    // ------------------------------------------------------------------ 配置覆盖默认的语义

    [Fact]
    public void Configured_role_should_override_the_default_mapping()
    {
        var options = new RolePermissionOptions();
        options.Roles[AuthHubConstants.Roles.Auditor] =
        [
            AuthHubConstants.Permissions.AuditRead,
            AuthHubConstants.Permissions.UsersManage
        ];

        var map = new ConfiguredRolePermissionMap(Options.Create(options));

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

        var map = new ConfiguredRolePermissionMap(Options.Create(options));

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

        var map = new ConfiguredRolePermissionMap(Options.Create(options));

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

        var map = new ConfiguredRolePermissionMap(Options.Create(options));

        map.RoleHasPermission("ReportViewer", AuthHubConstants.Permissions.AuditRead).Should().BeTrue();
        map.IsBuiltInRole("ReportViewer").Should().BeFalse();
        map.All.Should().ContainKey("ReportViewer");
    }
}
