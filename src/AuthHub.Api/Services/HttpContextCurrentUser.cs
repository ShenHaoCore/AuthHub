using System.Security.Claims;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using OpenIddict.Abstractions;

namespace AuthHub.Api.Services;

/// <summary>
/// 从 HttpContext 提取当前调用者信息（用户、客户端、IP、UA）。
///
/// 说明：本服务同时接受 Bearer 令牌与 Identity Cookie 两种身份，
/// 授权中间件会按策略里声明的认证方案填充 <c>HttpContext.User</c>，
/// 因此这里直接读取即可；匿名端点（例如登录页 POST）则为未登录状态。
/// </summary>
public sealed class HttpContextCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public HttpContextCurrentUser(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    private HttpContext? Context => _accessor.HttpContext;

    private ClaimsPrincipal? Principal => Context?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public string? UserId
        => Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value
           ?? Principal?.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;

    /// <summary>
    /// 用户名。三个候选类型缺一不可：
    ///   · <see cref="ClaimTypes.Name"/> —— 默认的 Identity 声明类型；
    ///   · <c>OpenIddictConstants.Claims.Name</c>（<c>"name"</c>）—— **本项目实际用的就是这个**：
    ///     IdentityExtensions 把 <c>ClaimsIdentity.UserNameClaimType</c> 对齐到了 OpenIddict 的
    ///     <c>"name"</c>。少了这一条，会话 Cookie 的 <c>UserName</c> 会恒为 null，
    ///     表现是审计日志里"谁干的"一栏全空（UserId 还在，所以不容易被注意到）。
    ///   · <c>preferred_username</c> —— Bearer 令牌侧的等价声明。
    /// </summary>
    public string? UserName
        => Principal?.FindFirst(ClaimTypes.Name)?.Value
           ?? Principal?.FindFirst(OpenIddictConstants.Claims.Name)?.Value
           ?? Principal?.FindFirst(OpenIddictConstants.Claims.PreferredUsername)?.Value;

    public string? ClientId
        => Principal?.FindFirst(OpenIddictConstants.Claims.ClientId)?.Value
           ?? Principal?.FindFirst("oi_cli")?.Value;

    public string? IpAddress
        => Context?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent
    {
        get
        {
            var agent = Context?.Request.Headers.UserAgent.ToString();
            return string.IsNullOrWhiteSpace(agent) ? null : agent;
        }
    }

    public IReadOnlyCollection<string> Roles
        => Principal is null
            ? Array.Empty<string>()
            : Principal.FindAll(ClaimTypes.Role)
                       .Concat(Principal.FindAll(OpenIddictConstants.Claims.Role))
                       .Select(c => c.Value)
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToArray();

    public IReadOnlyCollection<string> Permissions
        => Principal?.FindAll(AuthHubConstants.ClaimTypes.Permission)
                     .Select(c => c.Value)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .ToArray()
           ?? Array.Empty<string>();
}
