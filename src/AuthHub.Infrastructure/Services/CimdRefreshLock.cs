using AuthHub.Application.Interfaces;
using AuthHub.Application.Options;
using AuthHub.Infrastructure.Data;
using AuthHub.Domain.Entities;
using AuthHub.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthHub.Infrastructure.Services;

/// <summary>
/// <see cref="ICimdRefreshLock"/> 的数据库租约实现（表：<c>CimdRefreshLeases</c>）。
///
/// <para><b>为什么用数据库而不是进程内锁或 Redis</b>：进程内锁在多副本部署下等于没锁；
/// 引入 Redis 只是为了一把 5 秒的短锁，代价远大于收益。数据库本来就是共享存储，
/// 一行「主键 + 到期时间 + 持有者令牌」就够用，SQL Server 与 SQLite 行为一致。</para>
///
/// <para><b>两种取得路径</b>：
/// ① 还没有记录 → 插入（主键冲突即说明别人先到）；
/// ② 记录已过期（持有者崩了）→ 用带 <c>WHERE ExpiresAt &lt; now</c> 的原子更新接管，
///    只有一行被改到才算取得。两条路都把"谁赢"交给数据库的唯一约束，不做先查后写。</para>
///
/// <para><b>租约时长 = 拉取超时 + 15 秒缓冲</b>：必须严格长于一次拉取，否则同一实例会与
/// 自己接管打架；也不能太长，否则持有者崩溃后其他实例要干等那么久。</para>
/// </summary>
internal sealed class CimdRefreshLock : ICimdRefreshLock
{
    private static readonly TimeSpan LeaseBuffer = TimeSpan.FromSeconds(15);

    private readonly AuthHubDbContext _dbContext;
    private readonly IClock _clock;
    private readonly ILogger<CimdRefreshLock> _logger;
    private readonly TimeSpan _leaseDuration;

    public CimdRefreshLock(
        AuthHubDbContext dbContext,
        IClock clock,
        IOptions<CimdOptions> options,
        ILogger<CimdRefreshLock> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _logger = logger;
        _leaseDuration = options.Value.FetchTimeout + LeaseBuffer;
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var holderToken = Guid.NewGuid().ToString("N");
        var lease = new CimdRefreshLease
        {
            ClientId = clientId,
            HolderToken = holderToken,
            ExpiresAt = now + _leaseDuration
        };

        _dbContext.CimdRefreshLeases.Add(lease);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);

            return new Lease(this, clientId, holderToken);
        }
        catch (DbUpdateException)
        {
            // 主键冲突＝另一个实例先插入了。把失败的实体摘出去，否则后续查询会
            // 继续把它当作待插入的 Added 实体，下一次 SaveChanges 再次撞主键。
            _dbContext.Entry(lease).State = EntityState.Detached;
        }

        // 已有记录：只有"已过期"的才能被接管，且更新条件里带上比较，避免两个实例都认为自己赢了
        var takenOver = await _dbContext.CimdRefreshLeases
            .Where(existing => existing.ClientId == clientId && existing.ExpiresAt < now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(existing => existing.HolderToken, holderToken)
                    .SetProperty(existing => existing.ExpiresAt, now + _leaseDuration),
                cancellationToken);

        if (takenOver == 1)
        {
            return new Lease(this, clientId, holderToken);
        }

        _logger.LogDebug(
            "CIMD 刷新租约已被其他实例持有 | ClientId={ClientId}",
            clientId);

        return null;
    }

    /// <summary>
    /// 释放租约。失败只记日志、不抛 —— 释放发生在请求的收尾阶段，
    /// 元数据已经注册成功，不该因为一次清理失败把成功的请求变成 500。
    /// 最坏情况是租约自然过期。
    /// </summary>
    private async ValueTask ReleaseAsync(string clientId, string holderToken)
    {
        try
        {
            await _dbContext.CimdRefreshLeases
                .Where(lease => lease.ClientId == clientId && lease.HolderToken == holderToken)
                .ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "CIMD 刷新租约释放失败，将等待自然过期 | ClientId={ClientId}",
                clientId);
        }
    }

    /// <summary>一次性租约句柄：<c>await using</c> 结束时释放。</summary>
    private sealed class Lease(CimdRefreshLock owner, string clientId, string holderToken) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
            => Interlocked.Exchange(ref _released, 1) == 0
                ? owner.ReleaseAsync(clientId, holderToken)
                : ValueTask.CompletedTask;
    }
}
