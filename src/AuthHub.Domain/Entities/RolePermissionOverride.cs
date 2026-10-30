namespace AuthHub.Domain.Entities;

/// <summary>「角色 → 权限」归属的**运行时覆盖**（授权三层里策略层的最高优先级）。</summary>
/// <remarks>
/// - 优先级从低到高：出厂默认（<c>RolePermissionMap.CreateDefaultRoles()</c>）→ 配置 <c>AuthHub:RolePermissions</c> → 本表（后台界面勾选后落库）。
/// - 把"能改"从配置文件挪到数据库：改配置必须重启进程（生产要排窗口），落库即时生效且每次变更都能写审计。
/// - **一行一个角色、存完整权限快照**（而非"角色-权限"关系表）：关系表无法区分"显式设成没有任何权限"与"没被覆盖过"—— 两者都是没有行而语义完全不同；快照行天然区分：有行即覆盖，空数组即"显式收回全部权限"。
/// - 主键用**角色名**而不是 RoleId：策略、声明、配置基线都以角色名为键，免一次 join，也允许给"配置里声明了但还没建出来"的角色预留覆盖；代价是删除角色时要顺手清掉这里的行（见 <c>RoleAdminService.DeleteAsync</c>）。
/// </remarks>
public class RolePermissionOverride
{
    /// <summary>角色名（主键）。比较不区分大小写，与 Identity 的角色名查找保持一致。</summary>
    public string RoleName { get; set; } = string.Empty;

    /// <summary>该角色拥有的**全部**权限（完整快照，不是增量）。空数组合法，表示显式收回全部权限；由 EF 的 primitive collection 映射成 JSON 文本列。</summary>
    public string[] Permissions { get; set; } = Array.Empty<string>();

    /// <summary>最后一次变更时间（UTC）。</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>最后一次变更的操作者（用户名）。冗余存储，便于用户被删后仍可追溯。</summary>
    public string? UpdatedBy { get; set; }
}
