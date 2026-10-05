using AuthHub.Application.Common;
using AuthHub.Application.DTOs.AuditLogs;

namespace AuthHub.Application.Interfaces;

/// <summary>审计日志写入与查询。</summary>
public interface IAuditLogService
{
    /// <summary>写入一条审计记录。IP / UA / 当前用户由 ICurrentUser 自动补齐。</summary>
    Task LogAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    Task<PagedResult<AuditLogDto>> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken = default);

    // ---- 便捷方法：覆盖最常见的几类审计场景 ----

    Task LogLoginSucceededAsync(string userId, string userName, string? clientId = null, CancellationToken cancellationToken = default);

    Task LogLoginFailedAsync(string? userId, string userName, string? reason = null, CancellationToken cancellationToken = default);

    Task LogTokensRevokedAsync(string subject, int tokenCount, CancellationToken cancellationToken = default);
}

/// <summary>审计记录内容（不包含 IP/UA，由实现层补齐）。</summary>
public sealed record AuditEntry(
    string Action,
    bool Succeeded = true,
    string? UserId = null,
    string? UserName = null,
    string? ClientId = null,
    string? Details = null);
