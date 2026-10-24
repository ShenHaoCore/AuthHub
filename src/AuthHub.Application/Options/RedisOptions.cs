namespace AuthHub.Application.Options;

/// <summary>
/// Redis 令牌条目缓存的配置。
///
/// <para><b>是什么</b>：<c>EnableTokenEntryValidation()</c> 让每个带访问令牌的 API 请求都
/// 查一次库，确认令牌条目仍然有效（撤销即时性的代价）。本组件把这条热路径缓存进 Redis，
/// 多实例部署下共享一份缓存，撤销仍然立刻生效（见 <c>RedisCachedTokenStore</c> 的失效设计）。</para>
///
/// <para><b>为什么默认关闭</b>：Redis 是一个新引入的基础设施依赖 —— 单实例部署（SQLite）
/// 每次校验本来就是一次本地文件读，加一层远程缓存反而是负优化。默认关闭意味着升级不会
/// 悄悄改变部署要求；需要横向扩容的部署显式打开。</para>
///
/// <para><b>失效语义</b>：令牌级撤销走"精确删键"，批量撤销（RevokeBySubjectAsync 等）与
/// 清理（PruneAsync）走"全局版本号递增"—— 两者都保证已撤销的令牌**立刻**失效，与不缓存时
/// 一致。缓存层自身故障只降级（直查数据库），不阻断认证。</para>
/// </summary>
public sealed class RedisOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "AuthHub:Redis";

    /// <summary>总开关。关闭时完全不创建 Redis 连接，行为与引入本组件之前一致。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// StackExchange.Redis 连接串。默认指向本机回环（WSL 部署形态下 WSL 内的 Redis 经
    /// localhost 转发可达）。
    ///
    /// <para><c>abortConnect=false</c> 是刻意的：Redis 暂时不可用时，连接器在后台重试而不是
    /// 让首次缓存操作直接抛死 —— 缓存层故障走"降级直查库"的路径，不该演变成认证不可用。</para>
    /// </summary>
    public string ConnectionString { get; set; } = "localhost:6379,abortConnect=false";

    /// <summary>
    /// 缓存条目的存活时间（分钟）。正常情况下条目由"精确删键 / 版本号递增"主动失效，
    /// TTL 只是兜底：Redis 故障窗口内发生的撤销、以及旧版本号下的孤儿键，都靠它回收。
    /// 因此取值权衡的是"故障窗口内撤销延迟生效的时长"，不是缓存命中率。
    /// </summary>
    public int EntryTtlMinutes { get; set; } = 60;

    /// <summary>
    /// 键前缀。多套环境共用一个 Redis 实例（或集成测试并行跑）时用来隔离键空间；
    /// 也让运维在 Redis 里能用 <c>SCAN MATCH "authhub:tokens:*"</c> 一眼认出本服务的键。
    /// </summary>
    public string KeyPrefix { get; set; } = "authhub:tokens:";

    /// <summary>规范化为可用的条目 TTL。</summary>
    public TimeSpan EntryTtl => TimeSpan.FromMinutes(Math.Clamp(EntryTtlMinutes, 1, 24 * 60));

    /// <summary>规范化为可用的键前缀（保证非空，且以冒号结尾，符合 Redis 键的分层惯例）。</summary>
    public string EffectiveKeyPrefix
    {
        get
        {
            var prefix = string.IsNullOrWhiteSpace(KeyPrefix) ? "authhub:tokens:" : KeyPrefix.Trim();
            return prefix.EndsWith(':') ? prefix : $"{prefix}:";
        }
    }
}
