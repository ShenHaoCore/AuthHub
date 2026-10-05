using System.Security.Claims;
using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Consents;

namespace AuthHub.Application.Interfaces;

/// <summary>
/// 授权同意（consent）读写。
/// 授权端点用它判断“是否需要弹出同意页”，管理端用它做“已授权应用 / 解除授权”。
/// </summary>
public interface IConsentService
{
    /// <summary>构造同意页所需的展示信息（含“是否已授权过”）。</summary>
    Task<Result<ConsentPrompt>> GetPromptAsync(
        string subject,
        string clientId,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken = default);

    /// <summary>记录用户同意：写入一条 permanent authorization，后续同一客户端不再询问。</summary>
    Task<Result> GrantAsync(
        ClaimsPrincipal principal,
        string subject,
        string clientId,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken = default);

    /// <summary>列出某用户已授权的应用。</summary>
    Task<Result<IReadOnlyCollection<ConsentDto>>> ListBySubjectAsync(
        string subject,
        CancellationToken cancellationToken = default);

    /// <summary>解除授权（同时撤销该授权下已颁发的令牌）。</summary>
    Task<Result> RevokeAsync(string authorizationId, CancellationToken cancellationToken = default);
}
