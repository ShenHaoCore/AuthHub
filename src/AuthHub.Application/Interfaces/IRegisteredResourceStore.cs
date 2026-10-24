namespace AuthHub.Application.Interfaces;

/// <summary>
/// 已登记（启用中）资源标识的只读出口 —— 资源白名单库表化后的「当前全集」。
///
/// <para>两个消费者:① OpenIddict 资源校验 handler(每个带 resource 参数的授权/令牌请求
/// 都以它为白名单);② CIMD 自动注册客户端时授予哪些 <c>rsrc:</c> 权限。</para>
///
/// <para>每次调用都反映数据库的**最新**状态 —— 资源管理 API 增删/启停后,下一个请求就能看到,
/// 不需要重启,也没有跨实例的缓存失效问题。</para>
/// </summary>
public interface IRegisteredResourceStore
{
    /// <summary>当前启用中的全部资源标识（RFC 8707 绝对 URI 形态）。</summary>
    Task<IReadOnlyCollection<string>> GetEnabledResourcesAsync(CancellationToken cancellationToken = default);
}
