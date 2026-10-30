namespace AuthHub.Application.Interfaces;

/// <summary>「角色 → 权限」归属的查询入口 —— 授权三层里的**策略层**。</summary>
/// <remarks>
/// - 生效值由三种来源按优先级叠加（高的盖住低的）：出厂默认（<c>RolePermissionMap.CreateDefaultRoles()</c>）→ 配置 <c>AuthHub:RolePermissions</c> → 数据库运行时覆盖（<c>RolePermissionOverrides</c> 表，后台界面写入）。
/// - 与"能力目录"的边界：权限点与策略名是编译期常量、端点上的 <c>[Authorize]</c> 是程序集的一部分，这里能改的只有"已存在的能力发给谁"。
/// - 实现是**单例**且带缓存（解析角色发生在每次登录生成 authhub:permission 声明的热路径上），因此成员全部同步；写入路径改完数据后要调一次 <see cref="Invalidate"/>。
/// </remarks>
public interface IRolePermissionMap
{
    /// <summary>当前生效的全部角色及其权限（角色名 → 权限集合）。</summary>
    IReadOnlyDictionary<string, IReadOnlyCollection<string>> All { get; }

    /// <summary>取得指定角色拥有的权限；未知角色返回空集合而不抛异常。</summary>
    IReadOnlyCollection<string> GetPermissions(string roleName);

    /// <summary>把一组角色展开为去重后的权限集合（用于生成 authhub:permission 声明）。</summary>
    IReadOnlyCollection<string> ResolvePermissions(IEnumerable<string> roleNames);

    bool RoleHasPermission(string roleName, string permission);

    /// <summary>
    /// 是否为内置角色。判据是编译期的 <c>AuthHubConstants.Roles.All</c>，而不是"配置或数据库里出现过这个角色"——
    /// 否则"顺手给某个自定义角色补一条权限映射"会把它一并变成不可删除的系统角色。
    /// </summary>
    bool IsBuiltInRole(string roleName);

    /// <summary>该角色的权限是否来自运行时覆盖（后台界面上改过）；false 表示用的是配置 / 出厂默认 —— 界面据此区分"自定义"与"沿用默认"。</summary>
    bool IsCustomized(string roleName);

    /// <summary>
    /// 丢弃缓存、下次访问时重新读库。由写入路径（<c>IRolePermissionAdminService</c>）在保存后调用。
    /// 已知边界：只失效**本进程**的缓存 —— 多实例部署时其他实例要等重启或由网关把后台写请求粘到同一实例上（本项目当前是单实例形态）。
    /// </summary>
    void Invalidate();
}
