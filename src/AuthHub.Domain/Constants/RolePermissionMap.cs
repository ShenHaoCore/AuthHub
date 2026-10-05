namespace AuthHub.Domain.Constants;

/// <summary>
/// 角色 → 权限的映射表（RBAC 的“角色权限”部分）。
/// 纯领域逻辑，不依赖任何基础设施，便于单元测试。
/// </summary>
public static class RolePermissionMap
{
    private static readonly Dictionary<string, string[]> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [AuthHubConstants.Roles.Administrator] = new[]
        {
            AuthHubConstants.Permissions.ClientsManage,
            AuthHubConstants.Permissions.ScopesManage,
            AuthHubConstants.Permissions.UsersManage,
            AuthHubConstants.Permissions.RolesManage,
            AuthHubConstants.Permissions.TokensRevoke,
            AuthHubConstants.Permissions.AuditRead
        },
        [AuthHubConstants.Roles.UserManager] = new[]
        {
            AuthHubConstants.Permissions.UsersManage,
            AuthHubConstants.Permissions.TokensRevoke
        },
        [AuthHubConstants.Roles.Auditor] = new[]
        {
            AuthHubConstants.Permissions.AuditRead
        },
        [AuthHubConstants.Roles.User] = Array.Empty<string>()
    };

    /// <summary>取得指定角色拥有的权限集合。</summary>
    public static IEnumerable<string> GetPermissions(string roleName)
        => Map.TryGetValue(roleName, out var permissions) ? permissions : Array.Empty<string>();

    /// <summary>把一组角色展开为去重后的权限集合。</summary>
    public static IReadOnlyCollection<string> ResolvePermissions(IEnumerable<string> roleNames)
        => roleNames.SelectMany(GetPermissions)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

    /// <summary>角色是否拥有指定权限。</summary>
    public static bool RoleHasPermission(string roleName, string permission)
        => GetPermissions(roleName).Contains(permission, StringComparer.OrdinalIgnoreCase);

    /// <summary>该角色是否为内置角色。</summary>
    public static bool IsBuiltInRole(string roleName)
        => Map.ContainsKey(roleName);
}
