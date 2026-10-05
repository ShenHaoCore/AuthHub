using AuthHub.Api.Models;
using AuthHub.Application.DTOs.AuditLogs;
using AuthHub.Application.DTOs.Tokens;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthHub.Api.Controllers;

/// <summary>审计日志查询（需要 audit.read 权限）。</summary>
[Tags("审计日志")]
[Route("api/audit-logs")]
[Authorize(Policy = AuthHubConstants.Policies.AuditRead)]
[Produces("application/json")]
public class AuditLogsController : ApiControllerBase
{
    private readonly IAuditLogService _audit;

    public AuditLogsController(IAuditLogService audit)
    {
        _audit = audit;
    }

    [EndpointSummary("查询审计日志")]
    [EndpointDescription("按动作、用户、客户端与时间区间筛选，分页返回。")]
    [HttpGet]
    public async Task<IActionResult> Query([FromQuery] AuditLogQuery query)
        => Ok(await _audit.QueryAsync(query, HttpContext.RequestAborted));
}

/// <summary>令牌运维（需要 tokens.revoke 权限）。</summary>
[Tags("令牌运维")]
[Route("api/tokens")]
[Authorize(Policy = AuthHubConstants.Policies.TokensRevoke)]
[Produces("application/json")]
public class TokensController : ApiControllerBase
{
    private readonly ITokenAdminService _tokens;

    public TokensController(ITokenAdminService tokens)
    {
        _tokens = tokens;
    }

    /// <summary>按用户或客户端批量撤销令牌（两者至少填一个）。</summary>
    [EndpointSummary("批量撤销令牌")]
    [EndpointDescription("按用户或客户端撤销，两者至少提供一个。")]
    [HttpPost("revoke")]
    public Task<IActionResult> Revoke([FromBody] RevokeTokensRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Subject) && string.IsNullOrWhiteSpace(request.ClientId))
        {
            return Task.FromResult(Problem(Application.Common.Result.Failure(
                Application.Common.Error.Validation("subject 与 clientId 至少需要提供一个。"))));
        }

        return ExecuteAsync(async () =>
        {
            if (!string.IsNullOrWhiteSpace(request.Subject))
            {
                var bySubject = await _tokens.RevokeBySubjectAsync(request.Subject, HttpContext.RequestAborted);
                if (bySubject.IsFailure)
                {
                    return Application.Common.Result.Failure<RevokeTokensResponse>(bySubject.Error);
                }

                var revoked = bySubject.Value;

                if (!string.IsNullOrWhiteSpace(request.ClientId))
                {
                    var byClient = await _tokens.RevokeByClientAsync(request.ClientId, HttpContext.RequestAborted);
                    if (byClient.IsSuccess)
                    {
                        revoked = new RevokeResult(revoked.Tokens + byClient.Value.Tokens, revoked.Authorizations + byClient.Value.Authorizations);
                    }
                }

                return Application.Common.Result.Success(new RevokeTokensResponse(revoked.Tokens, revoked.Authorizations));
            }

            var result = await _tokens.RevokeByClientAsync(request.ClientId!, HttpContext.RequestAborted);
            return result.IsSuccess
                ? Application.Common.Result.Success(new RevokeTokensResponse(result.Value.Tokens, result.Value.Authorizations))
                : Application.Common.Result.Failure<RevokeTokensResponse>(result.Error);
        });
    }

    /// <summary>按令牌引用 Id 撤销单个令牌。</summary>
    [EndpointSummary("撤销单个令牌")]
    [EndpointDescription("按令牌引用 Id 精确撤销。")]
    [HttpPost("revoke/{referenceId}")]
    public Task<IActionResult> RevokeByReference(string referenceId)
        => ExecuteAsync(() => _tokens.RevokeByReferenceIdAsync(referenceId, HttpContext.RequestAborted));

    /// <summary>清理历史失效令牌记录（默认 30 天前）。</summary>
    [EndpointSummary("清理失效令牌")]
    [EndpointDescription("删除历史失效令牌记录，默认清理 30 天前。")]
    [HttpPost("prune")]
    public Task<IActionResult> Prune([FromBody] PruneTokensRequest request)
        => ExecuteAsync(() => _tokens.PruneAsync(TimeSpan.FromDays(request.OlderThanDays), HttpContext.RequestAborted));
}
