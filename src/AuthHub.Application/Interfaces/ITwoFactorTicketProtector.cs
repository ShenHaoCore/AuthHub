namespace AuthHub.Application.Interfaces;

/// <summary>
/// MFA 两阶段登录的中间凭据。第一阶段密码校验通过后，
/// 用受数据保护的一次性票据代替“已完全登录”的会话，避免绕过第二因子。
/// 实现位于 Infrastructure（使用 ASP.NET Core Data Protection）。
/// </summary>
public interface ITwoFactorTicketProtector
{
    /// <summary>生成票据（内含用户 Id、是否记住我、过期时间）。</summary>
    string Protect(string userId, bool rememberMe);

    /// <summary>解出票据内容；无效或已过期时返回 null。</summary>
    TwoFactorTicket? Unprotect(string? token);
}

/// <summary>票据载荷。</summary>
public sealed record TwoFactorTicket(string UserId, bool RememberMe, DateTimeOffset ExpiresAt);
