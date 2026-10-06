using AuthHub.Application.Interfaces;

namespace AuthHub.UnitTests.Fakes;

/// <summary>
/// <see cref="IRolePermissionOverrideStore"/> 的可控替身（内存字典）。
///
/// 有了它，<c>LayeredRolePermissionMap</c> 的叠加规则（尤其是
/// "有覆盖行 + 空数组 = 显式收回全部权限，**不**回落配置"）可以被单元测试直接钉住，
/// 不必为了一条规则去起一个真实数据库。
/// 走 EF 的那条路径由集成测试覆盖。
/// </summary>
internal sealed class FakeRolePermissionOverrideStore : IRolePermissionOverrideStore
{
    private readonly Dictionary<string, string[]> _rows;

    public FakeRolePermissionOverrideStore(params (string RoleName, string[] Permissions)[] rows)
        => _rows = rows.ToDictionary(row => row.RoleName, row => row.Permissions, StringComparer.OrdinalIgnoreCase);

    /// <summary>已被读取的次数。用来断言"快照确实被缓存了/失效后确实重读了"。</summary>
    public int LoadCount { get; private set; }

    public IReadOnlyDictionary<string, string[]> Load()
    {
        LoadCount++;
        return _rows;
    }

    /// <summary>改底层数据，模拟"别处（或另一个实例）改了库"。用于验证缓存行为。</summary>
    public void Set(string roleName, string[] permissions) => _rows[roleName] = permissions;

    public void Remove(string roleName) => _rows.Remove(roleName);
}
