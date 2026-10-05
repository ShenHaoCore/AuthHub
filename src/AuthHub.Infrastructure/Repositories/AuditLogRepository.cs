using AuthHub.Domain.Entities;
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

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        => _dbContext.SaveChangesAsync(cancellationToken);
}
