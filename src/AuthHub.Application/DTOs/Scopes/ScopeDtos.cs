namespace AuthHub.Application.DTOs.Scopes;

/// <summary>Scope 视图模型。</summary>
public sealed record ScopeDto
{
    public string Name { get; init; } = string.Empty;

    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    /// <summary>该 Scope 关联的 API 资源（会进入令牌 aud）。</summary>
    public IReadOnlyCollection<string> Resources { get; init; } = Array.Empty<string>();
}

public record CreateScopeRequest(
    string Name,
    string? DisplayName = null,
    string? Description = null,
    IReadOnlyCollection<string>? Resources = null);

public record UpdateScopeRequest(
    string? DisplayName = null,
    string? Description = null,
    IReadOnlyCollection<string>? Resources = null);
