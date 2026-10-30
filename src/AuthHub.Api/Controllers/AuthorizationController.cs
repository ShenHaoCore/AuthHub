using System.Security.Claims;
using AuthHub.Application.Interfaces;
using AuthHub.Api.Extensions;
using AuthHub.Domain.Constants;
using AuthHub.Domain.Entities;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;

namespace AuthHub.Api.Controllers;

/// <summary>
/// OIDC 协议端点。
///
/// 分工：
///   - <c>/connect/token</c>、<c>/connect/introspect</c>、<c>/connect/revoke</c>
///     由 OpenIddict 内置处理器完成（本控制器只在 OpenIddict 无法处理时兜底，例如 password 流程）；
///   - <c>/connect/authorize</c>、<c>/connect/logout</c>、<c>/connect/userinfo</c>
///     需要业务参与（登录、同意、返回用户信息），因此在这里实现。
///
/// 该控制器不加 [ApiController]：它需要返回 HTML（登录/同意页）与 302 重定向，
/// 而不是 ProblemDetails JSON。
///
/// 本类按职责拆为 partial class 多个文件（路由与公开契约完全不变）：
///   - AuthorizationController.cs             主文件：字段、构造函数、共享辅助方法
///   - AuthorizationController.Authorize.cs   /connect/authorize 授权端点
///   - AuthorizationController.Exchange.cs    /connect/token 令牌端点（含三种 grant）
///   - AuthorizationController.UserInfo.cs    /connect/userinfo 与 /connect/logout
/// </summary>
[Tags("OIDC 协议")]
public partial class AuthorizationController : Controller
{
    private readonly IOpenIddictApplicationManager _applicationManager;
    private readonly IOpenIddictScopeManager _scopeManager;
    private readonly IConsentService _consentService;
    private readonly IAuditLogService _audit;
    private readonly IAccountService _accountService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IAntiforgery _antiforgery;
    private readonly IConfiguration _configuration;
    private readonly IRolePermissionMap _rolePermissions;

    public AuthorizationController(
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictScopeManager scopeManager,
        IConsentService consentService,
        IAuditLogService audit,
        IAccountService accountService,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IAntiforgery antiforgery,
        IConfiguration configuration,
        IRolePermissionMap rolePermissions)
    {
        _applicationManager = applicationManager;
        _scopeManager = scopeManager;
        _consentService = consentService;
        _audit = audit;
        _accountService = accountService;
        _userManager = userManager;
        _signInManager = signInManager;
        _antiforgery = antiforgery;
        _configuration = configuration;
        _rolePermissions = rolePermissions;
    }

    // ------------------------------------------------------------------ 内部辅助（被各 partial 文件共用）

    /// <summary>
    /// 以指定 scope 为用户签发授权码 / 令牌。
    ///
    /// 每次都用 <see cref="SignInManager{TUser}.CreateUserPrincipalAsync"/> 重新构造主体，
    /// 而不是复用旧声明 —— 这样角色与权限的变更会立即体现在新令牌里。
    /// </summary>
    private async Task<IActionResult> SignInWithUserAsync(
        ApplicationUser user,
        IEnumerable<string> scopes,
        IEnumerable<string> resources,
        CancellationToken cancellationToken)
    {
        var principal = await _signInManager.CreateUserPrincipalAsync(user);
        await AttachScopesAndDestinationsAsync(principal, scopes, resources, cancellationToken);

        // 以 OpenIddict 的 Server 方案登录：OpenIddict 会据此签发授权码 / 令牌
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// 把 Scope / 资源挂到 principal 上，并为每条声明标注"进入哪个令牌"。
    /// 不标注目标的声明不会出现在任何令牌里（OpenIddict 的默认安全策略）。
    /// </summary>
    /// <remarks>
    /// <c>resources</c>（RFC 8707）**非空时只认它**，不再回头叠加 Scope 关联的资源。
    ///
    /// 这个"二选一"是有意的：MCP 这类场景下，客户端声明 <c>resource=https://mcp.example.com/mcp</c>
    /// 是在说"这张票只给那个服务器用"。如果这时还顺手把 Scope 的 <c>authhub-api</c> 塞进 aud，
    /// 令牌就同时是 AuthHub API 的通行证 —— MCP Server 一旦被攻破，攻击者可以拿用户的票
    /// 直接调 AuthHub 的管理接口。受众收窄才是 resource 参数存在的意义。
    ///
    /// 为空时退回旧行为（按 Scope 关联资源算 aud），既有客户端不受影响。
    /// </remarks>
    private async Task AttachScopesAndDestinationsAsync(
        ClaimsPrincipal principal,
        IEnumerable<string> scopes,
        IEnumerable<string> resources,
        CancellationToken cancellationToken)
    {
        principal.SetScopes(scopes);

        var requested = resources
            .Where(resource => !string.IsNullOrWhiteSpace(resource))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (requested.Length > 0)
        {
            principal.SetResources(requested);
        }
        else
        {
            var inherited = new List<string>();
            await foreach (var resource in _scopeManager.ListResourcesAsync(principal.GetScopes(), cancellationToken))
            {
                inherited.Add(resource);
            }

            if (inherited.Count > 0)
            {
                principal.SetResources(inherited);
            }
        }

        foreach (var claim in principal.Claims)
        {
            claim.SetDestinations(OpenIddictClaimDestinations.Resolve(claim, principal));
        }
    }

    private IActionResult RedirectToLogin()
    {
        var returnUrl = Request.PathBase + Request.Path + Request.QueryString;
        return Redirect($"/account/login?returnUrl={Uri.EscapeDataString(returnUrl.ToString())}");
    }

    /// <summary>
    /// OAuth 2.0 规范的错误响应体：<c>{"error": "...", "error_description": "..."}</c>。
    ///
    /// 刻意**不**用 RFC 7807 的 ProblemDetails：这两个字段是规范要求的形状，调用方按
    /// <c>error</c> 的取值分支（ProblemDetails 只服务 <c>/api/*</c> 那套内部约定）。
    /// 收在一个工厂里，避免 5 处匿名对象各自漂移。
    /// </summary>
    private static IActionResult OAuthError(string error, string description)
        => new BadRequestObjectResult(new { error, error_description = description });

    private IActionResult ForbidWithError(string error, string description)
        => Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
            }),
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
}
