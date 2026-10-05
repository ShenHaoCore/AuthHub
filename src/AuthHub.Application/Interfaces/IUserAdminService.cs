using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Users;

namespace AuthHub.Application.Interfaces;

/// <summary>用户管理（管理员视角）。</summary>
public interface IUserAdminService
{
    Task<PagedResult<UserDto>> QueryAsync(UserQuery query, CancellationToken cancellationToken = default);

    Task<Result<UserDto>> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<Result<UserDto>> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken = default);

    Task<Result<UserDto>> UpdateAsync(string id, UpdateUserRequest request, CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(string id, CancellationToken cancellationToken = default);

    Task<Result<UserDto>> AssignRolesAsync(string id, AssignRolesRequest request, CancellationToken cancellationToken = default);

    Task<Result<UserDto>> SetLockoutAsync(string id, bool locked, CancellationToken cancellationToken = default);
}
