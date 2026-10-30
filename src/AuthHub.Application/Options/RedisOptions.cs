namespace AuthHub.Application.Options;

/// <summary>Redis 令牌条目缓存的配置。</summary>
/// <remarks>
/// - 背景：<c>EnableTokenEntryValidation()</c> 让每个带访问令牌的 API 请求都查一次库确认令牌条目仍有效（撤销即时性的代价）；本组件把这条热路径缓存进 Redis，多实例部署共享一份缓存，撤销仍然立刻生效（设计见 <c>RedisCachedTokenStore</c>）。
/// - 默认关闭是刻意的：Redis 是新引入的基础设施依赖，单实例部署（SQLite）下每次校验本来就是本地文件读，加远程缓存反而是负优化；需要横向扩容的部署显式打开，升级不会悄悄改变部署要求。
/// - 失效语义：令牌级撤销走"精确删键"，批量撤销与清理走"全局版本号递增"，都保证已撤销的令牌**立刻**失效；缓存层自身故障只降级直查数据库，不阻断认证。
/// </remarks>
public sealed class RedisOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "AuthHub:Redis";

    /// <summary>总开关。关闭时完全不创建 Redis 连接，行为与引入本组件之前一致。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// StackExchange.Redis 连接串。默认指向本机回环（WSL 部署形态下 WSL 内的 Redis 经 localhost 转发可达）。
    /// <c>abortConnect=false</c> 是刻意的：Redis 暂时不可用时连接器在后台重试而不是让首次缓存操作直接抛死 —— 缓存层故障走"降级直查库"，不该演变成认证不可用。
    /// </summary>
    public string ConnectionString { get; set; } = "localhost:6379,abortConnect=false";

    /// <summary>
    /// 缓存条目的存活时间（分钟）。正常失效靠"精确删键 / 版本号递增"，
    /// TTL 只是兜底（Redis 故障窗口内发生的撤销、旧版本号下的孤儿键）—— 权衡的是撤销延迟生效的时长，不是缓存命中率。
    /// </summary>
    public int EntryTtlMinutes { get; set; } = 60;

    /// <summary>键前缀。多套环境共用一个 Redis 实例（或集成测试并行跑）时隔离键空间，也让运维能用 <c>SCAN MATCH "authhub:tokens:*"</c> 一眼认出本服务的键。</summary>
    public string KeyPrefix { get; set; } = "authhub:tokens:";

    public TimeSpan EntryTtl => TimeSpan.FromMinutes(Math.Clamp(EntryTtlMinutes, 1, 24 * 60));

    public string EffectiveKeyPrefix
    {
        get
        {
            var prefix = string.IsNullOrWhiteSpace(KeyPrefix) ? "authhub:tokens:" : KeyPrefix.Trim();
            return prefix.EndsWith(':') ? prefix : $"{prefix}:";
        }
    }
}
