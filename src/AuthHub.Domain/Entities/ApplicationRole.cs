using Microsoft.AspNetCore.Identity;

namespace AuthHub.Domain.Entities;

/// <summary>
/// Identity 角色实体。继承 <see cref="IdentityRole{TKey}"/>（string 主键）。
/// </summary>
public class ApplicationRole : IdentityRole<string>
{
    public ApplicationRole()
    {
        Id = Guid.NewGuid().ToString();
    }

    public ApplicationRole(string roleName) : this()
    {
        Name = roleName;
        NormalizedName = roleName.ToUpperInvariant();
    }

    /// <summary>角色说明，用于管理端展示。</summary>
    public string? Description { get; set; }

    /// <summary>内置角色不允许删除。</summary>
    public bool IsSystemRole { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
