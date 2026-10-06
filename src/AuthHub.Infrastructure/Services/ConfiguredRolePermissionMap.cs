using AuthHub.Application.Interfaces;
using AuthHub.Application.Options;
using AuthHub.Domain.Constants;
using Microsoft.Extensions.Options;

namespace AuthHub.Infrastructure.Services;

/// <summary>
/// <see cref="IRolePermissionMap"/> 的实现：把配置里的「角色 → 权限」固化成一份只读字典。
///
/// 做成单例、并在构造时一次性拷贝，理由有二：
///   1) 解析角色发生在每次登录（生成 authhub:permission 声明）与每次渲染角色页，属热路径；
///   2) 配置在进程生命周期内不会变（改了要重启才生效），没有必要每次回读。
/// 拷贝数组而非直接持有 Options 里的引用，是为了让构造之后任何对 Options 实例的改写都影响不到它。
/// </summary>
public sealed class ConfiguredRolePermissionMap : IRolePermissionMap
{
    private readonly Dictionary<string, string[]> _map;
    private readonly Dictionary<string, IReadOnlyCollection<string>> _all;

    public ConfiguredRolePermissionMap(IOptions<RolePermissionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _map = options.Value.Roles.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);

        // All 直接复用同一份数组，不做二次拷贝
        _all = _map.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyCollection<string>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> All => _all;

    public IReadOnlyCollection<string> GetPermissions(string roleName)
        => _map.TryGetValue(roleName, out var permissions) ? permissions : Array.Empty<string>();

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
}
