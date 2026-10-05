using System.Security.Claims;
using AuthHub.Api.Extensions;
using AuthHub.Api.Models;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;

namespace AuthHub.Api.Controllers;

/// <summary>
/// 受保护资源示例 —— 展示“下游 API 如何基于令牌做鉴权”。
///
/// 鉴权要素：
///   1) 策略 <c>AuthHub.ApiAccess</c> 要求携带 <c>api:read</c> 或 <c>api:write</c> 的 Bearer 令牌；
///   2) 令牌内的 <c>authhub:permission</c> 声明可用于更细粒度的判断（见 /api/profile/permissions）；
///   3) 写操作额外要求 <c>api:write</c>（见 POST /api/profile/echo）。
/// </summary>
[Tags("受保护资源示例")]
[Route("api/profile")]
[Authorize(Policy = AuthHubConstants.Policies.ApiAccess)]
[Produces("application/json")]
public class ProfileController : ApiControllerBase
{
    private readonly ICurrentUser _currentUser;

    public ProfileController(ICurrentUser currentUser)
    {
        _currentUser = currentUser;
    }

    /// <summary>返回令牌所代表的身份概要。</summary>
    [EndpointSummary("查询令牌身份概要")]
    [EndpointDescription("返回令牌的主体、客户端、角色、权限与 Scope。")]
    [HttpGet]
    public IActionResult Get()
        => Ok(new
        {
            subject = _currentUser.UserId,
            userName = _currentUser.UserName,
            clientId = _currentUser.ClientId,
            roles = _currentUser.Roles,
            permissions = _currentUser.Permissions,
            scopes = CurrentScopes(),
            authenticatedAt = DateTimeOffset.UtcNow,
            message = "你已通过 AuthHub 颁发的 Access Token 成功访问受保护资源。"
        });

    /// <summary>令牌中的全部声明，便于排查 claims 映射问题。</summary>
    [EndpointSummary("查询令牌原始声明")]
    [EndpointDescription("返回令牌里的全部 claim，便于排查映射问题。")]
    [HttpGet("claims")]
    public IActionResult GetClaims()
        => Ok(User.Claims.Select(c => new { type = c.Type, value = c.Value }));

    /// <summary>需要 api:write 的写操作示例（只读令牌访问会返回 403）。</summary>
    [EndpointSummary("写操作示例")]
    [EndpointDescription("需要 api:write；只有只读权限的令牌会返回 403。")]
    [HttpPost("echo")]
    public IActionResult Echo([FromBody] EchoRequest request)
    {
        if (!User.HasScope(AuthHubConstants.Scopes.ApiWrite))
        {
            return Problem(Application.Common.Result.Failure(
                Application.Common.Error.Forbidden("该接口需要 api:write 权限，当前令牌仅具备只读权限。")));
        }

        return Ok(new
        {
            echoed = request.Message,
            clientId = _currentUser.ClientId,
            wroteAt = DateTimeOffset.UtcNow
        });
    }

    private string[] CurrentScopes()
        => User.Claims
            .Where(c => c.Type == OpenIddictConstants.Claims.Scope)
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}

public sealed record EchoRequest(string Message);
