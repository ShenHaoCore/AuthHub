using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Users;
using AuthHub.Application.Interfaces;
using AuthHub.Application.Mappings;
using AuthHub.Domain.Constants;
using AuthHub.Domain.Entities;
using AuthHub.Domain.Enums;
using AuthHub.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthHub.Infrastructure.Services;

/// <summary>
/// 用户管理（管理员视角）。
///
/// 放在 Infrastructure 而不是 Application 的原因：列表查询需要 EF Core 的
/// IQueryable 异步分页（CountAsync / ToListAsync）与多表 join，
/// 让 Application 引用 EF Core 会破坏分层，因此这里承接“查询 + 管理”职责。
/// </summary>
public sealed class UserAdminService : IUserAdminService
{
    private readonly AuthHubDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly IAuditLogService _audit;

    public UserAdminService(
        AuthHubDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        IAuditLogService audit)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _roleManager = roleManager;
        _audit = audit;
    }

    public async Task<PagedResult<UserDto>> QueryAsync(UserQuery query, CancellationToken cancellationToken = default)
    {
        var users = _dbContext.Users.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            users = users.Where(u =>
                (u.UserName != null && u.UserName.Contains(search)) ||
                (u.Email != null && u.Email.Contains(search)) ||
                u.DisplayName.Contains(search));
        }

        if (query.IsActive is { } isActive)
        {
            users = users.Where(u => u.IsActive == isActive);
        }

        if (!string.IsNullOrWhiteSpace(query.Role))
        {
            var role = await _roleManager.FindByNameAsync(query.Role);
            if (role is null)
            {
                return PagedResult<UserDto>.Empty(query.Page, query.PageSize);
            }

            var memberIds = _dbContext.UserRoles
                .Where(ur => ur.RoleId == role.Id)
                .Select(ur => ur.UserId);

            users = users.Where(u => memberIds.Contains(u.Id));
        }

        var total = await users.LongCountAsync(cancellationToken);

        var page = await users
            .OrderBy(u => u.UserName)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        var roleLookup = await LoadRoleLookupAsync(page.Select(u => u.Id).ToArray(), cancellationToken);
        var now = DateTimeOffset.UtcNow;

        var items = page.Select(user => user.ToDto() with
        {
            Roles = roleLookup.TryGetValue(user.Id, out var roles) ? roles : Array.Empty<string>(),
            LockedOut = user.LockoutEnd is not null && user.LockoutEnd > now
        }).ToArray();

        return new PagedResult<UserDto>(items, total, query.Page, query.PageSize);
    }

    public async Task<Result<UserDto>> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
        {
            return Result.Failure<UserDto>(Error.NotFound("用户不存在。"));
        }

        return Result.Success(await ToDtoAsync(user));
    }

    public async Task<Result<UserDto>> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        var roles = request.Roles is { Count: > 0 }
            ? request.Roles.Distinct(StringComparer.Ordinal).ToArray()
            : new[] { AuthHubConstants.Roles.User };

        // 先校验角色是否存在，避免建完用户再回滚
        var invalidRoles = new List<string>();
        foreach (var role in roles)
        {
            if (await _roleManager.FindByNameAsync(role) is null)
            {
                invalidRoles.Add(role);
            }
        }

        if (invalidRoles.Count > 0)
        {
            return Result.Failure<UserDto>(Error.Validation($"角色不存在：{string.Join(", ", invalidRoles)}。"));
        }

        var user = new ApplicationUser
        {
            UserName = request.UserName.Trim(),
            Email = request.Email.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? request.UserName.Trim() : request.DisplayName.Trim(),
            EmailConfirmed = true, // 管理员创建的账号视为已确认
            IsActive = true
        };

        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            return Result.Failure<UserDto>(result.ToError());
        }

        await _userManager.AddToRolesAsync(user, roles);

        await _audit.LogAsync(
            new AuditEntry(AuditActionType.UserCreatedByAdmin, true, user.Id, user.UserName, Details: $"角色：{string.Join(",", roles)}"),
            cancellationToken);

        return Result.Success(await ToDtoAsync(user));
    }

    public async Task<Result<UserDto>> UpdateAsync(string id, UpdateUserRequest request, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
        {
            return Result.Failure<UserDto>(Error.NotFound("用户不存在。"));
        }

        var changes = new List<string>();

        if (request.Email is not null && !string.Equals(request.Email, user.Email, StringComparison.Ordinal))
        {
            user.Email = request.Email.Trim();
            changes.Add($"email -> {user.Email}");
        }

        if (request.DisplayName is not null && !string.Equals(request.DisplayName, user.DisplayName, StringComparison.Ordinal))
        {
            user.DisplayName = request.DisplayName.Trim();
            changes.Add($"displayName -> {user.DisplayName}");
        }

        if (request.PhoneNumber is not null && !string.Equals(request.PhoneNumber, user.PhoneNumber, StringComparison.Ordinal))
        {
            user.PhoneNumber = string.IsNullOrWhiteSpace(request.PhoneNumber) ? null : request.PhoneNumber.Trim();
            changes.Add($"phone -> {user.PhoneNumber}");
        }

        if (request.EmailConfirmed is { } emailConfirmed && emailConfirmed != user.EmailConfirmed)
        {
            user.EmailConfirmed = emailConfirmed;
            changes.Add($"emailConfirmed -> {emailConfirmed}");
        }

        if (request.IsActive is { } isActive && isActive != user.IsActive)
        {
            user.IsActive = isActive;
            changes.Add($"isActive -> {isActive}");

            // 停用账号时同时拉长锁定期，让已有会话在安全戳校验时立即失效
            user.LockoutEnd = isActive ? null : DateTimeOffset.MaxValue;
        }

        if (changes.Count == 0)
        {
            return Result.Success(await ToDtoAsync(user));
        }

        var update = await _userManager.UpdateAsync(user);
        if (!update.Succeeded)
        {
            return Result.Failure<UserDto>(update.ToError());
        }

        if (request.IsActive == false)
        {
            // 强制旧会话失效
            await _userManager.UpdateSecurityStampAsync(user);
        }

        await _audit.LogAsync(
            new AuditEntry(AuditActionType.UserUpdatedByAdmin, true, user.Id, user.UserName, Details: string.Join("；", changes)),
            cancellationToken);

        return Result.Success(await ToDtoAsync(user));
    }

    public async Task<Result> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
        {
            return Result.Failure(Error.NotFound("用户不存在。"));
        }

        if (string.Equals(user.UserName, AuthHubConstants.SeedUsers.AdminUserName, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(Error.Forbidden("内置管理员账号不允许删除。"));
        }

        var result = await _userManager.DeleteAsync(user);
        if (!result.Succeeded)
        {
            return Result.Failure(result.ToError());
        }

        await _audit.LogAsync(
            new AuditEntry(AuditActionType.UserDeletedByAdmin, true, user.Id, user.UserName),
            cancellationToken);

        return Result.Success();
    }

    public async Task<Result<UserDto>> AssignRolesAsync(string id, AssignRolesRequest request, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
        {
            return Result.Failure<UserDto>(Error.NotFound("用户不存在。"));
        }

        var target = request.Roles.Distinct(StringComparer.Ordinal).ToArray();

        var unknown = new List<string>();
        foreach (var role in target)
        {
            if (await _roleManager.FindByNameAsync(role) is null)
            {
                unknown.Add(role);
            }
        }

        if (unknown.Count > 0)
        {
            return Result.Failure<UserDto>(Error.Validation($"角色不存在：{string.Join(", ", unknown)}。"));
        }

        var current = await _userManager.GetRolesAsync(user);

        var toAdd = target.Except(current, StringComparer.Ordinal).ToArray();
        var toRemove = current.Except(target, StringComparer.Ordinal).ToArray();

        if (toAdd.Length > 0)
        {
            var addResult = await _userManager.AddToRolesAsync(user, toAdd);
            if (!addResult.Succeeded) return Result.Failure<UserDto>(addResult.ToError());
        }

        if (toRemove.Length > 0)
        {
            var removeResult = await _userManager.RemoveFromRolesAsync(user, toRemove);
            if (!removeResult.Succeeded) return Result.Failure<UserDto>(removeResult.ToError());
        }

        // 角色变更会影响权限声明，刷新安全戳让旧会话/令牌失效
        await _userManager.UpdateSecurityStampAsync(user);

        await _audit.LogAsync(
            new AuditEntry(
                AuditActionType.RoleAssigned,
                true,
                user.Id,
                user.UserName,
                Details: $"新增：{string.Join(",", toAdd)}；移除：{string.Join(",", toRemove)}；当前：{string.Join(",", target)}"),
            cancellationToken);

        return Result.Success(await ToDtoAsync(user));
    }

    public async Task<Result<UserDto>> SetLockoutAsync(string id, bool locked, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
        {
            return Result.Failure<UserDto>(Error.NotFound("用户不存在。"));
        }

        var result = await _userManager.SetLockoutEndDateAsync(user, locked ? DateTimeOffset.MaxValue : null);
        if (!result.Succeeded)
        {
            return Result.Failure<UserDto>(result.ToError());
        }

        if (locked)
        {
            await _userManager.UpdateSecurityStampAsync(user);
        }

        await _audit.LogAsync(
            new AuditEntry(
                AuditActionType.UserUpdatedByAdmin,
                true,
                user.Id,
                user.UserName,
                Details: locked ? "管理员锁定账号" : "管理员解除锁定"),
            cancellationToken);

        return Result.Success(await ToDtoAsync(user));
    }

    // ------------------------------------------------------------------ 内部辅助

    private async Task<UserDto> ToDtoAsync(ApplicationUser user)
    {
        var roles = await _userManager.GetRolesAsync(user);

        return user.ToDto() with
        {
            Roles = roles.ToArray(),
            LockedOut = await _userManager.IsLockedOutAsync(user)
        };
    }

    private async Task<Dictionary<string, string[]>> LoadRoleLookupAsync(string[] userIds, CancellationToken cancellationToken)
    {
        if (userIds.Length == 0)
        {
            return new Dictionary<string, string[]>();
        }

        var pairs = await (from userRole in _dbContext.UserRoles.AsNoTracking()
                           join role in _dbContext.Roles.AsNoTracking() on userRole.RoleId equals role.Id
                           where userIds.Contains(userRole.UserId) && role.Name != null
                           select new { userRole.UserId, role.Name })
            .ToListAsync(cancellationToken);

        return pairs
            .GroupBy(p => p.UserId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Name!).ToArray());
    }
}
