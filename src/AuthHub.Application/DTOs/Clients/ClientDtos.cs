using AuthHub.Application.Common;

namespace AuthHub.Application.DTOs.Clients;

/// <summary>OAuth 客户端视图模型。不含密钥（密钥只存哈希，永不回读）。</summary>
public sealed record ClientDto
{
    public string ClientId { get; init; } = string.Empty;

    public string? DisplayName { get; init; }

    /// <summary>web / native / 空（空表示纯机器客户端）。</summary>
    public string? ApplicationType { get; init; }

    /// <summary>confidential / public。</summary>
    public string? ClientType { get; init; }

    /// <summary>explicit / implicit / systematic / external。</summary>
    public string? ConsentType { get; init; }

    public IReadOnlyCollection<string> RedirectUris { get; init; } = Array.Empty<string>();

    public IReadOnlyCollection<string> PostLogoutRedirectUris { get; init; } = Array.Empty<string>();

    /// <summary>OpenIddict 原始权限项（ept: / gt: / rst: / scp: 前缀）。</summary>
    public IReadOnlyCollection<string> Permissions { get; init; } = Array.Empty<string>();

    public IReadOnlyCollection<string> Requirements { get; init; } = Array.Empty<string>();

    /// <summary>是否已配置客户端密钥。</summary>
    public bool HasSecret { get; init; }

    /// <summary>便于前端展示的授权类型（由 Permissions 反解）。</summary>
    public IReadOnlyCollection<string> GrantTypes { get; init; } = Array.Empty<string>();

    /// <summary>便于前端展示的可用 Scope（由 Permissions 反解）。</summary>
    public IReadOnlyCollection<string> AllowedScopes { get; init; } = Array.Empty<string>();
}

/// <summary>创建客户端。不传 ClientSecret 时自动生成，并在响应中一次性返回。</summary>
public record CreateClientRequest(
    string ClientId,
    string? DisplayName = null,
    string? ClientSecret = null,
    string? ApplicationType = null,
    string? ClientType = null,
    IReadOnlyCollection<string>? RedirectUris = null,
    IReadOnlyCollection<string>? PostLogoutRedirectUris = null,
    IReadOnlyCollection<string>? GrantTypes = null,
    IReadOnlyCollection<string>? Scopes = null,
    bool RequirePkce = true,
    bool RequireConsent = false);

/// <summary>更新客户端。null 表示“不修改该字段”。</summary>
public record UpdateClientRequest(
    string? DisplayName = null,
    IReadOnlyCollection<string>? RedirectUris = null,
    IReadOnlyCollection<string>? PostLogoutRedirectUris = null,
    IReadOnlyCollection<string>? GrantTypes = null,
    IReadOnlyCollection<string>? Scopes = null,
    bool? RequirePkce = null,
    bool? RequireConsent = null);

/// <summary>创建结果：客户端信息 + 明文密钥（仅此一次返回）。</summary>
public sealed record ClientCreatedResponse(ClientDto Client, string? PlainTextSecret);

/// <summary>轮换密钥结果。</summary>
public sealed record ClientSecretResponse(string ClientId, string PlainTextSecret);
