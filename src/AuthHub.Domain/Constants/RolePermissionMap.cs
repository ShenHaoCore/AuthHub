namespace AuthHub.Domain.Constants;

/// <summary>
/// 内置角色的**出厂默认**「角色 → 权限」映射。
///
/// 它不再是运行时的唯一事实源：部署时可用配置 <c>AuthHub:RolePermissions</c> 覆盖
/// （见 Application 层的 RolePermissionOptions 与 IRolePermissionMap 契约）。
/// 之所以仍然保留这份默认值，有两个理由：
///   1) 老部署的 appsettings 里没有这一段，出厂默认能让升级前后的行为逐字一致；
///   2) 默认值用 <see cref="AuthHubConstants.Permissions"/> 常量而不是字符串字面量拼出来，
///      将来重命名某个权限时编译器会替我们报错。
///
/// 另一个方向的取舍：**不**采用"配置缺失就启动失败"的做法。
/// 那份映射一旦为空，现象是管理员自己也进不去后台 —— 一个很难第一时间联想到配置的故障。
/// 让默认值兜底、配置只做覆盖，风险面更小；配置写错了则由启动期校验器拦住（非法权限名直接启动失败）。
/// </summary>
public static class RolePermissionMap
{
    /// <summary>
    /// 出厂默认的角色 → 权限映射。每次调用返回一份新的字典，调用方可自由改写。
    /// 角色名比较不区分大小写（与 Identity 的角色名查找保持一致）。
    /// </summary>
    public static Dictionary<string, string[]> CreateDefaultRoles() => new(StringComparer.OrdinalIgnoreCase)
    {
        [AuthHubConstants.Roles.Administrator] =
        [
            AuthHubConstants.Permissions.ClientsManage,
            AuthHubConstants.Permissions.ScopesManage,
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
