namespace AuthHub.Application.Interfaces;

/// <summary>
/// CIMD 客户端解析的三种结局。
/// </summary>
public enum CimdResolutionStatus
{
    /// <summary>不是 CIMD 场景：<c>client_id</c> 不是合规的元数据 URL，或该 client_id 已手工登记。放行。</summary>
    NotApplicable = 0,

    /// <summary>已确认是 CIMD 客户端且注册/缓存有效。放行。</summary>
    Allowed = 1,

    /// <summary>是 CIMD 客户端但注册被拒（元数据不合法、被禁用、拉取失败且无可用缓存）。</summary>
    Rejected = 2
}

/// <summary>CIMD 客户端解析结果。</summary>
/// <param name="Status">三种结局之一，决定中间件是放行还是拒绝。</param>
/// <param name="Error">被拒时的原因（会回给调用方，因此不能含服务端内部细节）。</param>
public sealed record CimdResolution(CimdResolutionStatus Status, string? Error = null)
{
    /// <summary>不适用于本次请求，交给 OpenIddict 的内置校验。</summary>
    public static readonly CimdResolution NotApplicable = new(CimdResolutionStatus.NotApplicable);

    /// <summary>可以继续。</summary>
    public static readonly CimdResolution Allowed = new(CimdResolutionStatus.Allowed);

    /// <summary>拒绝。</summary>
    public static CimdResolution Rejected(string error) => new(CimdResolutionStatus.Rejected, error);
}

/// <summary>保证"URL 型 client_id"在进入 OpenIddict 校验链之前已经是一条可用的应用记录。</summary>
/// <remarks>
/// - OpenIddict 的授权请求校验第一步就是按 <c>client_id</c> 查应用，查不到直接拒；CIMD 的价值恰恰是"没登记过也能用"，因此必须在它之前把事情做完。实现见 <c>CimdClientRegistrar</c>。
/// </remarks>
public interface ICimdClientRegistrar
{
    Task<CimdResolution> ResolveAsync(string clientId, CancellationToken cancellationToken = default);
}
