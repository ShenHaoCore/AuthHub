namespace AuthHub.Application.Interfaces;

/// <summary>
/// 「角色 → 权限」归属的查询入口 —— 授权三层里的**策略层**。
///
/// 数据来自配置 <c>AuthHub:RolePermissions</c>，出厂默认与升级前的代码内映射一致
/// （见 <c>AuthHub.Domain.Constants.RolePermissionMap</c>）。
///
/// 与"能力目录"的区别是本接口能改的边界：目录（有哪些权限点）与策略（策略名）
/// 都是编译期常量，端点上那道 <c>[Authorize]</c> 是程序集的一部分，
/// 所以这里能改的只有"已存在的能力发给谁"。
/// </summary>
public interface IRolePermissionMap
{
    /// <summary>配置中出现的全部角色及其权限（角色名 → 权限集合）。</summary>
    IReadOnlyDictionary<string, IReadOnlyCollection<string>> All { get; }

    /// <summary>取得指定角色拥有的权限；未配置的角色返回空集合而不抛异常。</summary>
    IReadOnlyCollection<string> GetPermissions(string roleName);

    /// <summary>把一组角色展开为去重后的权限集合（用于生成 authhub:permission 声明）。</summary>
    IReadOnlyCollection<string> ResolvePermissions(IEnumerable<string> roleNames);

    /// <summary>角色是否拥有指定权限。</summary>
    bool RoleHasPermission(string roleName, string permission);

    /// <summary>
    /// 是否为内置角色。
    ///
    /// 判据是编译期的 <c>AuthHubConstants.Roles.All</c>，而不是"配置里有没有出现过这个角色"——
    /// 后者会让"顺手给某个自定义角色补一条权限映射"把它一并变成不可删除的系统角色。
    /// </summary>
    bool IsBuiltInRole(string roleName);
}
