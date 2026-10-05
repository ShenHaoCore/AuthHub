using AuthHub.Domain.Constants;
using OpenIddict.Abstractions;

namespace AuthHub.Api.Extensions;

/// <summary>
/// 授权策略定义。
///
/// 管理类接口同时接受两种身份：
///   - 浏览器里的管理员（Identity 会话 Cookie）；
///   - 持有 Access Token 的调用方（例如运维脚本、网关）。
/// 因此策略里同时挂上两个认证方案，命中任意一个即可通过。
/// </summary>
public static class AuthorizationPolicyExtensions
{
    public static IServiceCollection AddAuthHubAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            // 下游 API：需要 api:read 或 api:write 之一
            options.AddPolicy(AuthHubConstants.Policies.ApiAccess, policy => policy
                .AddAuthenticationSchemes(AuthHubSchemes.Bearer, AuthHubSchemes.Cookie)
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                    context.User.HasScope(AuthHubConstants.Scopes.ApiRead) ||
                    context.User.HasScope(AuthHubConstants.Scopes.ApiWrite)));

            // 管理后台：拥有任意一个管理权限
            options.AddPolicy(AuthHubConstants.Policies.Admin, policy => policy
                .AddAuthenticationSchemes(AuthHubSchemes.Cookie, AuthHubSchemes.Bearer)
                .RequireAuthenticatedUser()
                .RequireClaim(AuthHubConstants.ClaimTypes.Permission, AuthHubConstants.Permissions.All.ToArray()));

            // 细粒度权限策略
            AddPermissionPolicy(options, AuthHubConstants.Policies.ClientsManage, AuthHubConstants.Permissions.ClientsManage);
            AddPermissionPolicy(options, AuthHubConstants.Policies.ScopesManage, AuthHubConstants.Permissions.ScopesManage);
            AddPermissionPolicy(options, AuthHubConstants.Policies.UsersManage, AuthHubConstants.Permissions.UsersManage);
            AddPermissionPolicy(options, AuthHubConstants.Policies.RolesManage, AuthHubConstants.Permissions.RolesManage);
            AddPermissionPolicy(options, AuthHubConstants.Policies.TokensRevoke, AuthHubConstants.Permissions.TokensRevoke);
            AddPermissionPolicy(options, AuthHubConstants.Policies.AuditRead, AuthHubConstants.Permissions.AuditRead);

            // ---- 管理后台页面（Razor Pages）：只挂会话 Cookie，见 Ui 策略的说明 ----

            options.AddPolicy(AuthHubConstants.Policies.Ui.Authenticated, policy => policy
                .AddAuthenticationSchemes(AuthHubSchemes.Cookie)
                .RequireAuthenticatedUser());

            options.AddPolicy(AuthHubConstants.Policies.Ui.Admin, policy => policy
                .AddAuthenticationSchemes(AuthHubSchemes.Cookie)
                .RequireAuthenticatedUser()
                .RequireClaim(AuthHubConstants.ClaimTypes.Permission, AuthHubConstants.Permissions.All.ToArray()));

            AddUiPermissionPolicy(options, AuthHubConstants.Policies.Ui.ClientsManage, AuthHubConstants.Permissions.ClientsManage);
            AddUiPermissionPolicy(options, AuthHubConstants.Policies.Ui.ScopesManage, AuthHubConstants.Permissions.ScopesManage);
            AddUiPermissionPolicy(options, AuthHubConstants.Policies.Ui.UsersManage, AuthHubConstants.Permissions.UsersManage);
            AddUiPermissionPolicy(options, AuthHubConstants.Policies.Ui.RolesManage, AuthHubConstants.Permissions.RolesManage);
            AddUiPermissionPolicy(options, AuthHubConstants.Policies.Ui.TokensRevoke, AuthHubConstants.Permissions.TokensRevoke);
            AddUiPermissionPolicy(options, AuthHubConstants.Policies.Ui.AuditRead, AuthHubConstants.Permissions.AuditRead);
        });

        return services;
    }

    /// <summary>后台页面策略：会话 Cookie + 指定权限声明。</summary>
    private static void AddUiPermissionPolicy(
        Microsoft.AspNetCore.Authorization.AuthorizationOptions options,
        string policyName,
        string permission)
    {
        options.AddPolicy(policyName, policy => policy
            .AddAuthenticationSchemes(AuthHubSchemes.Cookie)
            .RequireAuthenticatedUser()
            .RequireClaim(AuthHubConstants.ClaimTypes.Permission, permission));
    }

    private static void AddPermissionPolicy(
        Microsoft.AspNetCore.Authorization.AuthorizationOptions options,
        string policyName,
        string permission)
    {
        options.AddPolicy(policyName, policy => policy
            .AddAuthenticationSchemes(AuthHubSchemes.Cookie, AuthHubSchemes.Bearer)
            .RequireAuthenticatedUser()
            .RequireClaim(AuthHubConstants.ClaimTypes.Permission, permission));
    }
}
