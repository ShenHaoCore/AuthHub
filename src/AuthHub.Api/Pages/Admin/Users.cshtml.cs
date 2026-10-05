using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Roles;
using AuthHub.Application.DTOs.Users;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuthHub.Api.Pages.Admin;

/// <summary>
/// 用户管理。
///
/// 交互遵循 PRG（Post-Redirect-Get）：POST 成功后 302 回列表，刷新页面不会重复提交；
/// POST 失败则原地重新渲染并把弹窗重新打开（<see cref="OpenDialog"/>），用户填过的值不丢。
///
/// 所有写操作都直接调用 Application 层的管理服务，因此与 <c>/api/users</c> 走完全相同的
/// 校验、审计与安全戳刷新逻辑 —— 后台不会成为绕过审计的后门。
/// </summary>
[Authorize(Policy = AuthHubConstants.Policies.Ui.UsersManage)]
public class UsersModel : PageModel
{
    private const int DefaultPageSize = 20;

    private readonly IUserAdminService _users;
    private readonly IRoleAdminService _roles;
    private readonly ITokenAdminService _tokens;

    public UsersModel(IUserAdminService users, IRoleAdminService roles, ITokenAdminService tokens)
    {
        _users = users;
        _roles = roles;
        _tokens = tokens;
    }

    // ------------------------------------------------------------------ 查询条件（GET）

    [BindProperty(SupportsGet = true, Name = "page")]
    public int CurrentPage { get; set; } = 1;

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true, Name = "role")]
    public string? RoleFilter { get; set; }

    /// <summary>启用状态过滤：空 / "true" / "false"。</summary>
    [BindProperty(SupportsGet = true, Name = "active")]
    public string? ActiveFilter { get; set; }

    // ------------------------------------------------------------------ 表单字段（POST）

    [BindProperty] public string? ReturnUrl { get; set; }

    [BindProperty] public string? NewUserName { get; set; }

    [BindProperty] public string? NewEmail { get; set; }

    [BindProperty] public string? NewDisplayName { get; set; }

    [BindProperty] public string? NewPassword { get; set; }

    [BindProperty] public string[] NewRoles { get; set; } = Array.Empty<string>();

    [BindProperty] public string? EditId { get; set; }

    [BindProperty] public string? EditEmail { get; set; }

    [BindProperty] public string? EditDisplayName { get; set; }

    [BindProperty] public string? EditPhoneNumber { get; set; }

    [BindProperty] public bool EditIsActive { get; set; }

    [BindProperty] public bool EditEmailConfirmed { get; set; }

    [BindProperty] public string[] EditRoles { get; set; } = Array.Empty<string>();

    [BindProperty] public string? TargetId { get; set; }

    [BindProperty] public bool Locked { get; set; }

    // ------------------------------------------------------------------ 视图状态

    /// <summary>列表数据。</summary>
    public PagedResult<UserDto> Users { get; private set; } = PagedResult<UserDto>.Empty(1, DefaultPageSize);

    /// <summary>全部角色（用于筛选下拉与编辑弹窗的角色勾选）。</summary>
    public IReadOnlyCollection<RoleDto> AllRoles { get; private set; } = Array.Empty<RoleDto>();

    /// <summary>POST 失败时展示的错误文本。</summary>
    public string? FormError { get; private set; }

    /// <summary>POST 失败时需要重新打开的弹窗：create / edit。</summary>
    public string? OpenDialog { get; private set; }

    /// <summary>当前列表地址（含筛选条件），供 POST 后跳回原位置。</summary>
    public string CurrentUrl => $"{Request.Path}{Request.QueryString}";

    /// <summary>新建弹窗是否需要在页面加载后自动打开（上一次提交失败）。</summary>
    public bool AutoOpenCreate => string.Equals(OpenDialog, "create", StringComparison.Ordinal);

    /// <summary>该行的编辑弹窗是否需要自动打开。</summary>
    public bool IsEditFailure(string userId)
        => string.Equals(OpenDialog, "edit", StringComparison.Ordinal)
           && string.Equals(EditId, userId, StringComparison.Ordinal);

    /// <summary>分页组件模型。</summary>
    public Pages.Shared.PaginationModel Pagination => new()
    {
        Page = Users.Page,
        PageSize = Users.PageSize,
        Total = Users.Total,
        Path = Request.Path,
        Query = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["q"] = Search,
            ["role"] = RoleFilter,
            ["active"] = ActiveFilter
        }
    };

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "用户";
        ViewData["NavKey"] = "users";

        await LoadAsync(cancellationToken);
    }

    // ------------------------------------------------------------------ POST：新建

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(NewUserName) || string.IsNullOrWhiteSpace(NewEmail))
        {
            return await FailAsync("create", Error.Validation("用户名与邮箱为必填项。"), cancellationToken);
        }

        var result = await _users.CreateAsync(
            new CreateUserRequest(
                NewUserName.Trim(),
                NewEmail.Trim(),
                NewPassword ?? string.Empty,
                string.IsNullOrWhiteSpace(NewDisplayName) ? null : NewDisplayName.Trim(),
                NewRoles.Length > 0 ? NewRoles : null),
            cancellationToken);

        if (result.IsFailure)
        {
            return await FailAsync("create", result.Error, cancellationToken);
        }

        TempData["Success"] = $"已创建用户 {result.Value.UserName}（角色：{DescribeRoles(result.Value.Roles)}）。";
        return RedirectBack();
    }

    // ------------------------------------------------------------------ POST：保存编辑

    /// <summary>
    /// 保存编辑弹窗。
    ///
    /// 两点刻意的取舍：
    ///   1) 传给服务的字符串按"null 表示不修改"约定（见 <see cref="UnchangedAsNull"/>），
    ///      否则每次保存都会把未改动的字段记为一次变更，审计日志会被噪声淹没；
    ///   2) 角色只在**确实发生变化**时才调用 AssignRolesAsync —— 该服务每次都刷新安全戳，
    ///      无条件调用会让管理员每保存一次资料就把用户所有会话踢下线。
    /// </summary>
    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(EditId))
        {
            return await FailAsync("edit", Error.Validation("缺少用户标识。"), cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(EditEmail))
        {
            return await FailAsync("edit", Error.Validation("邮箱不能为空。"), cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(EditDisplayName))
        {
            return await FailAsync("edit", Error.Validation("显示名不能为空。"), cancellationToken);
        }

        var current = await _users.GetByIdAsync(EditId, cancellationToken);
        if (current.IsFailure)
        {
            return await FailAsync("edit", current.Error, cancellationToken);
        }

        var existing = current.Value;
        var phone = EditPhoneNumber?.Trim();
        var phoneUnchanged = string.Equals(
            phone ?? string.Empty,
            existing.PhoneNumber ?? string.Empty,
            StringComparison.Ordinal);

        var update = await _users.UpdateAsync(
            EditId,
            new UpdateUserRequest(
                Email: UnchangedAsNull(EditEmail, existing.Email),
                DisplayName: UnchangedAsNull(EditDisplayName, existing.DisplayName),

                // 手机号用空字符串表示“清空”（服务端把它归一为 null）；
                // 未改动时传 null，避免产生一条假的“手机号变更”审计记录。
                PhoneNumber: phoneUnchanged ? null : phone ?? string.Empty,
                IsActive: EditIsActive,
                EmailConfirmed: EditEmailConfirmed),
            cancellationToken);

        if (update.IsFailure)
        {
            return await FailAsync("edit", update.Error, cancellationToken);
        }

        var desired = EditRoles.Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal).ToArray();
        var previous = existing.Roles.OrderBy(r => r, StringComparer.Ordinal).ToArray();

        if (!desired.SequenceEqual(previous, StringComparer.Ordinal))
        {
            var assign = await _users.AssignRolesAsync(EditId, new AssignRolesRequest(desired), cancellationToken);

            if (assign.IsFailure)
            {
                return await FailAsync("edit", assign.Error, cancellationToken);
            }

            TempData["Success"] =
                $"已保存 {existing.UserName} 的资料并更新角色（{DescribeRoles(desired)}）。" +
                "角色变更会刷新安全戳，该用户的旧会话与令牌已失效。";

            return RedirectBack();
        }

        TempData["Success"] = $"已保存 {existing.UserName} 的资料。";
        return RedirectBack();
    }

    // ------------------------------------------------------------------ POST：锁定 / 解锁

    public async Task<IActionResult> OnPostLockAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(TargetId))
        {
            return await FailAsync(null, Error.Validation("缺少用户标识。"), cancellationToken);
        }

        var result = await _users.SetLockoutAsync(TargetId, Locked, cancellationToken);
        if (result.IsFailure)
        {
            return await FailAsync(null, result.Error, cancellationToken);
        }

        TempData["Success"] = Locked
            ? $"已锁定 {result.Value.UserName}，其现有会话已失效。"
            : $"已解除 {result.Value.UserName} 的锁定。";

        return RedirectBack();
    }

    // ------------------------------------------------------------------ POST：强制下线

    public async Task<IActionResult> OnPostRevokeTokensAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(TargetId))
        {
            return await FailAsync(null, Error.Validation("缺少用户标识。"), cancellationToken);
        }

        var result = await _tokens.RevokeBySubjectAsync(TargetId, cancellationToken);
        if (result.IsFailure)
        {
            return await FailAsync(null, result.Error, cancellationToken);
        }

        TempData["Success"] =
            $"已撤销 {result.Value.Tokens} 个令牌与 {result.Value.Authorizations} 条授权。未过期的访问令牌也会立即失效。";

        return RedirectBack();
    }

    // ------------------------------------------------------------------ POST：删除

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(TargetId))
        {
            return await FailAsync(null, Error.Validation("缺少用户标识。"), cancellationToken);
        }

        var result = await _users.DeleteAsync(TargetId, cancellationToken);
        if (result.IsFailure)
        {
            return await FailAsync(null, result.Error, cancellationToken);
        }

        TempData["Success"] = "用户已删除。";
        return RedirectBack();
    }

    // ------------------------------------------------------------------ 内部辅助

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        AllRoles = await _roles.GetAllAsync(cancellationToken);

        Users = await _users.QueryAsync(
            new UserQuery
            {
                Page = CurrentPage,
                PageSize = DefaultPageSize,
                Search = Search,
                Role = RoleFilter,
                IsActive = ParseActive(ActiveFilter)
            },
            cancellationToken);
    }

    private static bool? ParseActive(string? value)
        => value switch
        {
            "true" => true,
            "false" => false,
            _ => null
        };

    /// <summary>提交值与当前值相同则返回 null（服务端约定 null = 不修改）。</summary>
    private static string? UnchangedAsNull(string? submitted, string? current)
    {
        var value = submitted?.Trim();
        return string.Equals(value ?? string.Empty, current ?? string.Empty, StringComparison.Ordinal)
            ? null
            : value;
    }

    /// <summary>退回列表页（只允许站内地址，防开放重定向）。</summary>
    private IActionResult RedirectBack()
        => Redirect(!string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
            ? ReturnUrl
            : "/admin/users");

    private async Task<IActionResult> FailAsync(string? dialog, Error error, CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);

        FormError = error.Message;
        OpenDialog = dialog;

        return Page();
    }

    private static string DescribeRoles(IReadOnlyCollection<string> roles)
        => roles.Count == 0 ? "无" : string.Join("、", roles);
}
