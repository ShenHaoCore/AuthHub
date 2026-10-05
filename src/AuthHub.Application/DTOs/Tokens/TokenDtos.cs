namespace AuthHub.Application.DTOs.Tokens;

/// <summary>令牌摘要信息（不返回令牌明文，只返回元数据）。</summary>
public sealed record TokenInfoDto(
    string Id,
    string? Subject,
    string? ClientId,
    string Type,
    string Status,
    DateTimeOffset? CreationDate,
    DateTimeOffset? ExpirationDate);

/// <summary>按用户 / 客户端批量撤销令牌。</summary>
public record RevokeTokensRequest(string? Subject = null, string? ClientId = null);

/// <summary>撤销结果。</summary>
public sealed record RevokeTokensResponse(int RevokedTokenCount, int RevokedAuthorizationCount);

/// <summary>清理已过期/失效令牌的请求。</summary>
public record PruneTokensRequest(int OlderThanDays = 30);
