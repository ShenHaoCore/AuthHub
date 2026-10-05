using System.Security.Claims;
using AuthHub.Domain.Constants;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthHub.Api.Extensions;

/// <summary>
/// 声明 → 目标令牌的映射。
///
/// **为什么值得单独立一个类**：OpenIddict 的默认安全策略是"没标注目标的声明不进入任何令牌"，
/// 所以这张表一旦写错，症状是**令牌里悄悄少一个声明**（而不是报错）——
/// 下游拿到令牌后才发现 <c>sub</c> 在而 <c>email</c> 不在，回头查签发端要花很久。
/// 它是纯函数，从控制器里提出来之后就能直接单测（见 <c>OpenIddictClaimDestinationsTests</c>）。
///
/// 规则：
///   - 用户资料类声明（name / email / role）：按对应 scope 决定是否进 ID Token，
///     但**总是**进 Access Token（资源服务器要靠它做授权判断）；
///   - 权限声明：只进 Access Token —— ID Token 是给前端看"我是谁"的，不承载授权信息；
///   - OpenIddict 自行生成的声明（sub / jti / exp / iss / aud …）：一律不进，
///     显式标注反而会干扰它自己往正确位置写；
///   - 其余未知声明：默认进 Access Token（宁可多给资源服务器，也不要静默丢弃）。
/// </summary>
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
