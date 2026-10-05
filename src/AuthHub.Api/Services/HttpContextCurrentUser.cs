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

    public string? UserName
        => Principal?.FindFirst(ClaimTypes.Name)?.Value
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
