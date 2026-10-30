using System.Security.Claims;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Enums;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthHub.Api.Controllers;

/// <summary>
/// /connect/token 令牌端点（AuthorizationController 的 partial 部分），支持授权码、刷新令牌、客户端凭证与密码流程。
/// </summary>
public partial class AuthorizationController
{
    /// <summary>令牌端点。本服务开启了 token 端点 passthrough（Program.cs）：OpenIddict 先校验完请求（客户端认证、PKCE、code / refresh_token 有效性、scope 合法性），再交回应用签发。</summary>
    /// <remarks>
    /// - 因此每个 grant_type 都必须在此显式 SignIn 一个主体，否则请求以空响应落到 MVC（表现为 unsupported_grant_type）。
    /// - 额外收益：兑换与刷新时都能重新检查账号状态（停用 / 锁定 / 需要重新验证），不无条件信任授权时捕获的声明。
    /// </remarks>
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

        // 用最新的用户资料重建主体，使角色 / 权限变更立即反映到新令牌里。
        // 受众一致性（RFC 8707）：受众在授权那一刻定死，兑换与刷新都不能改口径 ——
        // 授权码兑换（§4）请求带 resource 时必须与原授权集合完全一致；刷新（§5）只允许收窄（请求值是原受众的子集）。
        // 否则一个同时持有两个资源权限的客户端，可以用为 A 签发的刷新令牌换出 aud={A,B} 的访问令牌，受众绑定这条 MCP 防线就出现回退。
        var requestedResources = request.GetResources()
            .Where(resource => !string.IsNullOrWhiteSpace(resource))
            .ToHashSet(StringComparer.Ordinal);
        var originalResources = result.Principal.GetResources()
            .ToHashSet(StringComparer.Ordinal);

        if (requestedResources.Count > 0)
        {
            var acceptable = request.IsRefreshTokenGrantType()
                ? requestedResources.IsSubsetOf(originalResources)
                : requestedResources.SetEquals(originalResources);

            if (!acceptable)
            {
                await _audit.LogAsync(
                    new AuditEntry(
                        AuditActionType.TokenRefreshed,
                        false,
                        user.Id,
                        user.UserName,
                        request.ClientId,
                        $"请求的受众与原授权不一致，已拒绝（RFC 8707）：请求 [{string.Join(", ", requestedResources)}]，原授权 [{string.Join(", ", originalResources)}]"),
                    cancellationToken);

                return ForbidWithError(
                    Errors.InvalidGrant,
                    "请求的受众与原授权不一致：兑换须与授权请求一致，刷新只允许收窄（RFC 8707）。");
            }
        }

        // 常规路径（请求不带 resource）沿用原受众：否则兑换出的令牌没有 aud，MCP Server 会直接拒收。
        var resources = requestedResources.Count > 0 ? requestedResources : originalResources;

        return await SignInWithUserAsync(user, scopes, resources, cancellationToken);
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
        await AttachScopesAndDestinationsAsync(principal, request.GetScopes(), request.GetResources(), cancellationToken);

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

    /// <summary>资源所有者密码流程，默认关闭（AuthHub:Features:EnablePasswordFlow）；仅作内部系统 / 旧系统迁移过渡 —— 它把用户密码交给了客户端。</summary>
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

        return await SignInWithUserAsync(user, request.GetScopes(), request.GetResources(), cancellationToken);
    }
}
