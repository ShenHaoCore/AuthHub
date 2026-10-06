namespace AuthHub.Application.Interfaces;

/// <summary>
/// 「角色 → 权限」归属的查询入口 —— 授权三层里的**策略层**。
///
/// 生效值由三种来源按优先级叠加而成（高的盖住低的）：
///   1) 出厂默认 —— <c>AuthHub.Domain.Constants.RolePermissionMap.CreateDefaultRoles()</c>；
///   2) 配置 <c>AuthHub:RolePermissions</c>；
///   3) 数据库里的运行时覆盖（<c>RolePermissionOverrides</c> 表，后台界面写入）。
///
/// 与"能力目录"的区别是本接口能改的边界：目录（有哪些权限点）与策略名
/// 都是编译期常量，端点上那道 <c>[Authorize]</c> 是程序集的一部分，
/// 所以这里能改的只有"已存在的能力发给谁"。
///
/// 实现是**单例**且带缓存（解析角色发生在每次登录生成 authhub:permission 声明的热路径上），
/// 因此所有成员都是同步的；写入路径改完数据后要调一次 <see cref="Invalidate"/>。
/// </summary>
public interface IRolePermissionMap
{
    /// <summary>当前生效的全部角色及其权限（角色名 → 权限集合）。</summary>
    IReadOnlyDictionary<string, IReadOnlyCollection<string>> All { get; }

    /// <summary>取得指定角色拥有的权限；未知角色返回空集合而不抛异常。</summary>
    IReadOnlyCollection<string> GetPermissions(string roleName);

    /// <summary>把一组角色展开为去重后的权限集合（用于生成 authhub:permission 声明）。</summary>
    IReadOnlyCollection<string> ResolvePermissions(IEnumerable<string> roleNames);

    /// <summary>角色是否拥有指定权限。</summary>
    bool RoleHasPermission(string roleName, string permission);

    /// <summary>
    /// 是否为内置角色。
    ///
    /// 判据是编译期的 <c>AuthHubConstants.Roles.All</c>，而不是"配置或数据库里出现过这个角色"——
    /// 后者会让"顺手给某个自定义角色补一条权限映射"把它一并变成不可删除的系统角色。
    /// </summary>
    bool IsBuiltInRole(string roleName);

    /// <summary>
    /// 该角色的权限是否来自运行时覆盖（即后台界面上改过）。
    /// 为 false 表示当前用的是配置 / 出厂默认 —— 界面据此区分"自定义"与"沿用默认"。
    /// </summary>
    bool IsCustomized(string roleName);

    /// <summary>
    /// 丢弃缓存、下次访问时重新读库。由写入路径（<c>IRolePermissionAdminService</c>）在保存后调用。
    ///
    /// 已知边界：只失效**本进程**的缓存。多实例部署时其他实例要等重启，
    /// 或由网关把后台写请求粘到同一实例上 —— 本项目当前是单实例形态。
    /// </summary>
    void Invalidate();
}
