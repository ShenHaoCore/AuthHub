using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Roles;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuthHub.Api.Pages.Admin;

/// <summary>权限分组（按 <see cref="AdminNav.PermissionGroupTitle"/> 归类）。</summary>
public sealed record PermissionGroup(string Title, IReadOnlyList<PermissionDescriptor> Permissions);

/// <summary>
/// 角色与权限。
///
/// 重要前提：本项目的「角色 → 权限」是**代码内固定映射**（<c>RolePermissionMap</c>），
/// 不是数据库配置 —— 内置角色对应的就是服务端的授权策略，运行时随意改写会带来提权风险
/// （详见 Infrastructure/Services/RoleAdminService.cs 的类注释）。
///
/// 因此这里的权限树是**只读的可视化**：勾选框刻意 disabled，只用来回答
/// “这个角色到底能做什么”。要调整权限，改代码里的 RolePermissionMap 即可；
/// 页面上也把这条路径写清楚了，避免运维以为界面漏了保存按钮。
/// </summary>
[Authorize(Policy = AuthHubConstants.Policies.Ui.RolesManage)]
public class RolesModel : AdminPageModel
{
    private readonly IRoleAdminService _roles;

    public RolesModel(IRoleAdminService roles)
    {
        _roles = roles;
    }

    [BindProperty] public string? NewRoleName { get; set; }

    [BindProperty] public string? NewRoleDescription { get; set; }

    [BindProperty] public string? EditRoleName { get; set; }

    [BindProperty] public string? EditRoleDescription { get; set; }

    [BindProperty] public string? TargetRoleName { get; set; }

    /// <summary>全部角色。</summary>
    public IReadOnlyCollection<RoleDto> Roles { get; private set; } = Array.Empty<RoleDto>();

    /// <summary>权限目录（按分组整理）。</summary>
    public IReadOnlyList<PermissionGroup> PermissionGroups { get; private set; } = Array.Empty<PermissionGroup>();

    // ------------------------------------------------------------------ 基类钩子（见 AdminPageModel）

    protected override string ListPath => "/admin/roles";

    protected override string? EditingId => EditRoleName;

    /// <summary>角色按名称查找，比较标识时不区分大小写。</summary>
    protected override StringComparison EditingIdComparison => StringComparison.OrdinalIgnoreCase;

    /// <summary>指定角色是否拥有该权限（用于权限树与权限矩阵）。</summary>
    public bool HasPermission(string roleName, string permission)
    {
        var role = Roles.FirstOrDefault(r => string.Equals(r.Name, roleName, StringComparison.OrdinalIgnoreCase));
        return role is not null && role.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>权限矩阵的列（内置角色，按固定顺序）。</summary>
    public IReadOnlyList<RoleDto> BuiltInRoles => Roles
        .Where(role => RolePermissionMap.IsBuiltInRole(role.Name))
        .OrderBy(role => RoleOrder(role.Name))
        .ToArray();

    private static readonly string[] BuiltInRoleOrder = AuthHubConstants.Roles.All.ToArray();

    private static int RoleOrder(string name)
    {
        for (var index = 0; index < BuiltInRoleOrder.Length; index++)
        {
            if (string.Equals(BuiltInRoleOrder[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "角色与权限";
        ViewData["NavKey"] = "roles";

        await LoadDataAsync(cancellationToken);
    }

    // ------------------------------------------------------------------ POST：新建角色

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(NewRoleName))
        {
            return await FailAsync(CreateDialog, Error.Validation("角色名不能为空。"), cancellationToken);
        }

        var result = await _roles.CreateAsync(
            new CreateRoleRequest(NewRoleName.Trim(), TrimToNull(NewRoleDescription)),
            cancellationToken);

        if (result.IsFailure)
        {
            return await FailAsync(CreateDialog, result.Error, cancellationToken);
        }

        return RedirectBackWithSuccess(result.Value.IsSystemRole
            ? $"已创建角色 {result.Value.Name}；它与内置角色同名，会自动继承 RolePermissionMap 里的权限映射。"
            : $"已创建角色 {result.Value.Name}。自定义角色目前不绑定任何内置权限，需要在 RolePermissionMap 中补充映射后才会获得权限。");
    }

    // ------------------------------------------------------------------ POST：修改说明

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(EditRoleName))
        {
            return await FailAsync(EditDialog, Error.Validation("缺少角色名。"), cancellationToken);
        }

        var result = await _roles.UpdateAsync(
            EditRoleName,
            new UpdateRoleRequest(TrimToNull(EditRoleDescription)),
            cancellationToken);

        if (result.IsFailure)
        {
            return await FailAsync(EditDialog, result.Error, cancellationToken);
        }

        return RedirectBackWithSuccess($"已更新角色 {result.Value.Name} 的说明。");
    }

    // ------------------------------------------------------------------ POST：删除角色

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(TargetRoleName))
        {
            return await FailAsync(null, Error.Validation("缺少角色名。"), cancellationToken);
        }

        var result = await _roles.DeleteAsync(TargetRoleName, cancellationToken);
        if (result.IsFailure)
        {
            return await FailAsync(null, result.Error, cancellationToken);
        }

        return RedirectBackWithSuccess($"已删除角色 {TargetRoleName}。");
    }

    // ------------------------------------------------------------------ 内部辅助

    protected override async Task LoadDataAsync(CancellationToken cancellationToken)
    {
        Roles = await _roles.GetAllAsync(cancellationToken);

        PermissionGroups = _roles.GetPermissionCatalog()
            .GroupBy(descriptor => AdminNav.PermissionGroupTitle(descriptor.Name), StringComparer.Ordinal)
            .OrderBy(group => GroupOrder(group.Key))
            .Select(group => new PermissionGroup(group.Key, group.ToArray()))
            .ToArray();
    }

    private static int GroupOrder(string title) => title switch
    {
        "用户" => 0,
        "角色" => 1,
        "客户端" => 2,
        "Scope" => 3,
        "令牌" => 4,
        "审计" => 5,
        _ => 99
    };
}
