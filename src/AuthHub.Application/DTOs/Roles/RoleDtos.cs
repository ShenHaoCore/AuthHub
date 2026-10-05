namespace AuthHub.Application.DTOs.Roles;

/// <summary>角色视图模型。</summary>
public sealed record RoleDto
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public bool IsSystemRole { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>该角色拥有的权限（由 RolePermissionMap 推导，只读）。</summary>
    public IReadOnlyCollection<string> Permissions { get; init; } = Array.Empty<string>();

    /// <summary>关联用户数。</summary>
    public int UserCount { get; init; }
}

public record CreateRoleRequest(string Name, string? Description = null);

public record UpdateRoleRequest(string? Description = null);

/// <summary>权限目录项，供管理界面渲染可选项。</summary>
public sealed record PermissionDescriptor(string Name, string? Description);
