using AuthHub.Application.Common;

namespace AuthHub.Application.Interfaces;

/// <summary>「角色 → 权限」归属的**写入**入口（对应 <see cref="IRolePermissionMap"/> 的读侧）。</summary>
/// <remarks>
/// - 落库而不是改配置文件：改配置要重启进程（生产要排窗口），落库即时生效且每一次变更都能写审计；代价是"授权关系"有了两处事实源 —— 配置仍是**基线**，数据库里的行是某角色的**整体替换**（含"替换成空集"），删掉那行即回落基线。
/// - 安全边界：只能把**已存在的权限点**发给某个角色 —— 权限点是编译期常量、端点上的 <c>[Authorize]</c> 是程序集的一部分，凭"界面上点几下"发明不出新能力。
/// </remarks>
public interface IRolePermissionAdminService
{
    /// <summary>
    /// 用给定的权限集合**整体替换**该角色的归属（空集合是合法输入，表示收回全部权限）。
    /// 校验权限名、拒绝会让系统再没人能管理角色的变更，写库后落一条审计并让缓存失效。
    /// </summary>
    Task<Result> SaveAsync(string roleName, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删掉该角色的运行时覆盖，回落配置 / 出厂默认。同样带防自锁校验与审计。
    /// </summary>
    Task<Result> ResetAsync(string roleName, CancellationToken cancellationToken = default);
}
