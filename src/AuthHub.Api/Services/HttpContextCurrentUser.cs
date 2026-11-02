using System.Security.Claims;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using OpenIddict.Abstractions;

namespace AuthHub.Api.Services;

/// <summary>
/// 从 HttpContext 提取当前调用者信息（用户、客户端、IP、UA）。
/// </summary>
/// <remarks>
/// - 同时接受 Bearer 令牌与 Identity Cookie 两种身份：授权中间件按策略里声明的方案填充
///   <c>HttpContext.User</c>，这里直接读取即可；匿名端点（例如登录页 POST）为未登录状态。
/// </remarks>
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
    /// 用户名。三个候选缺一不可：本项目实际用的是 OpenIddict 的 <c>"name"</c>
    /// （IdentityExtensions 把 UserNameClaimType 对齐到了它），少了这条会话 Cookie 的
    /// <c>UserName</c> 恒为 null，表现是审计日志"谁干的"一栏全空；
    /// <see cref="ClaimTypes.Name"/> 是 Identity 的默认声明类型，<c>preferred_username</c> 是 Bearer 侧等价声明。
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
