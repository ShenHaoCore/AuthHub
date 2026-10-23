namespace AuthHub.Domain.Entities;

/// <summary>
/// 已登记的 RFC 8707 资源标识(资源白名单的库表化)。
///
/// <para><b>为什么从配置搬进库</b>:OpenIddict 7.7.2 的资源校验集合是启动时快照,
/// 没有运行时增删接口 —— 资源放在配置里意味着「每接入一个 MCP Server 都要改配置 + 重启」。
/// 库表化后,资源校验直接以本表为准(每请求读启用中的资源),管理 API 改完即时生效;
/// <c>AuthHub:Resources</c> 配置降级为幂等种子(启动时入库,已存在则跳过)。</para>
///
/// <para><b>默认拒绝的红线</b>:校验方向永远是「查库命中才放行」——
/// 表为空时,任何携带 resource 参数的请求都会被 <c>invalid_target</c> 拒绝。
/// <see cref="Enabled"/> 是软开关:置 false 等同未登记(请求拒绝),但保留这条登记记录
/// 与它的显示名/说明,供之后重新启用。</para>
/// </summary>
public class RegisteredResource
{
    /// <summary>代理主键。</summary>
    public long Id { get; set; }

    /// <summary>
    /// 资源标识(绝对 URI,无 fragment,如 https://mcp.example.com/mcp)。
    /// 它会写进令牌的 aud 声明;全局唯一。
    /// </summary>
    public string Resource { get; set; } = string.Empty;

    /// <summary>显示名(后台/API 展示用)。</summary>
    public string? DisplayName { get; set; }

    /// <summary>说明(给管理员看:这个资源对应哪个 MCP Server / 资源服务器)。</summary>
    public string? Description { get; set; }

    /// <summary>是否启用。false = 等同未登记(请求被 invalid_target 拒绝),但保留登记记录。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>登记时间(UTC)。</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>最后一次变更时间(UTC)。</summary>
    public DateTimeOffset? UpdatedAt { get; set; }
}
