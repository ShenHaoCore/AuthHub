using System.Security.Claims;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using AuthHub.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AuthHub.Infrastructure.Identity;

/// <summary>
/// 自定义 ClaimsPrincipal 工厂：在 Identity 默认声明（nameidentifier / name / email / role）
/// 之外，追加两类声明，让会话 Cookie 与令牌使用同一套身份：
///   1) <c>authhub:display_name</c> —— 用户显示名；
///   2) <c>authhub:permission</c> —— 由角色展开出的细粒度权限
///      （见 <see cref="IRolePermissionMap"/>，数据来自配置 AuthHub:RolePermissions）。
///
/// 这样 RBAC 策略既可以在 IdP 内部用 Cookie 会话判断，
/// 也可以在下游资源服务器上仅凭令牌内的 permission 声明判断。
/// </summary>
public sealed class ApplicationUserClaimsPrincipalFactory
    : UserClaimsPrincipalFactory<ApplicationUser, ApplicationRole>
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly IRolePermissionMap _rolePermissions;

    public ApplicationUserClaimsPrincipalFactory(
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        IOptions<IdentityOptions> optionsAccessor,
        IRolePermissionMap rolePermissions)
        : base(userManager, roleManager, optionsAccessor)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _rolePermissions = rolePermissions;
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            identity.AddClaim(new Claim(AuthHubConstants.ClaimTypes.DisplayName, user.DisplayName));
        }

        var roles = await _userManager.GetRolesAsync(user);
        foreach (var permission in _rolePermissions.ResolvePermissions(roles))
        {
            identity.AddClaim(new Claim(AuthHubConstants.ClaimTypes.Permission, permission));
        }

        // 同步角色描述里声明的权限（若角色本身配置了 RoleClaim），Identity 的 base 已处理 role 声明，
        // 这里只需保证 RoleManager 被真正使用，避免某些部署下 RoleClaim 未生效。
        foreach (var roleName in roles)
        {
            var role = await _roleManager.FindByNameAsync(roleName);
            if (role is null) continue;

            var roleClaims = await _roleManager.GetClaimsAsync(role);
            foreach (var claim in roleClaims.Where(c => c.Type == AuthHubConstants.ClaimTypes.Permission))
            {
                if (!identity.HasClaim(claim.Type, claim.Value))
                {
                    identity.AddClaim(new Claim(claim.Type, claim.Value));
                }
            }
        }

        return identity;
    }
}
