using Oidc = OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthHub.Infrastructure.Services;

/// <summary>
/// OpenIddict 权限项（ept: / gt: / rst: / scp:）与业务概念（授权类型、Scope）之间的翻译。
///
/// 为什么不把 ept:/gt: 这类字符串直接暴露给管理 API：
/// 它们属于 OpenIddict 的内部表达，前端界面和调用方需要的是
/// “允许哪些授权类型、允许哪些 Scope”，翻译放在这里，两边解耦。
/// </summary>
internal static class OpenIddictPermissionTranslator
{
    public static readonly string[] SupportedGrantTypes =
    {
        "authorization_code", "refresh_token", "client_credentials", "password", "implicit"
    };

    /// <summary>根据授权类型、Scope、重定向地址推导出 OpenIddict 权限集合。</summary>
    public static HashSet<string> BuildPermissions(
        IEnumerable<string> grantTypes,
        IEnumerable<string> scopes,
        bool hasRedirectUris,
        bool hasPostLogoutRedirectUris)
    {
        var permissions = new HashSet<string>(StringComparer.Ordinal);

        var grants = grantTypes.ToHashSet(StringComparer.Ordinal);

        var usesAuthorizationEndpoint = grants.Contains("authorization_code") || grants.Contains("implicit");
        var usesTokenEndpoint = grants.Count > 0;

        if (usesAuthorizationEndpoint)
        {
            permissions.Add(Oidc.Permissions.Endpoints.Authorization);
        }

        if (usesTokenEndpoint)
        {
            permissions.Add(Oidc.Permissions.Endpoints.Token);
        }

        if (hasPostLogoutRedirectUris)
        {
            permissions.Add(Oidc.Permissions.Endpoints.Logout);
        }

        if (grants.Contains("authorization_code"))
        {
            permissions.Add(Oidc.Permissions.GrantTypes.AuthorizationCode);
            permissions.Add(Oidc.Permissions.ResponseTypes.Code);
        }

        if (grants.Contains("refresh_token"))
        {
            permissions.Add(Oidc.Permissions.GrantTypes.RefreshToken);
        }

        if (grants.Contains("client_credentials"))
        {
            permissions.Add(Oidc.Permissions.GrantTypes.ClientCredentials);
        }

        if (grants.Contains("password"))
        {
            permissions.Add(Oidc.Permissions.GrantTypes.Password);
        }

        if (grants.Contains("implicit"))
        {
            permissions.Add(Oidc.Permissions.GrantTypes.Implicit);
            permissions.Add(Oidc.Permissions.ResponseTypes.IdToken);
            permissions.Add(Oidc.Permissions.ResponseTypes.Token);
        }

        // 隐式授予 openid（identity token 必需），其余按调用方传入
        foreach (var scope in scopes)
        {
            permissions.Add(Oidc.Permissions.Prefixes.Scope + scope);
        }

        return permissions;
    }

    /// <summary>从权限集合反解授权类型，供管理界面展示。</summary>
    public static string[] ExtractGrantTypes(IEnumerable<string> permissions)
    {
        var set = permissions.ToHashSet(StringComparer.Ordinal);
        var result = new List<string>();

        if (set.Contains(Oidc.Permissions.GrantTypes.AuthorizationCode)) result.Add("authorization_code");
        if (set.Contains(Oidc.Permissions.GrantTypes.RefreshToken)) result.Add("refresh_token");
        if (set.Contains(Oidc.Permissions.GrantTypes.ClientCredentials)) result.Add("client_credentials");
        if (set.Contains(Oidc.Permissions.GrantTypes.Password)) result.Add("password");
        if (set.Contains(Oidc.Permissions.GrantTypes.Implicit)) result.Add("implicit");

        return result.ToArray();
    }

    /// <summary>从权限集合反解 Scope 名称。</summary>
    public static string[] ExtractScopes(IEnumerable<string> permissions)
        => permissions
            .Where(p => p.StartsWith(Oidc.Permissions.Prefixes.Scope, StringComparison.Ordinal))
            .Select(p => p[Oidc.Permissions.Prefixes.Scope.Length..])
            .Where(s => s.Length > 0)
            .ToArray();

    /// <summary>校验授权类型是否在支持范围内。</summary>
    public static bool IsSupportedGrantType(string grantType)
        => SupportedGrantTypes.Contains(grantType, StringComparer.Ordinal);

    /// <summary>随机生成客户端密钥（URL 安全，避免 form 编码歧义）。</summary>
    public static string GenerateSecret(int byteLength = 24)
        => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(byteLength))
                  .ToLowerInvariant();
}
