namespace AuthHub.Domain.Entities;

/// <summary>
/// 审计日志。记录所有认证授权相关的关键操作（登录、注册、令牌撤销、客户端变更等）。
/// </summary>
public class AuditLog
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>操作类型，取值见 <see cref="Enums.AuditActionType"/>。</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>操作是否成功。失败记录同样重要（用于识别暴力破解）。</summary>
    public bool Succeeded { get; set; }

    /// <summary>发起操作的用户 Id（未登录场景为 null）。</summary>
    public string? UserId { get; set; }

    /// <summary>发起操作的用户名（冗余存储，避免用户被删后无法追溯）。</summary>
    public string? UserName { get; set; }

    /// <summary>涉及的 OAuth 客户端 Id。</summary>
    public string? ClientId { get; set; }

    /// <summary>来源 IP（IPv6 最长 45 字符）。</summary>
    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>补充明细（JSON 或纯文本）。</summary>
    public string? Details { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
