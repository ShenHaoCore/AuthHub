using AuthHub.Application.Common;

namespace AuthHub.Application.Interfaces;

/// <summary>
/// 令牌撤销与黑名单。内部使用 <c>IOpenIddictTokenManager</c> /
/// <c>IOpenIddictAuthorizationManager</c> 把令牌标记为 revoked，
/// 资源服务器校验时会检查该状态，因此“未过期但已撤销”的令牌同样失效。
/// </summary>
public interface ITokenAdminService
{
    /// <summary>强制某用户下线：撤销其全部令牌与授权。</summary>
    Task<Result<RevokeResult>> RevokeBySubjectAsync(string subject, CancellationToken cancellationToken = default);

    /// <summary>撤销某客户端的全部令牌。</summary>
    Task<Result<RevokeResult>> RevokeByClientAsync(string clientId, CancellationToken cancellationToken = default);

    /// <summary>按令牌引用 Id（reference_id）撤销单个令牌。</summary>
    Task<Result> RevokeByReferenceIdAsync(string referenceId, CancellationToken cancellationToken = default);

    /// <summary>清理早于指定时间且已失效的令牌记录。</summary>
    Task<Result<int>> PruneAsync(TimeSpan olderThan, CancellationToken cancellationToken = default);
}

/// <summary>批量撤销的统计结果。</summary>
public sealed record RevokeResult(int Tokens, int Authorizations);
