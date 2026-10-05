namespace AuthHub.Domain.Constants;

/// <summary>
/// 全解决方案共享的常量。放在 Domain 层，使 Application / Infrastructure / Api
/// 不必各自硬编码字符串，也避免为此引用 OpenIddict 包。
/// </summary>
public static class AuthHubConstants
{
    /// <summary>客户端 / 令牌相关的自定义声明类型。</summary>
    public static class ClaimTypes
    {
        /// <summary>细粒度权限声明（由角色推导）。</summary>
        public const string Permission = "authhub:permission";

        /// <summary>用户显示名（与会话 Cookie 里的 name 保持一致）。</summary>
        public const string DisplayName = "authhub:display_name";
    }

    /// <summary>本服务对外提供的 Scope（与 OpenIddictScope 表中的 Name 一一对应）。</summary>
    public static class Scopes
    {
        /// <summary>OIDC 必备 Scope，OpenIddict 内部处理，不落 OpenIddictScope 表。</summary>
        public const string OpenId = "openid";

        /// <summary>请求刷新令牌时使用，OpenIddict 内部处理。</summary>
        public const string OfflineAccess = "offline_access";

        public const string Profile = "profile";
        public const string Email = "email";
        public const string Roles = "roles";
        public const string ApiRead = "api:read";
        public const string ApiWrite = "api:write";
        public const string Admin = "authhub:admin";
    }

    /// <summary>API 资源标识（会写进令牌的 aud 声明）。</summary>
    public static class Resources
    {
        public const string Api = "authhub-api";
    }

    /// <summary>内置角色名。</summary>
    public static class Roles
    {
        public const string Administrator = "Administrator";
        public const string UserManager = "UserManager";
        public const string Auditor = "Auditor";
        public const string User = "User";

        public static readonly IReadOnlyList<string> All = new[]
        {
            Administrator, UserManager, Auditor, User
        };
    }

    /// <summary>细粒度权限标识。</summary>
    public static class Permissions
    {
        public const string ClientsManage = "clients.manage";
        public const string ScopesManage = "scopes.manage";
        public const string UsersManage = "users.manage";
        public const string RolesManage = "roles.manage";
        public const string TokensRevoke = "tokens.revoke";
        public const string AuditRead = "audit.read";

        public static readonly IReadOnlyList<string> All = new[]
        {
            ClientsManage, ScopesManage, UsersManage, RolesManage, TokensRevoke, AuditRead
        };
    }

    /// <summary>授权策略名。控制器通过 [Authorize(Policy = ...)] 引用。</summary>
    public static class Policies
    {
        /// <summary>需要任意一个管理权限。</summary>
        public const string Admin = "AuthHub.Admin";

        /// <summary>仅接受 Access Token（Bearer）访问的 API 策略。</summary>
        public const string ApiAccess = "AuthHub.ApiAccess";

        public const string ClientsManage = "AuthHub.Permission." + Permissions.ClientsManage;
        public const string ScopesManage = "AuthHub.Permission." + Permissions.ScopesManage;
        public const string UsersManage = "AuthHub.Permission." + Permissions.UsersManage;
        public const string RolesManage = "AuthHub.Permission." + Permissions.RolesManage;
        public const string TokensRevoke = "AuthHub.Permission." + Permissions.TokensRevoke;
        public const string AuditRead = "AuthHub.Permission." + Permissions.AuditRead;
    }

    /// <summary>开发环境的种子客户端标识。</summary>
    public static class SeedClients
    {
        public const string Spa = "spa-client";
        public const string SpaSecret = "spa-secret";
        public const string Web = "web-client";
        public const string WebSecret = "web-secret";
        public const string M2M = "m2m-service";
        public const string M2MSecret = "m2m-secret";
    }

    /// <summary>开发环境的种子账号。</summary>
    public static class SeedUsers
    {
        public const string AdminUserName = "admin";
        public const string AdminEmail = "admin@authhub.local";
        public const string AdminPassword = "Admin@12345";
        public const string DemoUserName = "alice";
        public const string DemoEmail = "alice@authhub.local";
        public const string DemoPassword = "Alice@12345";
    }
}
