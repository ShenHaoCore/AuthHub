using AuthHub.Domain.Constants;

namespace AuthHub.Application.Options;

/// <summary>
/// 「角色 → 权限」归属映射的可配置载体 —— 即授权三层里的**策略层**数据。
///
/// 为什么这一层可以从代码搬到配置，而权限点不行：
///   - 能力目录（<see cref="AuthHubConstants.Permissions"/>）与授权策略
///     （<see cref="AuthHubConstants.Policies"/>）是编译期常量，端点上那道
///     <c>[Authorize]</c> 是程序集的一部分；
///   - 因此配置能改变的只有"已存在的能力发给谁"，凭空发明不出一个新能力。
/// 提权面因此被收窄成"谁能改生产环境的配置"。
///
/// 绑定语义（由 RolePermissionExtensions 实现）：配置里**出现**的角色覆盖出厂默认，
/// 未出现的角色沿用 <see cref="RolePermissionMap.CreateDefaultRoles"/> 的值。
/// 注意 <c>"SomeRole": []</c> 是有效写法，表示显式收回该角色的全部权限。
/// </summary>
public sealed class RolePermissionOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "AuthHub:RolePermissions";

    /// <summary>
    /// 角色名 → 该角色拥有的权限名集合。
    /// 初始值是内置角色的出厂默认映射，配置绑定会在此基础上覆盖同名角色。
    /// </summary>
    public Dictionary<string, string[]> Roles { get; set; } = RolePermissionMap.CreateDefaultRoles();
}
