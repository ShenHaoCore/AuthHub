using AuthHub.Domain.Constants;
using FluentAssertions;
using Xunit;

namespace AuthHub.UnitTests.Domain;

/// <summary>RBAC 角色 → 权限映射的领域规则测试。</summary>
public class RolePermissionMapTests
{
    [Fact]
    public void Administrator_should_own_every_declared_permission()
    {
        var permissions = RolePermissionMap.ResolvePermissions(new[] { AuthHubConstants.Roles.Administrator });

        permissions.Should().BeEquivalentTo(AuthHubConstants.Permissions.All);
        permissions.Should().HaveCount(AuthHubConstants.Permissions.All.Count);
    }

    [Fact]
    public void User_role_should_own_no_management_permission()
    {
        RolePermissionMap.GetPermissions(AuthHubConstants.Roles.User).Should().BeEmpty();
        RolePermissionMap.ResolvePermissions(new[] { AuthHubConstants.Roles.User }).Should().BeEmpty();
    }

    [Theory]
    [InlineData(AuthHubConstants.Roles.UserManager, AuthHubConstants.Permissions.UsersManage)]
    [InlineData(AuthHubConstants.Roles.UserManager, AuthHubConstants.Permissions.TokensRevoke)]
    [InlineData(AuthHubConstants.Roles.Auditor, AuthHubConstants.Permissions.AuditRead)]
    public void Built_in_roles_should_map_to_expected_permissions(string role, string permission)
    {
        RolePermissionMap.RoleHasPermission(role, permission).Should().BeTrue();
    }

    [Fact]
    public void UserManager_should_not_own_audit_permission()
    {
        RolePermissionMap.RoleHasPermission(AuthHubConstants.Roles.UserManager, AuthHubConstants.Permissions.AuditRead)
            .Should().BeFalse();
    }

    [Fact]
    public void Unknown_role_should_yield_empty_permissions_instead_of_throwing()
    {
        RolePermissionMap.GetPermissions("not-a-real-role").Should().BeEmpty();
        RolePermissionMap.IsBuiltInRole("not-a-real-role").Should().BeFalse();
    }

    [Fact]
    public void ResolvePermissions_should_expand_and_deduplicate_multiple_roles()
    {
        // Auditor 只给 audit.read，UserManager 给 users.manage + tokens.revoke
        var permissions = RolePermissionMap.ResolvePermissions(new[]
        {
            AuthHubConstants.Roles.Auditor,
            AuthHubConstants.Roles.UserManager,
            AuthHubConstants.Roles.Auditor
        });

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
        var granted = AuthHubConstants.Roles.All
            .SelectMany(RolePermissionMap.GetPermissions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        granted.Should().BeEquivalentTo(AuthHubConstants.Permissions.All);
    }

    [Fact]
    public void Built_in_role_detection_should_cover_all_declared_roles()
    {
        foreach (var role in AuthHubConstants.Roles.All)
        {
            RolePermissionMap.IsBuiltInRole(role).Should().BeTrue($"{role} 应为内置角色");
        }
    }
}
