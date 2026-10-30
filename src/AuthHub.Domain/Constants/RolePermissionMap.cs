namespace AuthHub.Domain.Constants;

/// <summary>内置角色的**出厂默认**「角色 → 权限」映射。</summary>
/// <remarks>
/// - 不再是运行时的唯一事实源：部署可用配置 <c>AuthHub:RolePermissions</c> 覆盖（见 Application 层的 RolePermissionOptions 与 IRolePermissionMap 契约）。仍保留默认值的理由：老部署的 appsettings 没有这一段，升级前后行为逐字一致；且默认值用 <see cref="AuthHubConstants.Permissions"/> 常量拼出，重命名权限时编译器报错。
/// - **不**采用"配置缺失就启动失败"：映射一旦为空，管理员自己也进不去后台 —— 很难第一时间联想到配置的故障。让默认值兜底、配置只做覆盖，配置写错由启动期校验器拦住。
/// </remarks>
public static class RolePermissionMap
{
    /// <summary>出厂默认的角色 → 权限映射。每次调用返回一份新字典，调用方可自由改写；角色名比较不区分大小写（与 Identity 的角色名查找保持一致）。</summary>
    public static Dictionary<string, string[]> CreateDefaultRoles() => new(StringComparer.OrdinalIgnoreCase)
    {
        [AuthHubConstants.Roles.Administrator] =
        [
            AuthHubConstants.Permissions.ClientsManage,
            AuthHubConstants.Permissions.ScopesManage,
            AuthHubConstants.Permissions.ResourcesManage,
            AuthHubConstants.Permissions.UsersManage,
            AuthHubConstants.Permissions.RolesManage,
            AuthHubConstants.Permissions.TokensRevoke,
            AuthHubConstants.Permissions.AuditRead
        ],

        [AuthHubConstants.Roles.UserManager] =
        [
            AuthHubConstants.Permissions.UsersManage,
            AuthHubConstants.Permissions.TokensRevoke
        ],

        [AuthHubConstants.Roles.Auditor] =
        [
            AuthHubConstants.Permissions.AuditRead
        ],

        // 显式列出：普通用户不拥有任何管理权限
        [AuthHubConstants.Roles.User] = []
    };
}
