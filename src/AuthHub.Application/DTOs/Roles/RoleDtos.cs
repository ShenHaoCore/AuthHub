namespace AuthHub.Application.DTOs.Roles;

/// <summary>角色视图模型。</summary>
public sealed record RoleDto
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public bool IsSystemRole { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>该角色当前生效的权限（出厂默认 ← 配置 ← 数据库覆盖，见 IRolePermissionMap）。</summary>
    public IReadOnlyCollection<string> Permissions { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 权限是否来自运行时覆盖（后台界面上改过）。
    /// 为 false 表示当前用的是配置 <c>AuthHub:RolePermissions</c> 或出厂默认。
    /// </summary>
    public bool PermissionsCustomized { get; init; }

    /// <summary>关联用户数。</summary>
    public int UserCount { get; init; }
}

public record CreateRoleRequest(string Name, string? Description = null);

public record UpdateRoleRequest(string? Description = null);

/// <summary>权限目录项，供管理界面渲染可选项。</summary>
public sealed record PermissionDescriptor(string Name, string? Description);
