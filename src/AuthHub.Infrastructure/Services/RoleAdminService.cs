using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Roles;
using AuthHub.Application.Interfaces;
using AuthHub.Application.Mappings;
using AuthHub.Domain.Constants;
using AuthHub.Domain.Entities;
using AuthHub.Domain.Enums;
using AuthHub.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthHub.Infrastructure.Services;

/// <summary>角色管理：负责角色自身的增删改；权限归属的写入在 <see cref="IRolePermissionAdminService"/>。</summary>
/// <remarks>
/// - 「角色 → 权限」的归属由 <see cref="IRolePermissionMap"/> 提供，按优先级叠三层：出厂默认 ← 配置 ← 数据库覆盖。
/// - 归属敢开放可改：权限点（能力目录）与策略名都编译在服务端，能改变的只有"已存在的权限发给谁"，
///   凭空发明不出新能力，调整归属不必发版；新增权限点仍属代码变更，要走发版。
/// - 用户与角色的绑定（谁属于哪个角色）是数据库数据，由用户管理页负责。
/// </remarks>
public sealed class RoleAdminService : IRoleAdminService
{
    private static readonly Dictionary<string, string> PermissionDescriptions = new(StringComparer.Ordinal)
    {
        [AuthHubConstants.Permissions.ClientsManage] = "管理 OAuth 客户端（注册 / 修改 / 轮换密钥 / 删除）",
        [AuthHubConstants.Permissions.ScopesManage] = "管理 Scope 与 API 资源",
        [AuthHubConstants.Permissions.ResourcesManage] = "管理资源白名单（登记 / 启停 / 下线）",
        [AuthHubConstants.Permissions.UsersManage] = "管理用户（创建 / 修改 / 停用 / 分配角色）",
        [AuthHubConstants.Permissions.RolesManage] = "管理角色",
        [AuthHubConstants.Permissions.TokensRevoke] = "撤销令牌 / 强制用户下线",
        [AuthHubConstants.Permissions.AuditRead] = "查看审计日志"
    };

    private readonly AuthHubDbContext _dbContext;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly IAuditLogService _audit;
    private readonly IRolePermissionMap _rolePermissions;

    public RoleAdminService(
        AuthHubDbContext dbContext,
        RoleManager<ApplicationRole> roleManager,
        IAuditLogService audit,
        IRolePermissionMap rolePermissions)
    {
        _dbContext = dbContext;
        _roleManager = roleManager;
        _audit = audit;
        _rolePermissions = rolePermissions;
    }

    public async Task<IReadOnlyCollection<RoleDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var roles = await _dbContext.Roles.AsNoTracking()
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);

        var userCounts = await _dbContext.UserRoles.AsNoTracking()
            .GroupBy(ur => ur.RoleId)
            .Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoleId, x => x.Count, cancellationToken);

        return roles.Select(role => ToDto(role, userCounts.GetValueOrDefault(role.Id))).ToArray();
    }

    public async Task<Result<RoleDto>> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var role = await _roleManager.FindByNameAsync(name);
        if (role is null)
        {
            return Result.Failure<RoleDto>(Error.NotFound($"角色 {name} 不存在。"));
        }

        var userCount = await _dbContext.UserRoles.AsNoTracking().CountAsync(ur => ur.RoleId == role.Id, cancellationToken);
        return Result.Success(ToDto(role, userCount));
    }

    public async Task<Result<RoleDto>> CreateAsync(CreateRoleRequest request, CancellationToken cancellationToken = default)
    {
        if (await _roleManager.FindByNameAsync(request.Name) is not null)
        {
            return Result.Failure<RoleDto>(Error.Conflict($"角色 {request.Name} 已存在。"));
        }

        var role = new ApplicationRole(request.Name)
        {
            Description = request.Description,
            IsSystemRole = _rolePermissions.IsBuiltInRole(request.Name)
        };

        var result = await _roleManager.CreateAsync(role);
        if (!result.Succeeded)
        {
            return Result.Failure<RoleDto>(result.ToError());
        }

        await _audit.LogAsync(
            new AuditEntry(AuditActionType.RoleCreated, true, Details: $"角色={request.Name}"),
            cancellationToken);

        return Result.Success(ToDto(role, 0));
    }

    public async Task<Result<RoleDto>> UpdateAsync(string name, UpdateRoleRequest request, CancellationToken cancellationToken = default)
    {
        var role = await _roleManager.FindByNameAsync(name);
        if (role is null)
        {
            return Result.Failure<RoleDto>(Error.NotFound($"角色 {name} 不存在。"));
        }

        role.Description = request.Description ?? role.Description;

        var result = await _roleManager.UpdateAsync(role);
        if (!result.Succeeded)
        {
            return Result.Failure<RoleDto>(result.ToError());
        }

        await _audit.LogAsync(
            new AuditEntry(AuditActionType.RoleUpdated, true, Details: $"角色={name}"),
            cancellationToken);

        var userCount = await _dbContext.UserRoles.AsNoTracking().CountAsync(ur => ur.RoleId == role.Id, cancellationToken);
        return Result.Success(ToDto(role, userCount));
    }

    public async Task<Result> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        var role = await _roleManager.FindByNameAsync(name);
        if (role is null)
        {
            return Result.Failure(Error.NotFound($"角色 {name} 不存在。"));
        }

        if (role.IsSystemRole || _rolePermissions.IsBuiltInRole(name))
        {
            return Result.Failure(Error.Forbidden($"内置角色 {name} 不允许删除。"));
        }

        var result = await _roleManager.DeleteAsync(role);
        if (!result.Succeeded)
        {
            return Result.Failure(result.ToError());
        }

        // 顺手清掉该角色的权限覆盖行：覆盖行以角色名为主键、没有外键级联，否则同名新角色会"继承"上一任的权限。
        var orphan = await _dbContext.RolePermissionOverrides
            .Where(x => x.RoleName == name)
            .ToListAsync(cancellationToken);

        if (orphan.Count > 0)
        {
            _dbContext.RolePermissionOverrides.RemoveRange(orphan);
            await _dbContext.SaveChangesAsync(cancellationToken);
            _rolePermissions.Invalidate();
        }

        await _audit.LogAsync(
            new AuditEntry(AuditActionType.RoleDeleted, true, Details: $"角色={name}"),
            cancellationToken);

        return Result.Success();
    }

    public IReadOnlyCollection<PermissionDescriptor> GetPermissionCatalog()
        => AuthHubConstants.Permissions.All
            .Select(p => new PermissionDescriptor(p, PermissionDescriptions.GetValueOrDefault(p)))
            .ToArray();

    /// <summary>全部角色当前生效的权限归属（三源叠加后的结果），管理端展示数据与排查授权问题的第一手依据。</summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> GetRolePermissionMap()
        => _rolePermissions.All;

    private RoleDto ToDto(ApplicationRole role, int userCount)
    {
        var dto = role.ToDto();

        return dto with
        {
            Permissions = role.Name is null
                ? Array.Empty<string>()
                : _rolePermissions.GetPermissions(role.Name).ToArray(),
            PermissionsCustomized = role.Name is not null && _rolePermissions.IsCustomized(role.Name),
            UserCount = userCount
        };
    }
}
