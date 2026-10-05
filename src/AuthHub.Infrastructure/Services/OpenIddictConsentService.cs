using System.Collections.Immutable;
using System.Security.Claims;
using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Consents;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using AuthHub.Domain.Enums;
using OpenIddict.Abstractions;
using Oidc = OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthHub.Infrastructure.Services;

/// <summary>
/// 授权同意服务。支撑两件事：
/// 1) 授权端点判断是否需要向用户展示同意页；
/// 2) 用户/管理员查看并解除已授权应用（对应 OpenIddictAuthorizations 表）。
///
/// 内置的 openid / offline_access 不属于 OpenIddictScope 表，
/// 这里给出可读的描述，避免同意页出现“空白权限”。
/// </summary>
public sealed class OpenIddictConsentService : IConsentService
{
    private static readonly Dictionary<string, ConsentScopeDescription> BuiltInScopes = new(StringComparer.Ordinal)
    {
        ["openid"] = new("openid", "身份标识", "以你的身份登录（返回 ID Token）"),
        ["offline_access"] = new("offline_access", "离线访问", "在你不使用应用时也能代表你访问（发放刷新令牌）")
    };

    private readonly IOpenIddictApplicationManager _applicationManager;
    private readonly IOpenIddictAuthorizationManager _authorizationManager;
    private readonly IOpenIddictTokenManager _tokenManager;
    private readonly IOpenIddictScopeManager _scopeManager;
    private readonly IAuditLogService _audit;

    public OpenIddictConsentService(
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictAuthorizationManager authorizationManager,
        IOpenIddictTokenManager tokenManager,
        IOpenIddictScopeManager scopeManager,
        IAuditLogService audit)
    {
        _applicationManager = applicationManager;
        _authorizationManager = authorizationManager;
        _tokenManager = tokenManager;
        _scopeManager = scopeManager;
        _audit = audit;
    }

    public async Task<Result<ConsentPrompt>> GetPromptAsync(
        string subject,
        string clientId,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken = default)
    {
        var application = await _applicationManager.FindByClientIdAsync(clientId, cancellationToken);
        if (application is null)
        {
            return Result.Failure<ConsentPrompt>(Error.NotFound($"客户端 {clientId} 不存在。"));
        }

        var applicationId = await _applicationManager.GetIdAsync(application, cancellationToken) ?? string.Empty;
        var permissions = await _applicationManager.GetPermissionsAsync(application, cancellationToken);
        var allowedScopes = OpenIddictPermissionTranslator.ExtractScopes(permissions)
            .ToHashSet(StringComparer.Ordinal);

        // 只展示“客户端确实被授权可申请”的 Scope，避免同意页泄露本服务支持的全部 Scope
        var requested = scopes
            .Where(s => s == AuthHubConstants.Scopes.OpenId || allowedScopes.Contains(s))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var alreadyConsented = false;
        if (requested.Length > 0)
        {
            await foreach (var _ in _authorizationManager.FindAsync(
                               subject,
                               applicationId,
                               Oidc.Statuses.Valid,
                               Oidc.AuthorizationTypes.Permanent,
                               requested.ToImmutableArray(),
                               cancellationToken))
            {
                alreadyConsented = true;
                break;
            }
        }

        var details = new List<ConsentScopeDescription>(requested.Length);
        foreach (var scope in requested)
        {
            if (BuiltInScopes.TryGetValue(scope, out var builtIn))
            {
                details.Add(builtIn);
                continue;
            }

            var descriptor = await _scopeManager.FindByNameAsync(scope, cancellationToken);
            details.Add(new ConsentScopeDescription(
                scope,
                descriptor is null ? null : await _scopeManager.GetDisplayNameAsync(descriptor, cancellationToken),
                descriptor is null ? null : await _scopeManager.GetDescriptionAsync(descriptor, cancellationToken)));
        }

        return Result.Success(new ConsentPrompt
        {
            ClientId = clientId,
            ClientDisplayName = await _applicationManager.GetDisplayNameAsync(application, cancellationToken),
            Scopes = requested,
            ScopeDetails = details,
            AlreadyConsented = alreadyConsented
        });
    }

    public async Task<Result> GrantAsync(
        ClaimsPrincipal principal,
        string subject,
        string clientId,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken = default)
    {
        var application = await _applicationManager.FindByClientIdAsync(clientId, cancellationToken);
        if (application is null)
        {
            return Result.Failure(Error.NotFound($"客户端 {clientId} 不存在。"));
        }

        var applicationId = await _applicationManager.GetIdAsync(application, cancellationToken) ?? string.Empty;

        await _authorizationManager.CreateAsync(
            principal,
            subject,
            applicationId,
            Oidc.AuthorizationTypes.Permanent,
            scopes.ToImmutableArray(),
            cancellationToken);

        await _audit.LogAsync(
            new AuditEntry(
                AuditActionType.ConsentGranted,
                true,
                subject,
                ClientId: clientId,
                Details: $"授权 Scope：{string.Join(",", scopes)}"),
            cancellationToken);

        return Result.Success();
    }

    public async Task<Result<IReadOnlyCollection<ConsentDto>>> ListBySubjectAsync(
        string subject,
        CancellationToken cancellationToken = default)
    {
        var result = new List<ConsentDto>();

        await foreach (var authorization in _authorizationManager.FindBySubjectAsync(subject, cancellationToken))
        {
            var applicationId = await _authorizationManager.GetApplicationIdAsync(authorization, cancellationToken);

            string? clientId = null;
            string? displayName = null;

            if (!string.IsNullOrEmpty(applicationId))
            {
                var application = await _applicationManager.FindByIdAsync(applicationId, cancellationToken);
                if (application is not null)
                {
                    clientId = await _applicationManager.GetClientIdAsync(application, cancellationToken);
                    displayName = await _applicationManager.GetLocalizedDisplayNameAsync(application, cancellationToken);
                }
            }

            var scopes = await _authorizationManager.GetScopesAsync(authorization, cancellationToken);

            result.Add(new ConsentDto(
                await _authorizationManager.GetIdAsync(authorization, cancellationToken) ?? string.Empty,
                subject,
                clientId ?? applicationId ?? string.Empty,
                displayName,
                scopes.ToArray(),
                await _authorizationManager.GetStatusAsync(authorization, cancellationToken) ?? Oidc.Statuses.Valid,
                await _authorizationManager.GetCreationDateAsync(authorization, cancellationToken)));
        }

        return Result.Success<IReadOnlyCollection<ConsentDto>>(
            result.OrderByDescending(c => c.CreatedAt).ToArray());
    }

    public async Task<Result> RevokeAsync(string authorizationId, CancellationToken cancellationToken = default)
    {
        var authorization = await _authorizationManager.FindByIdAsync(authorizationId, cancellationToken);
        if (authorization is null)
        {
            return Result.Failure(Error.NotFound("授权记录不存在。"));
        }

        // 先撤销该授权下已颁发的所有令牌，再撤销授权本身
        var revokedTokens = await _tokenManager.RevokeByAuthorizationIdAsync(authorizationId, cancellationToken);
        await _authorizationManager.TryRevokeAsync(authorization, cancellationToken);

        var subject = await _authorizationManager.GetSubjectAsync(authorization, cancellationToken);

        await _audit.LogAsync(
            new AuditEntry(
                AuditActionType.ConsentRevoked,
                true,
                subject,
                Details: $"撤销授权 {authorizationId}（连带撤销令牌 {revokedTokens} 个）"),
            cancellationToken);

        return Result.Success();
    }
}
