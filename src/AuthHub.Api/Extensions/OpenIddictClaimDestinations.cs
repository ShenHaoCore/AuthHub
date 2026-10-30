using System.Security.Claims;
using AuthHub.Domain.Constants;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthHub.Api.Extensions;

/// <summary>
/// 声明 → 目标令牌的映射。纯函数，从控制器提出后可直接单测（见 <c>OpenIddictClaimDestinationsTests</c>）。
/// </summary>
/// <remarks>
/// - 这张表写错的症状是令牌里悄悄少一个声明而不是报错（OpenIddict 默认不把未标注目标的声明放进任何令牌）。
/// - 用户资料类声明（name / email / role）：按对应 scope 进 ID Token，但总是进 Access Token
///   （资源服务器要靠它做授权判断）。
/// - 权限声明：只进 Access Token —— ID Token 是给前端看"我是谁"的，不承载授权信息。
/// - OpenIddict 自行生成的声明（sub / jti / exp / iss / aud …）：一律不标，显式标注反而干扰它自己往正确位置写。
/// - 未知声明：默认进 Access Token（宁可多给资源服务器，也不静默丢弃）。
/// </remarks>
public static class OpenIddictClaimDestinations
{
    public static IEnumerable<string> Resolve(Claim claim, ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(principal);

        switch (claim.Type)
        {
            case Claims.Name:
            case AuthHubConstants.ClaimTypes.DisplayName:
                yield return Destinations.AccessToken;

                if (principal.HasScope(AuthHubConstants.Scopes.Profile))
                {
                    yield return Destinations.IdentityToken;
                }

                yield break;

            case Claims.Email:
                yield return Destinations.AccessToken;

                if (principal.HasScope(AuthHubConstants.Scopes.Email))
                {
                    yield return Destinations.IdentityToken;
                }

                yield break;

            case Claims.Role:
                yield return Destinations.AccessToken;

                if (principal.HasScope(AuthHubConstants.Scopes.Roles))
                {
                    yield return Destinations.IdentityToken;
                }

                yield break;

            case AuthHubConstants.ClaimTypes.Permission:
                yield return Destinations.AccessToken;
                yield break;

            case Claims.Subject:
            case Claims.JwtId:
            case Claims.IssuedAt:
            case Claims.ExpiresAt:
            case Claims.NotBefore:
            case Claims.Issuer:
            case Claims.Audience:
            case Claims.TokenUsage:
            case Claims.ClientId:
                yield break;

            default:
                yield return Destinations.AccessToken;
                yield break;
        }
    }
}
