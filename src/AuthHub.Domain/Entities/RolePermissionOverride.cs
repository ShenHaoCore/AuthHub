namespace AuthHub.Domain.Entities;

/// <summary>
/// 「角色 → 权限」归属的**运行时覆盖**（授权三层里策略层的最高优先级）。
///
/// 为什么需要它：策略层有三种来源，优先级从低到高是
///   1) 出厂默认（<c>RolePermissionMap.CreateDefaultRoles()</c>）；
///   2) 配置 <c>AuthHub:RolePermissions</c>；
///   3) 本表 —— 后台界面勾选后落库的结果。
/// 之所以把"能改"这件事从配置文件挪到数据库：改配置必须重启进程，而重启在生产是要排窗口的；
/// 数据库改完即时生效，且能给每一次变更写审计。
///
/// **一行一个角色、存完整权限快照**（而不是一行一个"角色-权限"的关系表）：
/// 关系表无法区分"这个角色被显式设成了没有任何权限"与"这个角色没被覆盖过"——
/// 两者都是"没有行"，而它们的语义完全不同（前者要收回权限，后者要回落默认）。
/// 快照行天然带上了这个区分：有行就是覆盖，行里是空数组就是"显式收回全部权限"。
///
/// 主键用**角色名**而不是 RoleId：策略、声明、配置基线全都以角色名为键，
/// 用名字可以免掉一次 join，也允许给"配置里声明了但还没建出来"的角色预留覆盖。
/// 代价是删除角色时得顺手清掉这里的行（见 <c>RoleAdminService.DeleteAsync</c>）。
/// </summary>
public class RolePermissionOverride
{
    /// <summary>角色名（主键）。比较不区分大小写，与 Identity 的角色名查找保持一致。</summary>
    public string RoleName { get; set; } = string.Empty;

    /// <summary>
    /// 该角色拥有的**全部**权限（完整快照，不是增量）。空数组是合法值，表示显式收回全部权限。
    /// 存储层由 EF 的 primitive collection 映射成 JSON 文本列。
    /// </summary>
    public string[] Permissions { get; set; } = Array.Empty<string>();

    /// <summary>最后一次变更时间（UTC）。</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>最后一次变更的操作者（用户名）。冗余存储，便于用户被删后仍可追溯。</summary>
    public string? UpdatedBy { get; set; }
}
