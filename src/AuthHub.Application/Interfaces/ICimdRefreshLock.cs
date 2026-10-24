namespace AuthHub.Application.Interfaces;

/// <summary>
/// CIMD 元数据「到期重取」的跨实例互斥 —— 保证同一时刻只有一个实例向第三方站点出站。
///
/// <para><b>只管成本，不管正确性</b>：缓存（应用记录）是共享的数据库行，重复拉取不会写坏数据
/// （创建有唯一索引兜底、更新有 OpenIddict 并发令牌）。这把锁存在的唯一理由是：
/// 缓存到期那一刻，N 个实例会同时发现过期、同时拉同一份元数据。
/// 因此**拿不到锁时不需要报错**，而是按"别人正在刷新"处理：有缓存就继续用，
/// 没有缓存就短暂等对方写完。</para>
/// </summary>
public interface ICimdRefreshLock
{
    /// <summary>
    /// 尝试取得 <paramref name="clientId"/> 的刷新租约。
    /// 成功时返回一个 <see cref="IAsyncDisposable"/>，<c>DisposeAsync</c> 即释放（按持有者令牌匹配，
    /// 租约被别人接管后不会误删别人的锁）；未能取得时返回 <c>null</c>。
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string clientId, CancellationToken cancellationToken = default);
}
