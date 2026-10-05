using AuthHub.Application.Common;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Enums;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using Oidc = OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthHub.Infrastructure.Services;

/// <summary>
/// 令牌撤销 / 紧急下线。
///
/// 原理：OpenIddict 把令牌状态持久化在 OpenIddictTokens 表中（默认启用令牌存储），
/// 校验时 <c>EnableTokenEntryValidation()</c> 会检查该状态。
/// 因此把状态置为 revoked 后，即使 JWT 尚未过期也不会再被接受——
/// 这就是“令牌黑名单”在 OpenIddict 里的原生实现方式，无需自己维护黑名单表。
/// </summary>
public sealed class OpenIddictTokenAdminService : ITokenAdminService
{
    private readonly IOpenIddictTokenManager _tokenManager;
    private readonly IOpenIddictAuthorizationManager _authorizationManager;
    private readonly IOpenIddictApplicationManager _applicationManager;
    private readonly IAuditLogService _audit;
    private readonly ILogger<OpenIddictTokenAdminService> _logger;

    public OpenIddictTokenAdminService(
        IOpenIddictTokenManager tokenManager,
        IOpenIddictAuthorizationManager authorizationManager,
        IOpenIddictApplicationManager applicationManager,
        IAuditLogService audit,
        ILogger<OpenIddictTokenAdminService> logger)
    {
        _tokenManager = tokenManager;
        _authorizationManager = authorizationManager;
        _applicationManager = applicationManager;
        _audit = audit;
        _logger = logger;
    }

    public async Task<Result<RevokeResult>> RevokeBySubjectAsync(string subject, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return Result.Failure<RevokeResult>(Error.Validation("subject 不能为空。"));
        }

        var tokens = 0;
        await foreach (var token in _tokenManager.FindBySubjectAsync(subject, cancellationToken))
        {
            if (await _tokenManager.TryRevokeAsync(token, cancellationToken))
            {
                tokens++;
            }
        }

        var authorizations = 0;
        await foreach (var authorization in _authorizationManager.FindBySubjectAsync(subject, cancellationToken))
        {
            if (await _authorizationManager.TryRevokeAsync(authorization, cancellationToken))
            {
                authorizations++;
            }
        }

        await _audit.LogTokensRevokedAsync(subject, tokens, cancellationToken);

        _logger.LogInformation("已强制下线 subject={Subject}，撤销令牌 {Tokens} 个、授权 {Authorizations} 个。",
            subject, tokens, authorizations);

        return Result.Success(new RevokeResult(tokens, authorizations));
    }

    public async Task<Result<RevokeResult>> RevokeByClientAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var application = await _applicationManager.FindByClientIdAsync(clientId, cancellationToken);
        if (application is null)
        {
            return Result.Failure<RevokeResult>(Error.NotFound($"客户端 {clientId} 不存在。"));
        }

        var applicationId = await _applicationManager.GetIdAsync(application, cancellationToken);
        if (string.IsNullOrEmpty(applicationId))
        {
            return Result.Failure<RevokeResult>(Error.Failure("InvalidApplication", "无法确定客户端内部标识。"));
        }

        var tokens = 0;
        await foreach (var token in _tokenManager.FindByApplicationIdAsync(applicationId, cancellationToken))
        {
            if (await _tokenManager.TryRevokeAsync(token, cancellationToken))
            {
                tokens++;
            }
        }

        var authorizations = 0;
        await foreach (var authorization in _authorizationManager.FindByApplicationIdAsync(applicationId, cancellationToken))
        {
            if (await _authorizationManager.TryRevokeAsync(authorization, cancellationToken))
            {
                authorizations++;
            }
        }

        await _audit.LogAsync(
            new AuditEntry(
                AuditActionType.TokenRevoked,
                true,
                ClientId: clientId,
                Details: $"撤销令牌 {tokens} 个、授权 {authorizations} 个"),
            cancellationToken);

        return Result.Success(new RevokeResult(tokens, authorizations));
    }

    public async Task<Result> RevokeByReferenceIdAsync(string referenceId, CancellationToken cancellationToken = default)
    {
        var token = await _tokenManager.FindByReferenceIdAsync(referenceId, cancellationToken);
        if (token is null)
        {
            return Result.Failure(Error.NotFound("未找到对应的令牌引用。"));
        }

        var status = await _tokenManager.GetStatusAsync(token, cancellationToken);
        if (string.Equals(status, Oidc.Statuses.Revoked, StringComparison.Ordinal))
        {
            return Result.Success();
        }

        await _tokenManager.TryRevokeAsync(token, cancellationToken);

        await _audit.LogAsync(
            new AuditEntry(AuditActionType.TokenRevoked, true, Details: $"reference_id={referenceId}"),
            cancellationToken);

        return Result.Success();
    }

    public async Task<Result<int>> PruneAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        var threshold = DateTimeOffset.UtcNow.Subtract(olderThan);

        var prunedTokens = await _tokenManager.PruneAsync(threshold, cancellationToken);
        var prunedAuthorizations = await _authorizationManager.PruneAsync(threshold, cancellationToken);

        _logger.LogInformation("已清理过期记录：令牌 {Tokens} 条、授权 {Authorizations} 条（阈值 {Threshold:O}）。",
            prunedTokens, prunedAuthorizations, threshold);

        return Result.Success((int)prunedTokens);
    }
}
