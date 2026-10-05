using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Roles;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using AuthHub.Domain.Entities;
using AuthHub.Domain.Enums;
using AuthHub.Infrastructure.Data;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthHub.Infrastructure.Services;

/// <summary>
/// 角色管理。
/// 角色与权限的关系是“代码内固定映射”（<see cref="RolePermissionMap"/>）而非数据库配置：
/// 内置角色对应的是服务端的授权策略，允许运行时随意改写会带来提权风险。
/// 自定义角色可以创建，但目前不授予任何内置权限，保留扩展位。
/// </summary>
public sealed class RoleAdminService : IRoleAdminService
{
    private static readonly Dictionary<string, string> PermissionDescriptions = new(StringComparer.Ordinal)
    {
        [AuthHubConstants.Permissions.ClientsManage] = "管理 OAuth 客户端（注册 / 修改 / 轮换密钥 / 删除）",
        [AuthHubConstants.Permissions.ScopesManage] = "管理 Scope 与 API 资源",
        [AuthHubConstants.Permissions.UsersManage] = "管理用户（创建 / 修改 / 停用 / 分配角色）",
        [AuthHubConstants.Permissions.RolesManage] = "管理角色",
        [AuthHubConstants.Permissions.TokensRevoke] = "撤销令牌 / 强制用户下线",
        [AuthHubConstants.Permissions.AuditRead] = "查看审计日志"
    };

    private readonly AuthHubDbContext _dbContext;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly IMapper _mapper;
    private readonly IAuditLogService _audit;

    public RoleAdminService(
        AuthHubDbContext dbContext,
        RoleManager<ApplicationRole> roleManager,
        IMapper mapper,
        IAuditLogService audit)
    {
        _dbContext = dbContext;
        _roleManager = roleManager;
        _mapper = mapper;
        _audit = audit;
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
            IsSystemRole = RolePermissionMap.IsBuiltInRole(request.Name)
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

        if (role.IsSystemRole || RolePermissionMap.IsBuiltInRole(name))
        {
            return Result.Failure(Error.Forbidden($"内置角色 {name} 不允许删除。"));
        }

        var result = await _roleManager.DeleteAsync(role);
        if (!result.Succeeded)
        {
            return Result.Failure(result.ToError());
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

    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> GetRolePermissionMap()
        => AuthHubConstants.Roles.All.ToDictionary(
            role => role,
            role => (IReadOnlyCollection<string>)RolePermissionMap.GetPermissions(role).ToArray(),
            StringComparer.OrdinalIgnoreCase);

    private RoleDto ToDto(ApplicationRole role, int userCount)
    {
        var dto = _mapper.Map<RoleDto>(role);

        return dto with
        {
            Permissions = role.Name is null
                ? Array.Empty<string>()
                : RolePermissionMap.GetPermissions(role.Name).ToArray(),
            UserCount = userCount
        };
    }
}
