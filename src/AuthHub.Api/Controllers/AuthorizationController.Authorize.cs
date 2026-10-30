using AuthHub.Application.Interfaces;
using AuthHub.Api.Pages;
using AuthHub.Domain.Enums;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthHub.Api.Controllers;

/// <summary>
/// /connect/authorize 授权端点（AuthorizationController 的 partial 部分）。
/// GET 用于发起授权，POST 用于提交同意页（同意 / 拒绝）。
/// </summary>
public partial class AuthorizationController
{
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
            if (request.HasPromptValue(PromptValues.None))
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

            if (request.HasPromptValue(PromptValues.None))
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
                if (request.HasPromptValue(PromptValues.None))
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
        //
        // 把请求里的 resource 一并传下去：授权码里会记住这些受众，兑换令牌时才能原样落到 aud
        //（MCP 的授权规范要求令牌的 aud 就是 MCP Server 自己的地址）。
        return await SignInWithUserAsync(user, request.GetScopes(), request.GetResources(), cancellationToken);
    }
}
