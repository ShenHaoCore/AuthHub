namespace AuthHub.Domain.Enums;

/// <summary>
/// 审计动作类型。用字符串常量而非 enum 存储，
/// 便于查询过滤与后续扩展（无需数据库迁移）。
/// </summary>
public static class AuditActionType
{
    // 账号
    public const string UserRegistered = "user.registered";
    public const string UserLoginSucceeded = "user.login.succeeded";
    public const string UserLoginFailed = "user.login.failed";
    public const string UserLogout = "user.logout";
    public const string UserPasswordChanged = "user.password.changed";
    public const string UserLockedOut = "user.locked_out";

    /// <summary>通过 GitHub / Google 等第三方身份提供方完成登录。</summary>
    public const string UserExternalLoginSucceeded = "user.login.external.succeeded";

    /// <summary>第三方登录被拒绝（账号停用、邮箱未验证、绑定冲突等）。</summary>
    public const string UserExternalLoginFailed = "user.login.external.failed";

    // MFA
    public const string TwoFactorEnabled = "mfa.enabled";
    public const string TwoFactorDisabled = "mfa.disabled";
    public const string TwoFactorFailed = "mfa.failed";
    public const string TwoFactorRecoveryCodeUsed = "mfa.recovery_code.used";

    // 令牌
    public const string TokenIssued = "token.issued";
    public const string TokenRefreshed = "token.refreshed";
    public const string TokenRevoked = "token.revoked";
    public const string ConsentGranted = "consent.granted";
    public const string ConsentDenied = "consent.denied";
    public const string ConsentRevoked = "consent.revoked";

    // 客户端 / Scope
    public const string ClientCreated = "client.created";
    public const string ClientUpdated = "client.updated";
    public const string ClientDeleted = "client.deleted";
    public const string ScopeCreated = "scope.created";
    public const string ScopeUpdated = "scope.updated";
    public const string ScopeDeleted = "scope.deleted";

    // 用户 / 角色管理
    public const string UserCreatedByAdmin = "admin.user.created";
    public const string UserUpdatedByAdmin = "admin.user.updated";
    public const string UserDeletedByAdmin = "admin.user.deleted";
    public const string RoleAssigned = "admin.role.assigned";
    public const string RoleRevoked = "admin.role.revoked";
    public const string RoleCreated = "admin.role.created";
    public const string RoleUpdated = "admin.role.updated";
    public const string RoleDeleted = "admin.role.deleted";

    /// <summary>后台修改了角色的权限归属（策略层的运行时覆盖）。</summary>
    public const string RolePermissionsUpdated = "admin.role.permissions.updated";

    /// <summary>后台删掉了角色的权限覆盖，回落配置 / 出厂默认。</summary>
    public const string RolePermissionsReset = "admin.role.permissions.reset";
}
