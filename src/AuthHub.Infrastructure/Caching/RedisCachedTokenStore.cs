using System.Collections.Immutable;
using System.Text.Json;
using AuthHub.Application.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;
using StackExchange.Redis;

namespace AuthHub.Infrastructure.Caching;

/// <summary>
/// OpenIddict 令牌存储的 Redis 缓存装饰器：包住 EF Core 存储，把令牌条目校验的
/// 两次热读（<see cref="FindByIdAsync"/> / <see cref="FindByReferenceIdAsync"/>）从
/// "每次查库"变成"命中 Redis 则不出站到数据库"。
///
/// <para><b>为什么挂在存储而不是管理器/缓存接口上</b>：OpenIddict 维护者明确说
/// 分布式缓存的正确扩展缝就是自定义/包装存储（openiddict#1797）——管理器的所有读
/// 都经存储，包装存储即覆盖全部调用方（校验管线、令牌端点、管理服务）。</para>
///
/// <para><b>失效设计（本组件的核心承诺：撤销立刻生效）</b>：
/// ① 令牌级变更（Update/Delete，含 TryRevokeAsync / TryRedeemAsync 的底层路径）→
///    精确删除该令牌的缓存键；
/// ② 批量变更（<c>RevokeAsync</c>（按 subject/client 批量）、<c>RevokeBy*Async</c>、
///    <c>PruneAsync</c>）无法枚举受影响的具体令牌 → 递增全局版本号，所有旧条目立刻
///    判为过期。OpenIddict 自带的 scoped 内存缓存对批量撤销**同样不失效**（管理器的
///    批量方法根本不走缓存），所以"批量撤销在其它实例上延迟生效"是引入缓存前就存在的
///    事实——这里用版本号把它收紧到 0。</para>
///
/// <para><b>条目键与版本</b>：键为 <c>{前缀}id:{令牌Id}</c> / <c>{前缀}ref:{引用Id}</c>，
/// 值是「写入时的版本号 + 条目快照」。读路径并行 GET 条目与版本号（一次网络往返），
/// 版本不匹配视为未命中 —— 旧版本条目由 TTL 自然回收，不需要扫描删除。</para>
///
/// <para><b>缓存命中的实体形状（最容易翻车的地方）</b>：EF 模型上 <c>ApplicationId</c> /
/// <c>AuthorizationId</c> 是**影子外键**（模型类只有导航属性），JSON 往返会丢；
/// 而 <c>GetApplicationIdAsync</c> / <c>GetAuthorizationIdAsync</c> 读的是**导航**且对
/// Detached 实体直接返回 null —— 不处理的话，缓存命中时令牌的 AuthorizationId 会静默
/// 变 null，刷新令牌轮换出的新令牌将丢失与授权记录的关联。因此条目快照里单独保存两个
/// 外键 Id，命中时用 Id 重建**桩导航**（只填主键的导航实例）供属性读取；桩**不挂进**
/// EF 变更跟踪器，避免与同请求内物化的真实 Application/Authorization 行冲突
/// （EF 拒绝跟踪同键的两个实例）。进入写路径前再把外键写回影子属性（见
/// <see cref="PrepareForWriteAsync"/>），否则 <c>Update()</c> 会把外键列改成 null。</para>
///
/// <para><b>并发令牌</b>：条目快照带上了 <c>ConcurrencyToken</c>，交给内层存储
/// <c>UpdateAsync</c> 时作为乐观并发比较基准 —— 刷新令牌重用检测依赖的
/// <c>ConcurrencyException</c> 语义原样保留。</para>
///
/// <para><b>故障降级</b>：Redis 任何异常（连接、超时、协议）都只记一条节流的警告，
/// 然后直查内层存储。缓存层故障绝不把认证打断；故障窗口内发生的撤销由 TTL 兜底
/// （最迟 <see cref="RedisOptions.EntryTtl"/> 后恢复一致）。</para>
/// </summary>
internal sealed class RedisCachedTokenStore : IOpenIddictTokenStore<OpenIddictEntityFrameworkCoreToken>
{
    private readonly IOpenIddictTokenStore<OpenIddictEntityFrameworkCoreToken> _inner;
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly IOpenIddictEntityFrameworkCoreContext _contextAccessor;
    private readonly IOptionsMonitor<RedisOptions> _options;
    private readonly ILogger<RedisCachedTokenStore> _logger;

    /// <summary>降级警告的节流窗口：Redis 挂掉时每个请求都记日志会把日志灌满。</summary>
    private long _lastDegradationLogTicks;

    public RedisCachedTokenStore(
        IOpenIddictTokenStore<OpenIddictEntityFrameworkCoreToken> inner,
        IConnectionMultiplexer multiplexer,
        IOpenIddictEntityFrameworkCoreContext contextAccessor,
        IOptionsMonitor<RedisOptions> options,
        ILogger<RedisCachedTokenStore> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _contextAccessor = contextAccessor ?? throw new ArgumentNullException(nameof(contextAccessor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ------------------------------------------------------------------ 热读：带缓存的两个 Find

    public async ValueTask<OpenIddictEntityFrameworkCoreToken?> FindByIdAsync(
        string identifier, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);

        // 与内层存储同语义：同请求内已跟踪的实例优先（它是最新的库内状态，
        // 且避免了"同一令牌在变更跟踪器里出现两个实例"。
        var context = await _contextAccessor.GetDbContextAsync(cancellationToken);
        if (FindTracked(context, identifier) is OpenIddictEntityFrameworkCoreToken tracked)
        {
            return tracked;
        }

        var envelope = await TryReadAsync(IdKey(identifier), cancellationToken);
        if (envelope is not null)
        {
            return RebuildEntity(envelope.Token);
        }

        var token = await _inner.FindByIdAsync(identifier, cancellationToken);
        if (token is not null)
        {
            await FillAsync(IdKey(identifier), token, cancellationToken);
        }

        return token;
    }

    public async ValueTask<OpenIddictEntityFrameworkCoreToken?> FindByReferenceIdAsync(
        string identifier, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);

        // 引用 Id 在库里存的是混淆（SHA-256）后的值，这里的 identifier 就是那个混淆值，
        // 直接进键不会把原始令牌内容暴露成 Redis 键。
        var context = await _contextAccessor.GetDbContextAsync(cancellationToken);
        var tracked = context.ChangeTracker.Entries<OpenIddictEntityFrameworkCoreToken>()
            .FirstOrDefault(entry => string.Equals(entry.Entity.ReferenceId, identifier, StringComparison.Ordinal))
            ?.Entity;
        if (tracked is not null)
        {
            return tracked;
        }

        var envelope = await TryReadAsync(RefKey(identifier), cancellationToken);
        if (envelope is not null)
        {
            return RebuildEntity(envelope.Token);
        }

        var token = await _inner.FindByReferenceIdAsync(identifier, cancellationToken);
        if (token is not null)
        {
            await FillAsync(RefKey(identifier), token, cancellationToken);
        }

        return token;
    }

    // ------------------------------------------------------------------ 写路径：转发 + 精确失效

    public async ValueTask CreateAsync(OpenIddictEntityFrameworkCoreToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        // 新建条目的 Id / 引用 Id 都不可能有旧缓存（我们不缓存"未命中"），无需失效
        await _inner.CreateAsync(token, cancellationToken);
    }

    public async ValueTask UpdateAsync(OpenIddictEntityFrameworkCoreToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        var referenceIdBefore = await _inner.GetReferenceIdAsync(token, cancellationToken);

        await PrepareForWriteAsync(token, cancellationToken);
        await _inner.UpdateAsync(token, cancellationToken);

        // 撤销 / 兑换都走这里 —— 条目一失效，下一次校验立刻回库看到新状态
        var referenceIdAfter = await _inner.GetReferenceIdAsync(token, cancellationToken);
        await InvalidateAsync(token.Id!, referenceIdBefore, referenceIdAfter, cancellationToken);
    }

    public async ValueTask DeleteAsync(OpenIddictEntityFrameworkCoreToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        var referenceId = await _inner.GetReferenceIdAsync(token, cancellationToken);

        await PrepareForWriteAsync(token, cancellationToken);
        await _inner.DeleteAsync(token, cancellationToken);

        await InvalidateAsync(token.Id!, referenceId, null, cancellationToken);
    }

    // ------------------------------------------------------------------ 批量路径：转发 + 版本号递增

    public async ValueTask<long> PruneAsync(DateTimeOffset threshold, CancellationToken cancellationToken)
    {
        var pruned = await _inner.PruneAsync(threshold, cancellationToken);
        await BumpVersionAsync(pruned, cancellationToken);
        return pruned;
    }

    public async ValueTask<long> RevokeAsync(
        string? subject, string? client, string? status, string? type, CancellationToken cancellationToken)
    {
        var revoked = await _inner.RevokeAsync(subject, client, status, type, cancellationToken);
        await BumpVersionAsync(revoked, cancellationToken);
        return revoked;
    }

    public async ValueTask<long> RevokeByApplicationIdAsync(string identifier, CancellationToken cancellationToken)
    {
        var revoked = await _inner.RevokeByApplicationIdAsync(identifier, cancellationToken);
        await BumpVersionAsync(revoked, cancellationToken);
        return revoked;
    }

    public async ValueTask<long> RevokeByAuthorizationIdAsync(string identifier, CancellationToken cancellationToken)
    {
        var revoked = await _inner.RevokeByAuthorizationIdAsync(identifier, cancellationToken);
        await BumpVersionAsync(revoked, cancellationToken);
        return revoked;
    }

    public async ValueTask<long> RevokeBySubjectAsync(string subject, CancellationToken cancellationToken)
    {
        var revoked = await _inner.RevokeBySubjectAsync(subject, cancellationToken);
        await BumpVersionAsync(revoked, cancellationToken);
        return revoked;
    }

    // ------------------------------------------------------------------ 透传：无缓存价值的成员

    public ValueTask<long> CountAsync(CancellationToken cancellationToken) => _inner.CountAsync(cancellationToken);

    public ValueTask<long> CountAsync<TResult>(
        Func<IQueryable<OpenIddictEntityFrameworkCoreToken>, IQueryable<TResult>> query,
        CancellationToken cancellationToken) => _inner.CountAsync(query, cancellationToken);

    public IAsyncEnumerable<OpenIddictEntityFrameworkCoreToken> FindAsync(
        string? subject, string? client, string? status, string? type,
        CancellationToken cancellationToken) => _inner.FindAsync(subject, client, status, type, cancellationToken);

    public IAsyncEnumerable<OpenIddictEntityFrameworkCoreToken> FindByApplicationIdAsync(
        string identifier, CancellationToken cancellationToken) =>
        _inner.FindByApplicationIdAsync(identifier, cancellationToken);

    public IAsyncEnumerable<OpenIddictEntityFrameworkCoreToken> FindByAuthorizationIdAsync(
        string identifier, CancellationToken cancellationToken) =>
        _inner.FindByAuthorizationIdAsync(identifier, cancellationToken);

    public IAsyncEnumerable<OpenIddictEntityFrameworkCoreToken> FindBySubjectAsync(
        string subject, CancellationToken cancellationToken) => _inner.FindBySubjectAsync(subject, cancellationToken);    public ValueTask<string?> GetApplicationIdAsync(
        OpenIddictEntityFrameworkCoreToken token, CancellationToken cancellationToken) =>
        _inner.GetApplicationIdAsync(token, cancellationToken);

    public ValueTask<TResult?> GetAsync<TState, TResult>(
        Func<IQueryable<OpenIddictEntityFrameworkCoreToken>, TState, IQueryable<TResult>> query,
        TState state, CancellationToken cancellationToken) => _inner.GetAsync(query, state, cancellationToken);

    public ValueTask<string?> GetAuthorizationIdAsync(
        OpenIddictEntityFrameworkCoreToken token, CancellationToken cancellationToken) =>
        _inner.GetAuthorizationIdAsync(token, cancellationToken);

    public ValueTask<DateTimeOffset?> GetCreationDateAsync(
        OpenIddictEntityFrameworkCoreToken token, CancellationToken cancellationToken) =>
        _inner.GetCreationDateAsync(token, cancellationToken);

    public ValueTask<DateTimeOffset?> GetExpirationDateAsync(
        OpenIddictEntityFrameworkCoreToken token, CancellationToken cancellationToken) =>
        _inner.GetExpirationDateAsync(token, cancellationToken);

    public ValueTask<string?> GetIdAsync(
        OpenIddictEntityFrameworkCoreToken token, CancellationToken cancellationToken) =>
        _inner.GetIdAsync(token, cancellationToken);

    public ValueTask<string?> GetPayloadAsync(
        OpenIddictEntityFrameworkCoreToken token, CancellationToken cancellationToken) =>
        _inner.GetPayloadAsync(token, cancellationToken);

    public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(
        OpenIddictEntityFrameworkCoreToken token, CancellationToken cancellationToken) =>
        _inner.GetPropertiesAsync(token, cancellationToken);

    public ValueTask<DateTimeOffset?> GetRedemptionDateAsync(
        OpenIddictEntityFrameworkCoreToken token, CancellationToken cancellationToken) =>
        _inner.GetRedemptionDateAsync(token, cancellationToken);

    public ValueTask<string?> GetReferenceIdAsync(
        OpenIddictEntityFrameworkCoreToken token, CancellationToken cancellationToken) =>
        _inner.GetReferenceIdAsync(token, cancellationToken);

    public ValueTask<string?> GetStatusAsync(
        OpenIddictEntityFrameworkCoreToken token, CancellationToken cancellationToken) =>
        _inner.GetStatusAsync(token, cancellationToken);

    public ValueTask<string?> GetSubjectAsync(
        OpenIddictEntityFrameworkCoreToken token, CancellationToken cancellationToken) =>
        _inner.GetSubjectAsync(token, cancellationToken);

    public ValueTask<string?> GetTypeAsync(
        OpenIddictEntityFrameworkCoreToken token, CancellationToken cancellationToken) =>
        _inner.GetTypeAsync(token, cancellationToken);

    public ValueTask<OpenIddictEntityFrameworkCoreToken> InstantiateAsync(CancellationToken cancellationToken) =>
        _inner.InstantiateAsync(cancellationToken);

    public IAsyncEnumerable<OpenIddictEntityFrameworkCoreToken> ListAsync(
        int? count, int? offset, CancellationToken cancellationToken) => _inner.ListAsync(count, offset, cancellationToken);

    public IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
        Func<IQueryable<OpenIddictEntityFrameworkCoreToken>, TState, IQueryable<TResult>> query,
        TState state, CancellationToken cancellationToken) => _inner.ListAsync(query, state, cancellationToken);

    public ValueTask SetApplicationIdAsync(
        OpenIddictEntityFrameworkCoreToken token, string? identifier, CancellationToken cancellationToken) =>
        _inner.SetApplicationIdAsync(token, identifier, cancellationToken);

    public ValueTask SetAuthorizationIdAsync(
        OpenIddictEntityFrameworkCoreToken token, string? identifier, CancellationToken cancellationToken) =>
        _inner.SetAuthorizationIdAsync(token, identifier, cancellationToken);

    public ValueTask SetCreationDateAsync(
        OpenIddictEntityFrameworkCoreToken token, DateTimeOffset? date, CancellationToken cancellationToken) =>
        _inner.SetCreationDateAsync(token, date, cancellationToken);

    public ValueTask SetExpirationDateAsync(
        OpenIddictEntityFrameworkCoreToken token, DateTimeOffset? date, CancellationToken cancellationToken) =>
        _inner.SetExpirationDateAsync(token, date, cancellationToken);

    public ValueTask SetPayloadAsync(
        OpenIddictEntityFrameworkCoreToken token, string? payload, CancellationToken cancellationToken) =>
        _inner.SetPayloadAsync(token, payload, cancellationToken);

    public ValueTask SetPropertiesAsync(
        OpenIddictEntityFrameworkCoreToken token,
        ImmutableDictionary<string, JsonElement> properties, CancellationToken cancellationToken) =>
        _inner.SetPropertiesAsync(token, properties, cancellationToken);

    public ValueTask SetRedemptionDateAsync(
        OpenIddictEntityFrameworkCoreToken token, DateTimeOffset? date, CancellationToken cancellationToken) =>
        _inner.SetRedemptionDateAsync(token, date, cancellationToken);

    public ValueTask SetReferenceIdAsync(
        OpenIddictEntityFrameworkCoreToken token, string? identifier, CancellationToken cancellationToken) =>
        _inner.SetReferenceIdAsync(token, identifier, cancellationToken);

    public ValueTask SetStatusAsync(
        OpenIddictEntityFrameworkCoreToken token, string? status, CancellationToken cancellationToken) =>
        _inner.SetStatusAsync(token, status, cancellationToken);

    public ValueTask SetSubjectAsync(
        OpenIddictEntityFrameworkCoreToken token, string? subject, CancellationToken cancellationToken) =>
        _inner.SetSubjectAsync(token, subject, cancellationToken);

    public ValueTask SetTypeAsync(
        OpenIddictEntityFrameworkCoreToken token, string? type, CancellationToken cancellationToken) =>
        _inner.SetTypeAsync(token, type, cancellationToken);

    // ------------------------------------------------------------------ 缓存读写与实体重建

    private sealed record CachedTokenEntry(
        string Id,
        string? ConcurrencyToken,
        DateTime? CreationDate,
        DateTime? ExpirationDate,
        DateTime? RedemptionDate,
        string? Payload,
        string? Properties,
        string? ReferenceId,
        string? Status,
        string? Subject,
        string? Type,
        string? ApplicationId,
        string? AuthorizationId);

    private sealed record CachedEnvelope(long Version, CachedTokenEntry Token);

    private RedisKey IdKey(string identifier) => (RedisKey)$"{_options.CurrentValue.EffectiveKeyPrefix}id:{identifier}";

    private RedisKey RefKey(string referenceId) => (RedisKey)$"{_options.CurrentValue.EffectiveKeyPrefix}ref:{referenceId}";

    private RedisKey VersionKey => (RedisKey)$"{_options.CurrentValue.EffectiveKeyPrefix}ver";

    /// <summary>并行读条目与版本号（一次网络往返），版本不匹配视为未命中。</summary>
    private async Task<CachedEnvelope?> TryReadAsync(RedisKey key, CancellationToken cancellationToken)
    {
        try
        {
            var database = _multiplexer.GetDatabase();
            var entryTask = database.StringGetAsync(key);
            var versionTask = database.StringGetAsync(VersionKey);
            await Task.WhenAll(entryTask, versionTask);

            // 两个 GET 在 WhenAll 之后必然已完成，await 只是取结果；
            // 不用 .Result 是为了不在异步方法里同步阻塞（哪怕此刻它不会阻塞，读起来也是错误示范）。
            var entry = await entryTask;
            if (entry.IsNull)
            {
                return null;
            }

            var version = await versionTask;
            var envelope = JsonSerializer.Deserialize<CachedEnvelope>(entry.ToString());
            var currentVersion = version.IsNull ? 0L : (long)version;

            return envelope is not null && envelope.Version == currentVersion ? envelope : null;
        }
        catch (Exception exception) when (exception is RedisException or JsonException)
        {
            LogDegradation(exception, "读取");
            return null;
        }
    }

    /// <summary>把内层存储返回的（已跟踪）实体序列化进缓存。外键从影子属性上取。</summary>
    private async Task FillAsync(RedisKey key, OpenIddictEntityFrameworkCoreToken token, CancellationToken cancellationToken)
    {
        try
        {
            var context = await _contextAccessor.GetDbContextAsync(cancellationToken);
            var entry = context.Entry(token);
            var snapshot = new CachedTokenEntry(
                Id: token.Id!,
                ConcurrencyToken: token.ConcurrencyToken,
                CreationDate: token.CreationDate,
                ExpirationDate: token.ExpirationDate,
                RedemptionDate: token.RedemptionDate,
                Payload: token.Payload,
                Properties: token.Properties,
                ReferenceId: token.ReferenceId,
                Status: token.Status,
                Subject: token.Subject,
                Type: token.Type,
                ApplicationId: entry.Property("ApplicationId").CurrentValue as string,
                AuthorizationId: entry.Property("AuthorizationId").CurrentValue as string);

            var database = _multiplexer.GetDatabase();
            var version = await database.StringGetAsync(VersionKey);
            var envelope = new CachedEnvelope(version.IsNull ? 0L : (long)version, snapshot);

            await database.StringSetAsync(key, JsonSerializer.Serialize(envelope), _options.CurrentValue.EntryTtl);
        }
        catch (Exception exception) when (exception is RedisException or JsonException or InvalidOperationException)
        {
            // InvalidOperationException：实体恰好不在这个上下文里被跟踪（并发请求把它换成了别的实例）。
            // 缓存填充失败只影响命中率，不影响正确性 —— 什么都不用补偿。
            LogDegradation(exception, "写入");
        }
    }

    /// <summary>用条目快照重建一个可用的令牌实体（Detached，带桩导航）。</summary>
    private static OpenIddictEntityFrameworkCoreToken RebuildEntity(CachedTokenEntry snapshot) =>
        new()
        {
            Id = snapshot.Id,
            ConcurrencyToken = snapshot.ConcurrencyToken,
            CreationDate = snapshot.CreationDate,
            ExpirationDate = snapshot.ExpirationDate,
            RedemptionDate = snapshot.RedemptionDate,
            Payload = snapshot.Payload,
            Properties = snapshot.Properties,
            ReferenceId = snapshot.ReferenceId,
            Status = snapshot.Status,
            Subject = snapshot.Subject,
            Type = snapshot.Type,
            Application = snapshot.ApplicationId is null
                ? null
                : new OpenIddictEntityFrameworkCoreApplication { Id = snapshot.ApplicationId },
            Authorization = snapshot.AuthorizationId is null
                ? null
                : new OpenIddictEntityFrameworkCoreAuthorization { Id = snapshot.AuthorizationId }
        };

    /// <summary>
    /// 写路径的前置处理：把读路径专用的桩导航摘掉、外键写回影子属性、必要时让出已占用的键。
    ///
    /// <para>桩导航绝不能带着进 EF —— <c>Update</c>/<c>Remove</c> 会级联操作导航实体，
    /// 把只填了主键的桩当成真实行去写/删。外键恢复后，内层存储的
    /// <c>Attach + Update</c> 拿到的实体与"刚从库里读出来的"完全同构，乐观并发语义不变。</para>
    /// </summary>
    private async ValueTask PrepareForWriteAsync(
        OpenIddictEntityFrameworkCoreToken token, CancellationToken cancellationToken)
    {
        var applicationId = token.Application?.Id;
        var authorizationId = token.Authorization?.Id;
        token.Application = null;
        token.Authorization = null;

        // 调用方手里的实体可能是分离的（缓存命中重建的），也可能是内层枚举给的已跟踪实例。
        // 后者已经以自己的形状在跟踪器里，什么都不用做；前者要占住键 —— 若同键已有别的实例
        // （本请求先前经枚举物化过），让位，否则 Attach 会抛"同键两实例"。
        var context = await _contextAccessor.GetDbContextAsync(cancellationToken);
        if (FindTracked(context, token.Id!) is not OpenIddictEntityFrameworkCoreToken tracked
            || !ReferenceEquals(tracked, token))
        {
            DetachTrackedInstance(context, token.Id!);
            context.Attach(token);

            if (applicationId is not null)
            {
                context.Entry(token).Property("ApplicationId").CurrentValue = applicationId;
            }

            if (authorizationId is not null)
            {
                context.Entry(token).Property("AuthorizationId").CurrentValue = authorizationId;
            }
        }
    }

    private static OpenIddictEntityFrameworkCoreToken? FindTracked(DbContext context, string identifier) =>
        context.ChangeTracker.Entries<OpenIddictEntityFrameworkCoreToken>()
            .FirstOrDefault(entry => string.Equals(entry.Entity.Id, identifier, StringComparison.Ordinal))
            ?.Entity;

    private static void DetachTrackedInstance(DbContext context, string identifier)
    {
        if (FindTracked(context, identifier) is { } tracked)
        {
            context.Entry(tracked).State = EntityState.Detached;
        }
    }

    /// <summary>删除一个令牌的全部缓存键（Id 键 + 更新前后两个引用 Id 键，后者通常相同）。</summary>
    private async Task InvalidateAsync(string id, string? referenceIdBefore, string? referenceIdAfter, CancellationToken cancellationToken)
    {
        try
        {
            var database = _multiplexer.GetDatabase();
            var idTask = database.KeyDeleteAsync(IdKey(id));
            var referenceTask = string.Equals(referenceIdBefore, referenceIdAfter, StringComparison.Ordinal)
                ? referenceIdBefore is null ? Task.CompletedTask : database.KeyDeleteAsync(RefKey(referenceIdBefore))
                : Task.WhenAll(
                    referenceIdBefore is null ? Task.CompletedTask : database.KeyDeleteAsync(RefKey(referenceIdBefore)),
                    referenceIdAfter is null ? Task.CompletedTask : database.KeyDeleteAsync(RefKey(referenceIdAfter)));

            await Task.WhenAll(idTask, referenceTask);
        }
        catch (RedisException exception)
        {
            // 失效失败意味着旧条目要活到 TTL 才消失 —— 记警告让运维能对上"撤销延迟生效"的时间窗
            LogDegradation(exception, "失效");
        }
    }

    /// <summary>批量变更不经过条目级失效，用版本号让所有旧条目立刻过期（count 为 0 就不必惊动缓存）。</summary>
    private async Task BumpVersionAsync(long affectedRows, CancellationToken cancellationToken)
    {
        if (affectedRows == 0)
        {
            return;
        }

        try
        {
            await _multiplexer.GetDatabase().StringIncrementAsync(VersionKey);
        }
        catch (RedisException exception)
        {
            LogDegradation(exception, "版本号递增");
        }
    }

    private void LogDegradation(Exception exception, string operation)
    {
        // 节流：Redis 挂掉时每个请求都会走到这里，30 秒记一条足够排障。
        // 比较交换失败说明另一个线程刚记过，直接放弃。
        var now = Environment.TickCount64;
        var last = Volatile.Read(ref _lastDegradationLogTicks);
        if (now - last < _throttleWindowTicks
            || Interlocked.CompareExchange(ref _lastDegradationLogTicks, now, last) != last)
        {
            return;
        }

        _logger.LogWarning(exception,
            "Redis 令牌缓存{Operation}失败，本次降级直查数据库（{Window}秒内不重复记录）| 连接串见 AuthHub:Redis:ConnectionString",
            operation,
            _throttleWindowTicks / 1000);
    }

    /// <summary>降级警告的节流窗口（毫秒）。</summary>
    private const long _throttleWindowTicks = 30_000;
}
