namespace AuthHub.Application.Interfaces;

/// <summary>
/// 当前请求上下文信息（用户、客户端、IP、UA）。
/// 接口定义在 Application 层，实现放在 Api 层（依赖 HttpContext）。
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    string? UserId { get; }

    string? UserName { get; }

    /// <summary>当前令牌所属的 OAuth 客户端 Id（M2M 场景下有值）。</summary>
    string? ClientId { get; }

    string? IpAddress { get; }

    string? UserAgent { get; }

    IReadOnlyCollection<string> Roles { get; }

    IReadOnlyCollection<string> Permissions { get; }
}
