using System.Security.Claims;
using AuthHub.Application.Interfaces;
using AuthHub.Api.Extensions;
using AuthHub.Api.Pages;
using AuthHub.Domain.Constants;
using AuthHub.Domain.Entities;
using AuthHub.Domain.Enums;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

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
/// </summary>
[Tags("OIDC 协议")]
public class AuthorizationController : Controller
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

    public AuthorizationController(
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictScopeManager scopeManager,
        IConsentService consentService,
        IAuditLogService audit,
        IAccountService accountService,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IAntiforgery antiforgery,
        IConfiguration configuration)
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
    }

    // ------------------------------------------------------------------ 授权端点

    /// <summary>
    /// 授权端点。GET 用于发起授权，POST 用于提交同意页（同意 / 拒绝）。
    ///
    /// 这里忽略防伪令牌是与 OpenIddict 官方实践一致的：
    /// 授权请求的合法性由 redirect_uri 白名单 + state + PKCE 保证，
    /// 而同意表单回传的是完整原始请求参数，攻击者无法凭空构造。
    /// 登录表单（/account/login）则强制校验防伪令牌，见 AccountController。
    /// </summary>
    [EndpointSummary("授权端点")]
    [EndpointDescription("GET 发起授权；POST 提交同意页（同意或拒绝）。未登录会跳转登录页。")]
    [HttpGet("~/connect/authorize")]
    [HttpPost("~/connect/authorize")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Authorize()
    {
        var cancellationToken = HttpContext.RequestAborted;

        var request = HttpContext.GetOpenIddictServerRequest();
        if (request is null)
        {
            return OAuthError(Errors.InvalidRequest, "无法解析 OpenID Connect 授权请求。");
        }

        // 1) 是否已有 Identity 会话（SSO 的关键：有会话就不再要求输入凭证）
        var authentication = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!authentication.Succeeded || authentication.Principal is null)
        {
            if (request.HasPrompt(Prompts.None))
            {
                // prompt=none 表示调用方要求“静默授权”，未登录时必须显式报错而不是弹登录页
                return ForbidWithError(Errors.LoginRequired, "用户尚未登录，无法静默完成授权。");
            }

            return RedirectToLogin();
        }

        var user = await _userManager.GetUserAsync(authentication.Principal);
        if (user is null || !user.IsActive)
        {
            await _signInManager.SignOutAsync();

            if (request.HasPrompt(Prompts.None))
            {
                return ForbidWithError(Errors.LoginRequired, "账号不存在或已被停用。");
            }

            return RedirectToLogin();
        }

        // 2) 用户在同意页点了“拒绝”
        if (Request.HasFormContentType && Request.Form.ContainsKey("submit.Deny"))
        {
            await _audit.LogAsync(
                new AuditEntry(AuditActionType.ConsentDenied, false, user.Id, user.UserName, request.ClientId, "用户在同意页拒绝授权"),
                cancellationToken);

            return ForbidWithError(Errors.AccessDenied, "用户拒绝了本次授权请求。");
        }

        var application = await _applicationManager.FindByClientIdAsync(request.ClientId!, cancellationToken);
        if (application is null)
        {
            return OAuthError(Errors.InvalidRequest, $"未知的 client_id：{request.ClientId}。");
        }

        // 3) 同意逻辑：only explicit 客户端才需要用户确认
        var consentType = await _applicationManager.GetConsentTypeAsync(application, cancellationToken);
        var requireConsent = string.Equals(consentType, ConsentTypes.Explicit, StringComparison.Ordinal);
        var grantedByForm = Request.HasFormContentType && Request.Form.ContainsKey("submit.Accept");

        if (requireConsent && !grantedByForm)
        {
            var promptResult = await _consentService.GetPromptAsync(user.Id, request.ClientId!, request.GetScopes().ToArray(), cancellationToken);
            if (promptResult.IsFailure)
            {
                return OAuthError(Errors.InvalidRequest, promptResult.Error.Message);
            }

            var prompt = promptResult.Value;

            // 已授权过（存在 permanent authorization）则直接跳过同意页
            if (!prompt.AlreadyConsented)
            {
                if (request.HasPrompt(Prompts.None))
                {
                    // prompt=none 表示调用方要求“静默完成”。此时既不能弹登录页（上面已判断），
                    // 也不能弹同意页，必须按 OIDC 规范显式报 consent_required。
                    return ForbidWithError(Errors.ConsentRequired, "用户尚未授予该应用所需的权限，无法静默完成授权。");
                }

                var parameters = (Request.HasFormContentType ? Request.Form : (IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>>)Request.Query)
                    .ToDictionary(kv => kv.Key, kv => kv.Value.ToString());

                var requestToken = _antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? string.Empty;

                return Content(
                    HtmlPages.ConsentPage(
                        prompt.ClientDisplayName ?? request.ClientId!,
                        prompt.ScopeDetails,
                        parameters,
                        requestToken,
                        user.DisplayName),
                    "text/html; charset=utf-8");
            }
        }
        else if (requireConsent && grantedByForm)
        {
            await _consentService.GrantAsync(
                authentication.Principal,
                user.Id,
                request.ClientId!,
                request.GetScopes().ToArray(),
                cancellationToken);
        }

        // 4) 签发授权码 / 令牌
        return await SignInWithUserAsync(user, request.GetScopes(), cancellationToken);
    }

    // ------------------------------------------------------------------ 令牌端点

    /// <summary>
    /// 令牌端点。
    ///
    /// 为什么每个 grant_type 都要在这里收尾：本服务开启了 token 端点的 passthrough
    /// （Program.cs 的 EnableTokenEndpointPassthrough）。passthrough 的准确语义是
    /// 「OpenIddict 先把请求校验完（客户端认证、PKCE、code / refresh_token 有效性、scope 合法性），
    /// 再把请求交回应用，由应用决定用哪个主体签发令牌」——
    /// 因此这里必须为每个允许的流程显式 SignIn 一个 principal，
    /// 否则请求会以空响应落到 MVC（表现就是 unsupported_grant_type）。
    ///
    /// 这样安排还有额外收益：授权码兑换与刷新令牌时都能重新检查账号状态
    /// （是否被停用 / 锁定 / 需要重新验证），而不是无条件相信当初授权时捕获的声明。
    /// </summary>
    [EndpointSummary("令牌端点")]
    [EndpointDescription("支持授权码、刷新令牌、客户端凭证与密码流程（密码流程默认关闭）。")]
    [HttpPost("~/connect/token")]
    [IgnoreAntiforgeryToken]
    [Produces("application/json")]
    public async Task<IActionResult> Exchange()
    {
        var cancellationToken = HttpContext.RequestAborted;

        var request = HttpContext.GetOpenIddictServerRequest();
        if (request is null)
        {
            return OAuthError(Errors.InvalidRequest, "无法解析令牌请求。");
        }

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            return await ExchangeStoredGrantAsync(request, cancellationToken);
        }

        if (request.IsClientCredentialsGrantType())
        {
            return await ClientCredentialsGrantAsync(request, cancellationToken);
        }

        if (request.IsPasswordGrantType())
        {
            return await PasswordGrantAsync(request, cancellationToken);
        }

        return OAuthError(Errors.UnsupportedGrantType, $"不支持的 grant_type：{request.GrantType}。");
    }

    /// <summary>
    /// 授权码兑换与刷新令牌共用同一条路径 ——
    /// 用 OpenIddict 的 Server 方案取回「与本次 code / refresh_token 关联的主体」
    /// （即当初在 /connect/authorize 阶段捕获并随授权一起持久化的声明）。
    /// </summary>
    private async Task<IActionResult> ExchangeStoredGrantAsync(
        OpenIddictRequest request,
        CancellationToken cancellationToken)
    {
        var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal is null)
        {
            return ForbidWithError(Errors.InvalidGrant, "授权已失效，请重新登录并授权。");
        }

        var subject = result.Principal.GetClaim(Claims.Subject);
        var user = string.IsNullOrEmpty(subject) ? null : await _userManager.FindByIdAsync(subject);

        if (user is null || !user.IsActive)
        {
            await _audit.LogAsync(
                new AuditEntry(
                    AuditActionType.TokenRefreshed,
                    false,
                    subject,
                    UserName: null,
                    ClientId: request.ClientId,
                    Details: "账号不存在或已被停用，拒绝签发令牌"),
                cancellationToken);

            return ForbidWithError(Errors.InvalidGrant, "账号不存在或已被停用。");
        }

        // 账号可能在授权之后被锁定或变得不允许登录，这里再确认一次
        if (_userManager.SupportsUserLockout && await _userManager.IsLockedOutAsync(user))
        {
            return ForbidWithError(Errors.InvalidGrant, "账号已被临时锁定，请稍后重试。");
        }

        if (!await _signInManager.CanSignInAsync(user))
        {
            return ForbidWithError(Errors.InvalidGrant, "账号当前不允许登录。");
        }

        // Scope 必须沿用原授权：code / refresh_token 请求本身不带 scope 参数
        var scopes = result.Principal.GetScopes();

        if (request.IsRefreshTokenGrantType())
        {
            await _audit.LogAsync(
                new AuditEntry(
                    AuditActionType.TokenRefreshed,
                    true,
                    user.Id,
                    user.UserName,
                    request.ClientId,
                    "刷新令牌换取新的访问令牌"),
                cancellationToken);
        }

        // 用最新的用户资料重建主体，使角色 / 权限变更立即反映到新令牌里
        return await SignInWithUserAsync(user, scopes, cancellationToken);
    }

    /// <summary>客户端凭证流程（M2M）：没有用户参与，主体就是客户端自身。</summary>
    private async Task<IActionResult> ClientCredentialsGrantAsync(
        OpenIddictRequest request,
        CancellationToken cancellationToken)
    {
        var application = await _applicationManager.FindByClientIdAsync(request.ClientId!, cancellationToken);
        if (application is null)
        {
            return ForbidWithError(Errors.InvalidClient, $"未知的 client_id：{request.ClientId}。");
        }

        var displayName = await _applicationManager.GetDisplayNameAsync(application, cancellationToken);

        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        // 机器身份没有用户名，以 client_id 作为主体标识（令牌里的 sub）
        identity.AddClaim(new Claim(Claims.Subject, request.ClientId!));
        identity.AddClaim(new Claim(
            Claims.Name,
            string.IsNullOrWhiteSpace(displayName) ? request.ClientId! : displayName));

        var principal = new ClaimsPrincipal(identity);
        await AttachScopesAndDestinationsAsync(principal, request.GetScopes(), cancellationToken);

        await _audit.LogAsync(
            new AuditEntry(
                AuditActionType.TokenIssued,
                true,
                UserId: null,
                UserName: request.ClientId,
                ClientId: request.ClientId,
                Details: "客户端凭证流程签发访问令牌"),
            cancellationToken);

        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// 资源所有者密码流程。默认关闭（AuthHub:Features:EnablePasswordFlow），
    /// 仅用于内部系统或从旧系统迁移的过渡期 —— 它把用户密码交给了客户端。
    /// </summary>
    private async Task<IActionResult> PasswordGrantAsync(
        OpenIddictRequest request,
        CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue("AuthHub:Features:EnablePasswordFlow", false))
        {
            return ForbidWithError(Errors.UnsupportedGrantType, "本服务已禁用资源所有者密码流程，请使用授权码 + PKCE。");
        }

        var identifier = request.Username ?? string.Empty;
        var user = identifier.Contains('@', StringComparison.Ordinal)
            ? await _userManager.FindByEmailAsync(identifier)
            : await _userManager.FindByNameAsync(identifier);

        if (user is null || !user.IsActive)
        {
            await _audit.LogLoginFailedAsync(null, identifier, "password 流程：用户不存在或已停用", cancellationToken);
            return ForbidWithError(Errors.InvalidGrant, "用户名或密码错误。");
        }

        var signIn = await _signInManager.CheckPasswordSignInAsync(user, request.Password ?? string.Empty, lockoutOnFailure: true);

        if (signIn.IsLockedOut)
        {
            return ForbidWithError(Errors.InvalidGrant, "登录失败次数过多，账号已被临时锁定。");
        }

        if (signIn.RequiresTwoFactor)
        {
            return ForbidWithError(Errors.InvalidGrant, "该账号启用了两步验证，无法使用 password 流程，请改用授权码流程。");
        }

        if (!signIn.Succeeded)
        {
            await _audit.LogLoginFailedAsync(user.Id, user.UserName ?? user.Id, "password 流程：密码错误", cancellationToken);
            return ForbidWithError(Errors.InvalidGrant, "用户名或密码错误。");
        }

        await _audit.LogLoginSucceededAsync(user.Id, user.UserName ?? user.Id, cancellationToken: cancellationToken);

        return await SignInWithUserAsync(user, request.GetScopes(), cancellationToken);
    }

    // ------------------------------------------------------------------ 用户信息端点

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

            var permissions = RolePermissionMap.ResolvePermissions(claims[Claims.Role] as string[] ?? Array.Empty<string>());
            claims[AuthHubConstants.ClaimTypes.Permission] = permissions.ToArray();
        }

        return Ok(claims);
    }

    // ------------------------------------------------------------------ 登出端点

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

    // ------------------------------------------------------------------ 内部辅助

    /// <summary>
    /// 以指定 scope 为用户签发授权码 / 令牌。
    ///
    /// 每次都用 <see cref="SignInManager{TUser}.CreateUserPrincipalAsync"/> 重新构造主体，
    /// 而不是复用旧声明 —— 这样角色与权限的变更会立即体现在新令牌里。
    /// </summary>
    private async Task<IActionResult> SignInWithUserAsync(
        ApplicationUser user,
        IEnumerable<string> scopes,
        CancellationToken cancellationToken)
    {
        var principal = await _signInManager.CreateUserPrincipalAsync(user);
        await AttachScopesAndDestinationsAsync(principal, scopes, cancellationToken);

        // 以 OpenIddict 的 Server 方案登录：OpenIddict 会据此签发授权码 / 令牌
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// 把 Scope 与对应的 API 资源挂到 principal 上，并为每条声明标注“进入哪个令牌”。
    /// 不标注目标的声明不会出现在任何令牌里（OpenIddict 的默认安全策略）。
    /// </summary>
    private async Task AttachScopesAndDestinationsAsync(
        ClaimsPrincipal principal,
        IEnumerable<string> scopes,
        CancellationToken cancellationToken)
    {
        principal.SetScopes(scopes);

        var resources = new List<string>();
        await foreach (var resource in _scopeManager.ListResourcesAsync(principal.GetScopes(), cancellationToken))
        {
            resources.Add(resource);
        }

        if (resources.Count > 0)
        {
            principal.SetResources(resources);
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
