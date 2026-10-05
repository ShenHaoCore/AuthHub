using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Roles;

namespace AuthHub.Application.Interfaces;

/// <summary>角色管理。</summary>
public interface IRoleAdminService
{
    Task<IReadOnlyCollection<RoleDto>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Result<RoleDto>> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    Task<Result<RoleDto>> CreateAsync(CreateRoleRequest request, CancellationToken cancellationToken = default);

    Task<Result<RoleDto>> UpdateAsync(string name, UpdateRoleRequest request, CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>权限目录（角色可用权限的全集）。</summary>
    IReadOnlyCollection<PermissionDescriptor> GetPermissionCatalog();

    /// <summary>角色与权限的映射关系（只读，展示用）。</summary>
    IReadOnlyDictionary<string, IReadOnlyCollection<string>> GetRolePermissionMap();
}
