namespace AuthHub.Application.DTOs.Consents;

/// <summary>
/// 同意授权记录。对应 OpenIddictAuthorization 表，
/// 也是“已授权应用”管理页面的数据源。
/// </summary>
public sealed record ConsentDto(
    string AuthorizationId,
    string Subject,
    string ClientId,
    string? ClientDisplayName,
    IReadOnlyCollection<string> Scopes,
    string Status,
    DateTimeOffset? CreatedAt);

/// <summary>授权过程中需要用户确认的上下文（渲染同意页用）。</summary>
public sealed record ConsentPrompt
{
    public string ClientId { get; init; } = string.Empty;

    public string? ClientDisplayName { get; init; }

    public IReadOnlyCollection<string> Scopes { get; init; } = Array.Empty<string>();

    /// <summary>Scope 对应的可读名称（来自 OpenIddictScope 表）。</summary>
    public IReadOnlyCollection<ConsentScopeDescription> ScopeDetails { get; init; } = Array.Empty<ConsentScopeDescription>();

    /// <summary>是否已存在长期授权（无需再次询问）。</summary>
    public bool AlreadyConsented { get; init; }
}

public sealed record ConsentScopeDescription(string Name, string? DisplayName, string? Description);
