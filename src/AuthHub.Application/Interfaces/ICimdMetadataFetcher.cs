namespace AuthHub.Application.Interfaces;

/// <summary>
/// 拉取 CIMD 元数据文档（<c>client_id</c> 指向的 HTTPS 地址）。
///
/// <para>抽成接口不是为了"将来可能换实现"，而是为了**把 SSRF 防护与业务流程分开测试**：
/// 防护的每一条（仅 https、拒私网、限跳数、限体积、超时）用真实地址策略做单元测试；
/// 而"拉到元数据之后怎么注册客户端"用假拉取器做端到端测试 —— 后者不可能真去请求一个
/// 公网域名（测试不该依赖外部网络，也不该把 CI 变成出站扫描器）。</para>
/// </summary>
public interface ICimdMetadataFetcher
{
    /// <summary>
    /// 拉取并返回原始 JSON 文本。失败时返回带原因的结果，**不抛异常**
    /// （调用方需要按"拉取失败 → 降级用缓存"处理，异常会打断这条路径）。
    /// </summary>
    Task<CimdFetchResult> FetchAsync(Uri metadataUri, CancellationToken cancellationToken = default);
}

/// <summary>元数据拉取结果。</summary>
/// <param name="Document">成功时的原始 JSON 文本（未解析；解析与校验属于下一步）。</param>
/// <param name="Error">失败原因（用于日志与审计，不含敏感信息）。</param>
public sealed record CimdFetchResult(string? Document, string? Error)
{
    public bool IsSuccess => Document is not null;

    public static CimdFetchResult Success(string document) => new(document, null);

    public static CimdFetchResult Failure(string error) => new(null, error);
}
