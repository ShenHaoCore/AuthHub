using System.Security.Claims;
using AuthHub.Domain.Constants;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthHub.Api.Controllers;

/// <summary>
/// /connect/userinfo 与 /connect/logout（AuthorizationController 的 partial 部分）。
/// </summary>
public partial class AuthorizationController
{
    /// <summary>返回当前 Access Token 对应用户的信息（OIDC UserInfo）。</summary>
    [EndpointSummary("用户信息端点")]
    [EndpointDescription("按 Access Token 的 Scope 返回对应用户声明（profile / email / roles）。")]
    [HttpGet("~/connect/userinfo")]
    [HttpPost("~/connect/userinfo")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
    [Produces("application/json")]
    public async Task<IActionResult> Userinfo()
    {
        var subject = User.FindFirst(Claims.Subject)?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(subject))
        {
            return Challenge(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        }

        var user = await _userManager.FindByIdAsync(subject);
        if (user is null || !user.IsActive)
        {
            return Challenge(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        }

        var claims = new Dictionary<string, object>
        {
            [Claims.Subject] = user.Id
        };

        if (User.HasScope(AuthHubConstants.Scopes.Profile))
        {
            claims[Claims.Name] = user.DisplayName;
            claims[Claims.PreferredUsername] = user.UserName ?? string.Empty;
            claims[Claims.UpdatedAt] = user.CreatedAt.ToUnixTimeSeconds();
        }

        if (User.HasScope(AuthHubConstants.Scopes.Email) && !string.IsNullOrWhiteSpace(user.Email))
        {
            claims[Claims.Email] = user.Email;
            claims[Claims.EmailVerified] = user.EmailConfirmed;
        }

        if (User.HasScope(AuthHubConstants.Scopes.Roles))
        {
            claims[Claims.Role] = (await _userManager.GetRolesAsync(user)).ToArray();

            var permissions = _rolePermissions.ResolvePermissions(claims[Claims.Role] as string[] ?? Array.Empty<string>());
            claims[AuthHubConstants.ClaimTypes.Permission] = permissions.ToArray();
        }

        return Ok(claims);
    }

    /// <summary>
    /// 登出：清除本服务的 Identity 会话；若请求带了 post_logout_redirect_uri，
    /// 由 OpenIddict 校验白名单后跳回客户端。
    /// </summary>
    [EndpointSummary("OIDC 登出端点")]
    [EndpointDescription("清除本服务会话；带 post_logout_redirect_uri 时按白名单跳回客户端。")]
    [HttpGet("~/connect/logout")]
    [HttpPost("~/connect/logout")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _accountService.SignOutAsync(HttpContext.RequestAborted);

        var request = HttpContext.GetOpenIddictServerRequest();

        if (request is null || string.IsNullOrEmpty(request.PostLogoutRedirectUri))
        {
            return Redirect("/account/loggedout");
        }

        return SignOut(
            new AuthenticationProperties { RedirectUri = request.PostLogoutRedirectUri },
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}
