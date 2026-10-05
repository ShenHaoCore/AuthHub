using AuthHub.Application.Common;

namespace AuthHub.Application.DTOs.Users;

/// <summary>用户视图模型。</summary>
public sealed record UserDto
{
    public string Id { get; init; } = string.Empty;

    public string UserName { get; init; } = string.Empty;

    public string? Email { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string? PhoneNumber { get; init; }

    public bool EmailConfirmed { get; init; }

    public bool TwoFactorEnabled { get; init; }

    public bool IsActive { get; init; }

    public bool LockedOut { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastLoginAt { get; init; }

    public IReadOnlyCollection<string> Roles { get; init; } = Array.Empty<string>();
}

/// <summary>管理员创建用户。</summary>
public record CreateUserRequest(
    string UserName,
    string Email,
    string Password,
    string? DisplayName,
    IReadOnlyCollection<string>? Roles = null);

/// <summary>管理员更新用户（只允许改可写字段）。</summary>
public record UpdateUserRequest(
    string? Email = null,
    string? DisplayName = null,
    bool? IsActive = null,
    bool? EmailConfirmed = null,
    string? PhoneNumber = null);

/// <summary>分配角色（全量覆盖）。</summary>
public record AssignRolesRequest(IReadOnlyCollection<string> Roles);

/// <summary>用户列表查询条件。</summary>
public sealed class UserQuery : PagedQuery
{
    public string? Search { get; set; }

    public string? Role { get; set; }

    public bool? IsActive { get; set; }
}
