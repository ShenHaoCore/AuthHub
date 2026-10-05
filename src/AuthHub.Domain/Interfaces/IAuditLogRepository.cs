using AuthHub.Domain.Entities;

namespace AuthHub.Domain.Interfaces;

/// <summary>审计日志仓储抽象。实现在 Infrastructure 层。</summary>
public interface IAuditLogRepository
{
    Task AddAsync(AuditLog log, CancellationToken cancellationToken = default);

    Task<AuditLog?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>按条件分页查询，返回当页数据与总数。</summary>
    Task<(IReadOnlyList<AuditLog> Items, long Total)> QueryAsync(
        string? action = null,
        string? userId = null,
        string? clientId = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
