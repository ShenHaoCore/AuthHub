using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Scopes;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuthHub.Api.Pages.Admin;

/// <summary>
/// OAuth Scope 管理（`IScopeAdminService` → `IOpenIddictScopeManager`）。
///
/// 三个需要向使用者交代清楚的点，页面上也各有对应提示：
///
/// 1) **`openid` / `offline_access` 不在这张表里**。它们由 OpenIddict 内部处理，
///    不落 `OpenIddictScope` 表，所以列表里看不到 —— 但客户端的 `scp:` 权限里
///    确实会有。看不到不等于不存在，因此加了说明，避免管理员去"补建"一个同名的。
///
/// 2) **Scope 名称是主键、不可改**。`UpdateScopeRequest` 里没有 Name 字段，
///    编辑弹窗里的名称是只读的；要改名只能删掉重建。
///
/// 3) **删除不会级联清理客户端的 `scp:` 权限**。OpenIddict 不做引用完整性检查，
///    删掉一个仍被客户端引用的 scope，会让那些客户端的授权请求在下一步
///    "scope 不在允许列表里"而失败。所以列表里给出引用计数，删除前明确警告。
/// </summary>
[Authorize(Policy = AuthHubConstants.Policies.Ui.ScopesManage)]
public class ScopesModel : PageModel
{
    /// <summary>
    /// 统计引用时一次拉取的客户端上限（`IClientAdminService` 内部上限为 200）。
    /// 超过这个数则计数为下界，界面上以 "≥" 标注 —— 不假装精确。
    /// </summary>
    private const int ReferenceScanSize = 200;

    /// <summary>种子数据创建的 scope。删除它们会连带打断已签发的客户端，单独标记出来。</summary>
    private static readonly HashSet<string> BuiltInScopes = new(StringComparer.Ordinal)
    {
        AuthHubConstants.Scopes.Profile,
        AuthHubConstants.Scopes.Email,
        AuthHubConstants.Scopes.Roles,
        AuthHubConstants.Scopes.ApiRead,
        AuthHubConstants.Scopes.ApiWrite,
        AuthHubConstants.Scopes.Admin
    };

    private readonly IScopeAdminService _scopes;
    private readonly IClientAdminService _clients;

    public ScopesModel(IScopeAdminService scopes, IClientAdminService clients)
    {
        _scopes = scopes;
        _clients = clients;
    }

    // ------------------------------------------------------------------ 查询条件（GET）

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Search { get; set; }

    // ------------------------------------------------------------------ 表单字段（POST）

    [BindProperty] public string? ReturnUrl { get; set; }

    [BindProperty] public string? NewName { get; set; }

    [BindProperty] public string? NewDisplayName { get; set; }

    [BindProperty] public string? NewDescription { get; set; }

    [BindProperty] public string? NewResources { get; set; }

    [BindProperty] public string? EditName { get; set; }

    [BindProperty] public string? EditDisplayName { get; set; }

    [BindProperty] public string? EditDescription { get; set; }

    [BindProperty] public string? EditResources { get; set; }

    [BindProperty] public string? TargetName { get; set; }

    // ------------------------------------------------------------------ 视图状态

    public IReadOnlyList<ScopeDto> Scopes { get; private set; } = Array.Empty<ScopeDto>();

    /// <summary>Scope 名 → 引用它的客户端数量。</summary>
    public IReadOnlyDictionary<string, int> ReferenceCounts { get; private set; }
        = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>客户端总数超过扫描上限时，引用计数只是下界。</summary>
    public bool ReferenceCountsTruncated { get; private set; }

    /// <summary>当前筛选命中的 scope 数量（用于空态文案区分"没有数据"与"没有匹配"）。</summary>
    public int TotalCount { get; private set; }

    public string? FormError { get; private set; }

    /// <summary>POST 失败时需要重新打开的弹窗：create / edit。</summary>
    public string? OpenDialog { get; private set; }

    public bool AutoOpenCreate => string.Equals(OpenDialog, "create", StringComparison.Ordinal);

    public bool HasFilter => !string.IsNullOrWhiteSpace(Search);

    public string CurrentUrl => $"{Request.Path}{Request.QueryString}";

    public bool IsEditFailure(string name)
        => string.Equals(OpenDialog, "edit", StringComparison.Ordinal)
           && string.Equals(EditName, name, StringComparison.Ordinal);

    public static bool IsBuiltIn(string name) => BuiltInScopes.Contains(name);

    /// <summary>某个 Scope 被多少个客户端引用（用于删除前提示）。</summary>
    public int ReferenceCount(string name)
        => ReferenceCounts.TryGetValue(name, out var count) ? count : 0;

    /// <summary>引用计数的展示文本（超出扫描范围时标 ≥）。</summary>
    public string ReferenceText(string name)
    {
        var count = ReferenceCount(name);
        if (count == 0)
        {
            return "未被引用";
        }

        return ReferenceCountsTruncated ? $"≥{count} 个客户端" : $"{count} 个客户端";
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "Scope";
        ViewData["NavKey"] = "scopes";

        await LoadAsync(cancellationToken);
    }

    // ------------------------------------------------------------------ POST：新建

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(NewName))
        {
            return await FailAsync("create", Error.Validation("Scope 名称不能为空。"), cancellationToken);
        }

        var result = await _scopes.CreateAsync(
            new CreateScopeRequest(
                Name: NewName.Trim(),
                DisplayName: string.IsNullOrWhiteSpace(NewDisplayName) ? null : NewDisplayName.Trim(),
                Description: string.IsNullOrWhiteSpace(NewDescription) ? null : NewDescription.Trim(),
                Resources: SplitLines(NewResources)),
            cancellationToken);

        if (result.IsFailure)
        {
            return await FailAsync("create", result.Error, cancellationToken);
        }

        TempData["Success"] = $"已创建 Scope {result.Value.Name}。还记得把它加进需要它的客户端，否则授权请求会因 scope 未授权而失败。";
        return RedirectBack();
    }

    // ------------------------------------------------------------------ POST：保存编辑

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(EditName))
        {
            return await FailAsync("edit", Error.Validation("缺少 Scope 名称。"), cancellationToken);
        }

        // 服务端的 UpdateScopeRequest 是 "null 表示不修改" 语义（DisplayName/Description 都做了 ?? 兜底），
        // 所以这里把空串转成 null 而不是空值 —— 否则会被当成"改成空"却实际保留了旧值，行为反直觉。
        var result = await _scopes.UpdateAsync(
            EditName,
            new UpdateScopeRequest(
                DisplayName: string.IsNullOrWhiteSpace(EditDisplayName) ? null : EditDisplayName.Trim(),
                Description: string.IsNullOrWhiteSpace(EditDescription) ? null : EditDescription.Trim(),
                // 资源列表是整体覆盖：表单里预填了现有值，用户清空即表示"不再关联任何资源"
                Resources: SplitLines(EditResources)),
            cancellationToken);

        if (result.IsFailure)
        {
            return await FailAsync("edit", result.Error, cancellationToken);
        }

        TempData["Success"] = $"已保存 Scope {EditName}。资源变更会在下一次签发令牌时生效（已签发令牌的 aud 不变）。";
        return RedirectBack();
    }

    // ------------------------------------------------------------------ POST：删除

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(TargetName))
        {
            return await FailAsync(null, Error.Validation("缺少 Scope 名称。"), cancellationToken);
        }

        var result = await _scopes.DeleteAsync(TargetName, cancellationToken);
        if (result.IsFailure)
        {
            return await FailAsync(null, result.Error, cancellationToken);
        }

        TempData["Success"] = $"已删除 Scope {TargetName}。仍引用它的客户端需要同步调整，否则授权请求会失败。";
        return RedirectBack();
    }

    // ------------------------------------------------------------------ 内部辅助

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var all = await _scopes.GetAllAsync(cancellationToken);
        TotalCount = all.Count;

        IReadOnlyList<ScopeDto> filtered = all.ToArray();

        if (HasFilter)
        {
            var keyword = Search!.Trim();
            filtered = filtered
                .Where(scope =>
                    scope.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    (scope.DisplayName?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (scope.Description?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToArray();
        }

        Scopes = filtered;

        // 引用计数：客户端数量级很小，一次拉完在内存里统计；
        // 不做 N 次查询（每个 scope 查一遍某个客户端的 scp: 权限既慢又难读）
        var clients = await _clients.QueryAsync(1, ReferenceScanSize, null, cancellationToken);
        ReferenceCountsTruncated = clients.Total > ReferenceScanSize;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var client in clients.Items)
        {
            // 同一个客户端里重复的 scope 只算一次
            foreach (var scope in client.AllowedScopes.Distinct(StringComparer.Ordinal))
            {
                counts[scope] = counts.TryGetValue(scope, out var current) ? current + 1 : 1;
            }
        }

        ReferenceCounts = counts;
    }

    /// <summary>把多行文本切成去重后的数组（资源标识按行输入）。</summary>
    private static string[] SplitLines(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

    private IActionResult RedirectBack()
        => Redirect(!string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
            ? ReturnUrl
            : "/admin/scopes");

    private async Task<IActionResult> FailAsync(string? dialog, Error error, CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);

        FormError = error.Message;
        OpenDialog = dialog;

        return Page();
    }
}
