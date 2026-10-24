namespace AuthHub.Domain.Entities;

/// <summary>
/// CIMD 元数据「到期重取」的分布式租约（一把跨实例的短锁）。
///
/// <para><b>要解决的问题</b>：元数据缓存本身就是数据库里的应用记录，多实例天然共享；
/// 但缓存到期的那一刻，所有实例会同时发现"过期了"，于是一起向同一个第三方站点出站。
/// 这不是正确性问题（应用表有唯一索引、OpenIddict 有并发令牌），只是把对方站点
/// 和本服务的出站带宽按实例数放大了一次。这里让**只有一个实例**去拉，其余等它写完。</para>
///
/// <para><b>为什么是表而不是进程内锁</b>：进程内锁只在单实例下有效，部署多副本时等于没锁。
/// 本仓库没有 Redis 之类的分布式缓存依赖，而数据库是本来就有的共享存储 ——
/// 用一行带过期时间的租约即可，SQL Server 与 SQLite 都支持。</para>
///
/// <para><b>过期时间不是可选项</b>：持有者崩在拉取中途时，没有过期时间的锁会把该客户端
/// 永久锁死。租约有效期取「拉取超时 + 缓冲」，持有者正常结束时主动删除。</para>
/// </summary>
public class CimdRefreshLease
{
    /// <summary>
    /// 被租用的 <c>client_id</c>（CIMD 场景下就是元数据 URL）。主键 —— 同一个客户端同时只允许一个持有者。
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// 持有者令牌。释放时按它匹配，避免"我的租约已被别人接管"时误删别人的锁。
    /// </summary>
    public string HolderToken { get; set; } = string.Empty;

    /// <summary>租约到期时刻（UTC）。到期后其他实例可以原子地接管。</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
