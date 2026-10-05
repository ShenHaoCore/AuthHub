using System.Security.Claims;
using AuthHub.Api.Extensions;
using AuthHub.Domain.Constants;
using FluentAssertions;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthHub.IntegrationTests;

/// <summary>
/// 声明 → 目标令牌映射的**纯单元测试**（不需要宿主，放在集成测试项目里只是因为它要引用 Api 层）。
///
/// 为什么值得单测：OpenIddict 的默认策略是"没标注目标的声明不进任何令牌"，
/// 这张表写错的症状是**令牌里静默少一个声明**，而不是抛异常 ——
/// 下游拿到令牌后才发现 <c>email</c> 不见了，回头查签发端成本很高。
/// 以前它是控制器里的 private static，只能靠端到端测试间接覆盖。
/// </summary>
public class OpenIddictClaimDestinationsTests
{
    [Theory]
    [InlineData(AuthHubConstants.Scopes.Profile)]
    [InlineData(AuthHubConstants.Scopes.Email)]
    [InlineData(AuthHubConstants.Scopes.Roles)]
    public void Profile_claims_should_always_reach_the_access_token(string grantedScope)
    {
        var principal = PrincipalWithScopes(grantedScope);

        foreach (var type in new[]
                 {
                     Claims.Name, Claims.Email, Claims.Role, AuthHubConstants.ClaimTypes.DisplayName
                 })
        {
            Resolve(type, principal).Should().Contain(Destinations.AccessToken,
                because: $"{type} 是资源服务器做授权判断的依据，与 scope 无关，必须进 Access Token");
        }
    }

    [Fact]
    public void Profile_claims_should_reach_the_identity_token_only_with_matching_scope()
    {
        var profileOnly = PrincipalWithScopes(AuthHubConstants.Scopes.Profile);

        Resolve(Claims.Name, profileOnly).Should().Contain(Destinations.IdentityToken);
        Resolve(Claims.Email, profileOnly).Should().NotContain(
            Destinations.IdentityToken,
            because: "没有 email scope 时邮箱不能进 ID Token");

        var emailOnly = PrincipalWithScopes(AuthHubConstants.Scopes.Email);

        Resolve(Claims.Email, emailOnly).Should().Contain(Destinations.IdentityToken);
        Resolve(Claims.Name, emailOnly).Should().NotContain(Destinations.IdentityToken);
    }

    [Fact]
    public void Permission_claim_should_never_reach_the_identity_token()
    {
        // 权限只用于授权判断；ID Token 是给前端看"我是谁"的，塞进去等于把权限表发给浏览器
        var principal = PrincipalWithScopes(AuthHubConstants.Scopes.Roles);

        var destinations = Resolve(AuthHubConstants.ClaimTypes.Permission, principal).ToArray();

        destinations.Should().Equal(Destinations.AccessToken);
    }

    [Fact]
    public void OpenIddict_managed_claims_should_be_left_alone()
    {
        // 这些由 OpenIddict 自己往正确位置写；显式标注反而会干扰它
        var principal = PrincipalWithScopes(AuthHubConstants.Scopes.Profile);

        foreach (var type in new[]
                 {
                     Claims.Subject, Claims.JwtId, Claims.IssuedAt, Claims.ExpiresAt,
                     Claims.NotBefore, Claims.Issuer, Claims.Audience, Claims.TokenUsage, Claims.ClientId
                 })
        {
            Resolve(type, principal).Should().BeEmpty(
                because: $"{type} 由 OpenIddict 自行处理，标注目标会干扰它");
        }
    }

    [Fact]
    public void Unknown_claims_should_default_to_the_access_token()
    {
        // 宁可多给资源服务器一个声明，也不要静默丢弃（静默丢弃是最难查的一类故障）
        var principal = PrincipalWithScopes(AuthHubConstants.Scopes.Profile);

        Resolve("custom:whatever", principal).Should().Equal(Destinations.AccessToken);
    }

    private static IEnumerable<string> Resolve(string claimType, ClaimsPrincipal principal)
        => OpenIddictClaimDestinations.Resolve(new Claim(claimType, "value"), principal);

    private static ClaimsPrincipal PrincipalWithScopes(params string[] scopes)
    {
        var identity = new ClaimsIdentity("test");
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(scopes);
        return principal;
    }
}
