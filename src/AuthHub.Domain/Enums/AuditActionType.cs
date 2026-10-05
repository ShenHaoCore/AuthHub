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
}
