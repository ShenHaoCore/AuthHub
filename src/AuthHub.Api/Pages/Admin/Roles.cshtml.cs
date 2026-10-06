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
/// 理解本页的前提是分清授权三层，它们的可变性完全不同：
///   1) **能力目录**（<see cref="AuthHubConstants.Permissions"/>）—— 编译期常量。
///      新增权限点必须改代码并重新部署，因为它对应的是程序集里那道 [Authorize]。
///      本页能做的只是把它渲染成选项，勾不出一个新的权限点。
///   2) **策略名**（<see cref="AuthHubConstants.Policies"/>）—— 同样是编译期常量，
///      由权限点常量拼接而成，控制器 / 页面按策略名引用。
///   3) **角色归属**（本页直接编辑）—— 出厂默认 ← 配置 AuthHub:RolePermissions ← 数据库覆盖，
///      界面上的勾选写进最高那一层，改完即时生效，既不用重启也不用发版。
///
/// 因此权限树是**可勾选**的，但有两个安全护栏值得知道：
///   · 谁能进本页谁就能改归属（本页策略是 roles.manage），所以"给角色加权限"与
///     "把自己变成管理员"之间只差一次点击 —— 每次变更都会落一条审计，这是唯一的追溯手段；
///   · 变更后系统里必须**仍然有角色**持有 roles.manage，否则谁都进不了本页（服务端拒绝这种提交）。
/// 另外，权限声明是登录那一刻写进会话 Cookie 的，已登录用户最迟在一个安全戳校验周期后刷新。
/// </summary>
[Authorize(Policy = AuthHubConstants.Policies.Ui.RolesManage)]
public class RolesModel : AdminPageModel
{
    /// <summary>权限归属弹窗的标识（传给 <see cref="AdminPageModel.IsDialogFailure"/>）。</summary>
    private const string PermissionsDialog = "permissions";

    private readonly IRoleAdminService _roles;
    private readonly IRolePermissionMap _rolePermissions;
    private readonly IRolePermissionAdminService _permissionAdmin;

    public RolesModel(
        IRoleAdminService roles,
        IRolePermissionMap rolePermissions,
        IRolePermissionAdminService permissionAdmin)
    {
        _roles = roles;
        _rolePermissions = rolePermissions;
        _permissionAdmin = permissionAdmin;
    }

    [BindProperty] public string? NewRoleName { get; set; }

    [BindProperty] public string? NewRoleDescription { get; set; }

    [BindProperty] public string? EditRoleName { get; set; }

    [BindProperty] public string? EditRoleDescription { get; set; }

    [BindProperty] public string? TargetRoleName { get; set; }

    /// <summary>权限归属表单提交的角色名。失败重渲染时据此重开对应行的权限弹窗。</summary>
    [BindProperty] public string? PermissionRoleName { get; set; }

    /// <summary>权限归属表单里被勾选的权限。未勾选的复选框不会出现在表单数据里。</summary>
    [BindProperty] public List<string>? SelectedPermissions { get; set; }

    /// <summary>全部角色。</summary>
    public IReadOnlyCollection<RoleDto> Roles { get; private set; } = Array.Empty<RoleDto>();

    /// <summary>权限目录（按分组整理）。</summary>
    public IReadOnlyList<PermissionGroup> PermissionGroups { get; private set; } = Array.Empty<PermissionGroup>();

    // ------------------------------------------------------------------ 基类钩子（见 AdminPageModel）

    protected override string ListPath => "/admin/roles";

    /// <summary>
    /// 正在编辑的标识。三种弹窗（新建 / 编辑说明 / 权限归属）里后两种都指向同一行，
    /// 因此这里取"当前被填进来的那个角色名"。
    /// </summary>
    protected override string? EditingId => EditRoleName ?? PermissionRoleName;

    /// <summary>角色按名称查找，比较标识时不区分大小写。</summary>
    protected override StringComparison EditingIdComparison => StringComparison.OrdinalIgnoreCase;

    /// <summary>指定角色是否拥有该权限（用于权限树与权限矩阵）。</summary>
    public bool HasPermission(string roleName, string permission)
    {
        var role = Roles.FirstOrDefault(r => string.Equals(r.Name, roleName, StringComparison.OrdinalIgnoreCase));
        return role is not null && role.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>指定角色是否内置（视图用；判据是编译期的角色常量，见 <see cref="IRolePermissionMap.IsBuiltInRole"/>）。</summary>
    public bool IsBuiltInRole(string roleName) => _rolePermissions.IsBuiltInRole(roleName);

    /// <summary>该行的权限弹窗是否需要自动打开（上一次保存失败）。</summary>
    public bool AutoOpenPermissions(string roleName) => IsDialogFailure(PermissionsDialog, roleName);

    /// <summary>权限矩阵的列（内置角色，按固定顺序）。</summary>
    public IReadOnlyList<RoleDto> BuiltInRoles => Roles
        .Where(role => _rolePermissions.IsBuiltInRole(role.Name))
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
            ? $"已创建角色 {result.Value.Name}；它与内置角色同名，会沿用出厂默认的权限归属。"
            : $"已创建角色 {result.Value.Name}。自定义角色默认不绑定任何权限，用该行的「权限」按钮勾选后即可生效。");
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

    // ------------------------------------------------------------------ POST：权限归属
    // 权限归属落库（RolePermissionOverrides），优先级高于配置 AuthHub:RolePermissions。
    // 弹窗里是一组复选框，未勾选的在表单里不出现，因此提交上来的就是"该角色最终的完整权限集合"，
    // 服务端按**整体替换**处理 —— 这也正是空集合（全部取消勾选）能表达"收回全部权限"的原因。

    public async Task<IActionResult> OnPostPermissionsAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(PermissionRoleName))
        {
            return await FailAsync(PermissionsDialog, Error.Validation("缺少角色名。"), cancellationToken);
        }

        var result = await _permissionAdmin.SaveAsync(
            PermissionRoleName,
            SelectedPermissions ?? new List<string>(),
            cancellationToken);

        if (result.IsFailure)
        {
            return await FailAsync(PermissionsDialog, result.Error, cancellationToken);
        }

        return RedirectBackWithSuccess(
            $"已更新角色 {PermissionRoleName} 的权限归属，立即对新登录生效；" +
            "已在线的用户最迟 5 分钟内刷新（重新登录可立即生效）。");
    }

    public async Task<IActionResult> OnPostResetPermissionsAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(PermissionRoleName))
        {
            return await FailAsync(PermissionsDialog, Error.Validation("缺少角色名。"), cancellationToken);
        }

        var result = await _permissionAdmin.ResetAsync(PermissionRoleName, cancellationToken);
        if (result.IsFailure)
        {
            return await FailAsync(PermissionsDialog, result.Error, cancellationToken);
        }

        return RedirectBackWithSuccess(
            $"已清除角色 {PermissionRoleName} 的自定义权限，回落到配置 / 出厂默认。");
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
