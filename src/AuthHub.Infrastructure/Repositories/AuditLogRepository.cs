using AuthHub.Domain.Entities;
using AuthHub.Domain.Enums;
using AuthHub.Domain.Interfaces;
using AuthHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AuthHub.Infrastructure.Repositories;

/// <summary>审计日志仓储（EF Core 实现）。</summary>
public sealed class AuditLogRepository : IAuditLogRepository
{
    private readonly AuthHubDbContext _dbContext;

    public AuditLogRepository(AuthHubDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(AuditLog log, CancellationToken cancellationToken = default)
        => await _dbContext.AuditLogs.AddAsync(log, cancellationToken);

    public async Task<AuditLog?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        => await _dbContext.AuditLogs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<(IReadOnlyList<AuditLog> Items, long Total)> QueryAsync(
        string? action = null,
        string? userId = null,
        string? clientId = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.AuditLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(x => x.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(userId))
        {
            query = query.Where(x => x.UserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(clientId))
        {
            query = query.Where(x => x.ClientId == clientId);
        }

        if (from is not null)
        {
            query = query.Where(x => x.CreatedAt >= from);
        }

        if (to is not null)
        {
            query = query.Where(x => x.CreatedAt <= to);
        }

        var total = await query.LongCountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((Math.Max(page, 1) - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task<IReadOnlyList<(string UserId, DateOnly Date, int Count)>> GetDailyLoginCountsAsync(
        IReadOnlyCollection<string> userIds,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        var completedActions = new[]
        {
            AuditActionType.UserLoginSucceeded,
            AuditActionType.UserExternalLoginSucceeded,
            AuditActionType.TwoFactorRecoveryCodeUsed
        };

        var logs = await _dbContext.AuditLogs.AsNoTracking()
            .Where(x => x.Succeeded
                        && x.UserId != null
                        && userIds.Contains(x.UserId)
                        && completedActions.Contains(x.Action)
                        && x.CreatedAt >= from
                        && x.CreatedAt < to
                        // 旧版本把等待 MFA 也记为登录成功；新事件已拆分，这里兼容历史数据。
                        && x.Details != "密码校验通过，等待 MFA")
            .Select(x => new { x.UserId, x.CreatedAt })
            .ToListAsync(cancellationToken);

        // SQLite 不能翻译 DateTimeOffset 的日期分组；只投影两个字段后按 UTC 计数，保持跨提供方一致。
        return logs
            .GroupBy(x => new { x.UserId, Date = DateOnly.FromDateTime(x.CreatedAt.UtcDateTime) })
            .Select(g => (UserId: g.Key.UserId!, g.Key.Date, Count: g.Count()))
            .ToArray();
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        => _dbContext.SaveChangesAsync(cancellationToken);
}
