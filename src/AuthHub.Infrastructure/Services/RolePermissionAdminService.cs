using AuthHub.Application.Common;
using AuthHub.Application.Interfaces;
using AuthHub.Application.Options;
using AuthHub.Domain.Constants;
using AuthHub.Domain.Entities;
using AuthHub.Domain.Enums;
using AuthHub.Domain.Interfaces;
using AuthHub.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthHub.Infrastructure.Services;

/// <summary>
/// <see cref="IRolePermissionAdminService"/> 的实现：把后台勾选的「角色 → 权限」落成
/// <c>RolePermissionOverrides</c> 里的一行，写完让 <see cref="IRolePermissionMap"/> 的缓存失效。
/// </summary>
/// <remarks>
/// - 整体替换语义：库里的行是该角色权限的完整快照，空数组表示"收回全部权限"，与"没有这一行（回落配置）"是两回事，
///   因此不做增量增删。
/// - 防自锁：变更后必须仍有角色持有 <c>roles.manage</c> —— 一旦没人能进「角色与权限」页，恢复只能去改数据库。
/// - 缓存失效只覆盖本进程：单实例改完即生效，多实例要等各自重启；已登录用户的声明最迟在一个安全戳校验周期
///   （<c>SecurityStampValidatorOptions.ValidationInterval</c>，本项目设为 5 分钟）后刷新。
/// </remarks>
public sealed class RolePermissionAdminService : IRolePermissionAdminService
{
    private readonly AuthHubDbContext _dbContext;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly IAuditLogService _audit;
    private readonly IRolePermissionMap _rolePermissions;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly RolePermissionOptions _baseline;

    public RolePermissionAdminService(
        AuthHubDbContext dbContext,
        RoleManager<ApplicationRole> roleManager,
        IAuditLogService audit,
        IRolePermissionMap rolePermissions,
        ICurrentUser currentUser,
        IClock clock,
        IOptions<RolePermissionOptions> baselineOptions)
    {
        _dbContext = dbContext;
        _roleManager = roleManager;
        _audit = audit;
        _rolePermissions = rolePermissions;
        _currentUser = currentUser;
        _clock = clock;
        _baseline = baselineOptions.Value;
    }

    public async Task<Result> SaveAsync(
        string roleName,
        IReadOnlyCollection<string> permissions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        var name = roleName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return Result.Failure(Error.Validation("缺少角色名。"));
        }

        var role = await _roleManager.FindByNameAsync(name);
        if (role is null || role.Name is null)
        {
            return Result.Failure(Error.NotFound($"角色 {name} 不存在。"));
        }

        // 纵深防御：界面只会提交权限目录里的值，但表单数据一律按不可信处理；
        // 大小写不匹配也拦下 —— 权限名要精确匹配 [Authorize(Policy=...)]。
        var unknown = permissions
            .Where(p => !AuthHubConstants.Permissions.All.Contains(p, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (unknown.Length > 0)
        {
            return Result.Failure(Error.Validation(
                $"提交了未声明的权限：{string.Join("、", unknown)}。" +
                $"合法值：{string.Join("、", AuthHubConstants.Permissions.All)}。" +
                "新增权限点属能力目录变更，需要改 AuthHubConstants.Permissions 并重新部署。"));
        }

        var normalized = permissions
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        var guard = EnsureRolesManageSurvives(role.Name, normalized);
        if (guard.IsFailure)
        {
            return guard;
        }

        var row = await FindOverrideAsync(role.Name, cancellationToken);
        if (row is null)
        {
            row = new RolePermissionOverride { RoleName = role.Name };
            _dbContext.RolePermissionOverrides.Add(row);
        }

        row.Permissions = normalized;
        row.UpdatedAt = _clock.UtcNow;
        row.UpdatedBy = _currentUser.UserName;

        await _dbContext.SaveChangesAsync(cancellationToken);
        _rolePermissions.Invalidate();

        await _audit.LogAsync(
            new AuditEntry(
                AuditActionType.RolePermissionsUpdated,
                true,
                Details: $"角色={role.Name}；权限={Format(normalized)}"),
            cancellationToken);

        return Result.Success();
    }

    public async Task<Result> ResetAsync(string roleName, CancellationToken cancellationToken = default)
    {
        var name = roleName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return Result.Failure(Error.Validation("缺少角色名。"));
        }

        var role = await _roleManager.FindByNameAsync(name);
        if (role is null || role.Name is null)
        {
            return Result.Failure(Error.NotFound($"角色 {name} 不存在。"));
        }

        var row = await FindOverrideAsync(role.Name, cancellationToken);
        if (row is null)
        {
            return Result.Failure(Error.Validation($"角色 {role.Name} 没有自定义权限，已经在沿用配置 / 出厂默认。"));
        }

        var fallback = _baseline.Roles.TryGetValue(role.Name, out var configured)
            ? configured
            : Array.Empty<string>();

        var guard = EnsureRolesManageSurvives(role.Name, fallback);
        if (guard.IsFailure)
        {
            return guard;
        }

        _dbContext.RolePermissionOverrides.Remove(row);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _rolePermissions.Invalidate();

        await _audit.LogAsync(
            new AuditEntry(
                AuditActionType.RolePermissionsReset,
                true,
                Details: $"角色={role.Name}；回落到{Format(fallback)}"),
            cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// 防自锁：把 <paramref name="replacement"/> 当成该角色的新归属，检查变更之后是否还有角色持有 <c>roles.manage</c>。
    /// 传 null 表示"这次不改这个角色"（当前实现用不到，留给将来复用）。
    /// </summary>
    private Result EnsureRolesManageSurvives(string roleName, IReadOnlyCollection<string>? replacement)
    {
        var othersStillHaveIt = _rolePermissions.All
            .Where(pair => !string.Equals(pair.Key, roleName, StringComparison.OrdinalIgnoreCase))
            .Any(pair => pair.Value.Contains(AuthHubConstants.Permissions.RolesManage, StringComparer.OrdinalIgnoreCase));

        if (othersStillHaveIt)
        {
            return Result.Success();
        }

        if (replacement is not null &&
            replacement.Contains(AuthHubConstants.Permissions.RolesManage, StringComparer.OrdinalIgnoreCase))
        {
            return Result.Success();
        }

        return Result.Failure(Error.Validation(
            $"这会让系统里不再有任何角色拥有 {AuthHubConstants.Permissions.RolesManage}，" +
            "之后谁都进不了「角色与权限」页，只能去改数据库才能恢复。" +
            $"请先把 {AuthHubConstants.Permissions.RolesManage} 授予另一个角色，再回来调整 {roleName}。"));
    }

    /// <summary>
    /// 按角色名找覆盖行：表只有几行，整表载入后内存里做不区分大小写的比对。不走 SQL 等值查询 ——
    /// 各数据库默认排序规则对大小写处理不一，同一角色在两种提供程序下会取到不同结果。
    /// </summary>
    private async Task<RolePermissionOverride?> FindOverrideAsync(string roleName, CancellationToken cancellationToken)
    {
        var rows = await _dbContext.RolePermissionOverrides.ToListAsync(cancellationToken);
        return rows.FirstOrDefault(row => string.Equals(row.RoleName, roleName, StringComparison.OrdinalIgnoreCase));
    }

    private static string Format(IReadOnlyCollection<string> permissions)
        => permissions.Count == 0 ? "（无权限）" : string.Join("、", permissions);
}
