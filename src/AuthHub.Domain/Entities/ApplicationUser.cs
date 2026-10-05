using Microsoft.AspNetCore.Identity;

namespace AuthHub.Domain.Entities;

/// <summary>
/// Identity 用户实体。继承 <see cref="IdentityUser{TKey}"/>（string 主键）。
/// </summary>
public class ApplicationUser : IdentityUser<string>
{
    public ApplicationUser()
    {
        Id = Guid.NewGuid().ToString();
        SecurityStamp = Guid.NewGuid().ToString();
    }

    /// <summary>显示名称（会作为 name 声明进入 ID Token）。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>是否启用。停用后无法登录，已颁发令牌在下次校验时被拒绝。</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>最近一次成功登录时间（UTC）。</summary>
    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>最近一次成功登录的 IP，用于安全审计。</summary>
    public string? LastLoginIp { get; set; }
}
