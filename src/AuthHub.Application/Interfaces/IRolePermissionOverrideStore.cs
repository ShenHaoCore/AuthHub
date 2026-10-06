namespace AuthHub.Application.Interfaces;

/// <summary>
/// 运行时权限覆盖的**读取**端口（写入见 <see cref="IRolePermissionAdminService"/>）。
///
/// 单独抽一层的目的：让 <see cref="IRolePermissionMap"/> 的实现只负责
/// "三层怎么叠加 + 快照怎么缓存"这段纯逻辑，不掺入数据访问 ——
/// 那段叠加规则（尤其是"空集合不是没有覆盖"）是本项目授权语义的核心，必须能被单元测试直接钉住。
/// </summary>
public interface IRolePermissionOverrideStore
{
    /// <summary>
    /// 读取全部覆盖行（角色名 → 该角色的**完整**权限集合，键不区分大小写）。
    /// 只包含被覆盖过的角色；没有出现在结果里的角色应当回落配置 / 出厂默认。
    /// </summary>
    IReadOnlyDictionary<string, string[]> Load();
}
