using AuthHub.Application.Interfaces;
using AuthHub.Application.Options;
using AuthHub.Domain.Constants;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthHub.Infrastructure.Services;

/// <summary>
/// <see cref="IRolePermissionMap"/> 的实现：把三种来源按优先级叠成一张生效表。
///
///   出厂默认（<c>CreateDefaultRoles()</c>） ← 配置 <c>AuthHub:RolePermissions</c> ← 运行时覆盖（数据库）
///
/// 做成**单例 + 懒加载快照**，理由有三：
///   1) 解析角色发生在每次登录（生成 authhub:permission 声明）与每次渲染角色页，属热路径，
///      不能每次都去读一遍覆盖表；
///   2) 惰性加载是必须的 —— 单例可能先于数据库初始化被解析（启动期自检就跑在迁移之前），
///      构造函数里不能碰数据访问；
///   3) 快照一旦建好就是不可变对象，读路径无锁。
///
/// **叠加规则里最容易写错的一条**：覆盖行存的是该角色权限的**完整快照**，
/// 因此"有覆盖行 + 空数组"是"显式收回全部权限"，**不能**回落配置 ——
/// 那正是它与"没有覆盖行"的唯一区别。
///
/// 类名里的 Layered 说的就是这件事：它不是"配置的封装"，而是三层叠加后的结果。
/// </summary>
public sealed class LayeredRolePermissionMap : IRolePermissionMap
{
    /// <summary>生效表的不可变快照。</summary>
    private sealed record Snapshot(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> Effective,
        IReadOnlySet<string> Customized);

    private readonly Dictionary<string, string[]> _baseline;
    private readonly IRolePermissionOverrideStore _overrideStore;
    private readonly ILogger<LayeredRolePermissionMap> _logger;
    private readonly object _gate = new();

    private volatile Snapshot? _snapshot;

    public LayeredRolePermissionMap(
        IOptions<RolePermissionOptions> options,
        IRolePermissionOverrideStore overrideStore,
        ILogger<LayeredRolePermissionMap> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(overrideStore);
        ArgumentNullException.ThrowIfNull(logger);

        _overrideStore = overrideStore;
        _logger = logger;

        // 构造时一次性拷贝「出厂默认 + 配置」的叠加结果。
        // 拷贝而非持有 Options 里的引用，是为了让构造之后任何对 Options 实例的改写都影响不到它。
        _baseline = options.Value.Roles.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> All => GetSnapshot().Effective;

    public IReadOnlyCollection<string> GetPermissions(string roleName)
        => GetSnapshot().Effective.TryGetValue(roleName, out var permissions)
            ? permissions
            : Array.Empty<string>();

    public IReadOnlyCollection<string> ResolvePermissions(IEnumerable<string> roleNames)
    {
        ArgumentNullException.ThrowIfNull(roleNames);

        return roleNames
            .SelectMany(GetPermissions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool RoleHasPermission(string roleName, string permission)
        => GetPermissions(roleName).Contains(permission, StringComparer.OrdinalIgnoreCase);

    public bool IsBuiltInRole(string roleName)
        => AuthHubConstants.Roles.All.Contains(roleName, StringComparer.OrdinalIgnoreCase);

    public bool IsCustomized(string roleName) => GetSnapshot().Customized.Contains(roleName);

    public void Invalidate()
    {
        // volatile 写：下一次访问会在锁内重建。旧快照可能正被并发读者使用，让它自然失活即可。
        _snapshot = null;
    }

    private Snapshot GetSnapshot()
    {
        var snapshot = _snapshot;
        if (snapshot is not null)
        {
            return snapshot;
        }

        lock (_gate)
        {
            // 双重检查：等锁期间可能已经有别的线程建好了
            snapshot = _snapshot;
            if (snapshot is not null)
            {
                return snapshot;
            }

            snapshot = Build();
            _snapshot = snapshot;
            return snapshot;
        }
    }

    private Snapshot Build()
    {
        var overrides = _overrideStore.Load();

        var effective = new Dictionary<string, string[]>(_baseline, StringComparer.OrdinalIgnoreCase);
        foreach (var (roleName, permissions) in overrides)
        {
            // 整体替换。空数组在这里就是"该角色没有任何权限"，**不**回落 baseline。
            effective[roleName] = permissions;
        }

        var unified = effective.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyCollection<string>)pair.Value
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);

        if (overrides.Count > 0)
        {
            _logger.LogInformation(
                "角色权限覆盖已加载：{Count} 个角色来自数据库（{Roles}）。",
                overrides.Count,
                string.Join("、", overrides.Keys.OrderBy(x => x, StringComparer.Ordinal)));
        }

        return new Snapshot(unified, new HashSet<string>(overrides.Keys, StringComparer.OrdinalIgnoreCase));
    }
}
