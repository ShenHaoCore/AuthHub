using System.Globalization;
using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Resources;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthHub.Api.Pages.Admin;

/// <summary>
/// 资源标识白名单（RFC 8707）的管理页。
///
/// 与 <see cref="ScopesModel"/> 同构，但有三个只有资源才成立的差别，页面上也各有提示：
///
/// 1) **变更即时生效**。校验每请求现查 <c>RegisteredResources</c>，登记 / 启停 / 删除都
///    不需要重启 —— 这正是 #23 把白名单从配置搬进库表的理由。
/// 2) **停用 ≠ 删除**。<c>Enabled=false</c> 的登记等同未登记（请求拿到 <c>invalid_target</c>），
///    但记录还在，重新勾选即可恢复；删除则是彻底移除。
/// 3) **删除有两步**。仍有客户端持有该资源的 <c>rsrc:</c> 权限时服务端返回 409，
///    必须先到客户端页收回授权。页面把这条错误显示在列表顶部，而不是假装删除成功。
/// </summary>
[Authorize(Policy = AuthHubConstants.Policies.Ui.ResourcesManage)]
public class ResourcesModel : AdminPageModel
{
    /// <summary>
    /// 统计引用时一次拉取的客户端上限（<see cref="IClientAdminService"/> 内部上限为 200）。
    /// 超过这个数则计数为下界，界面上以 "≥" 标注 —— 不假装精确。
    /// </summary>
    private const int ReferenceScanSize = 200;

    private readonly IResourceAdminService _resources;
    private readonly IClientAdminService _clients;

    public ResourcesModel(IResourceAdminService resources, IClientAdminService clients)
    {
        _resources = resources;
        _clients = clients;
    }

    // ------------------------------------------------------------------ 查询条件（GET）

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Search { get; set; }

    // ------------------------------------------------------------------ 表单字段（POST）

    [BindProperty] public string? NewResource { get; set; }

    [BindProperty] public string? NewDisplayName { get; set; }

    [BindProperty] public string? NewDescription { get; set; }

    [BindProperty] public long? EditId { get; set; }

    [BindProperty] public string? EditDisplayName { get; set; }

    [BindProperty] public string? EditDescription { get; set; }

    [BindProperty] public bool EditEnabled { get; set; }

    [BindProperty] public long? TargetId { get; set; }

    // ------------------------------------------------------------------ 视图状态

    public IReadOnlyList<ResourceDto> Resources { get; private set; } = Array.Empty<ResourceDto>();

    /// <summary>资源标识 → 持有它 <c>rsrc:</c> 权限的客户端数量。</summary>
    public IReadOnlyDictionary<string, int> ReferenceCounts { get; private set; }
        = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>客户端总数超过扫描上限时，引用计数只是下界。</summary>
    public bool ReferenceCountsTruncated { get; private set; }

    /// <summary>未筛选前的登记总数（用于区分"一条都没有"与"没有匹配"）。</summary>
    public int TotalCount { get; private set; }

    public bool HasFilter => !string.IsNullOrWhiteSpace(Search);

    // ------------------------------------------------------------------ 基类钩子（见 AdminPageModel）

    protected override string ListPath => "/admin/resources";

    protected override string? EditingId => EditId?.ToString(CultureInfo.InvariantCulture);

    /// <summary>某个资源被多少个客户端持有（用于删除前提示）。</summary>
    public int ReferenceCount(string resource)
        => ReferenceCounts.TryGetValue(resource, out var count) ? count : 0;

    /// <summary>引用计数的展示文本（超出扫描范围时标 ≥）。</summary>
    public string ReferenceText(string resource)
    {
        var count = ReferenceCount(resource);
        if (count == 0)
        {
            return "未被引用";
        }

        return ReferenceCountsTruncated ? $"≥{count} 个客户端" : $"{count} 个客户端";
    }

    public static string DialogId(long id) => $"dlg-edit-{id.ToString(CultureInfo.InvariantCulture)}";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "资源";
        ViewData["NavKey"] = "resources";

        await LoadDataAsync(cancellationToken);
    }

    // ------------------------------------------------------------------ POST：新建

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(NewResource))
        {
            return await FailAsync(CreateDialog, Error.Validation("资源标识不能为空。"), cancellationToken);
        }

        var result = await _resources.CreateAsync(
            new CreateResourceRequest(
                Resource: NewResource.Trim(),
                DisplayName: TrimToNull(NewDisplayName),
                Description: TrimToNull(NewDescription)),
            cancellationToken);

        if (result.IsFailure)
        {
            return await FailAsync(CreateDialog, result.Error, cancellationToken);
        }

        return RedirectBackWithSuccess(
            $"已登记资源 {result.Value.Resource}，立即生效 —— 不需要重启。还记得给需要它的客户端加上 rsrc: 权限。");
    }

    // ------------------------------------------------------------------ POST：保存编辑

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        if (EditId is null)
        {
            return await FailAsync(EditDialog, Error.Validation("缺少资源登记 Id。"), cancellationToken);
        }

        // DisplayName / Description 直接传表单原文（而不是 TrimToNull）：
        // 服务端把 null 解释为"不修改"、空串解释为"清空"，两者不能混为一谈。
        // 表单里两个输入框始终存在，清空即真的清空。
        var result = await _resources.UpdateAsync(
            EditId.Value,
            new UpdateResourceRequest(
                DisplayName: EditDisplayName,
                Description: EditDescription,
                Enabled: EditEnabled),
            cancellationToken);

        if (result.IsFailure)
        {
            return await FailAsync(EditDialog, result.Error, cancellationToken);
        }

        return RedirectBackWithSuccess(
            $"已保存资源 {result.Value.Resource}，变更立即生效（已签发的令牌不受影响，下一次校验起按新状态执行）。");
    }

    // ------------------------------------------------------------------ POST：删除

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken cancellationToken)
    {
        if (TargetId is null)
        {
            return await FailAsync(null, Error.Validation("缺少资源登记 Id。"), cancellationToken);
        }

        var result = await _resources.DeleteAsync(TargetId.Value, cancellationToken);
        if (result.IsFailure)
        {
            // 409：仍有客户端持有 rsrc: 权限。这条消息里带着客户端 id，必须给管理员看见
            return await FailAsync(null, result.Error, cancellationToken);
        }

        return RedirectBackWithSuccess("已删除资源登记，立即生效。");
    }

    // ------------------------------------------------------------------ 内部辅助

    protected override async Task LoadDataAsync(CancellationToken cancellationToken)
    {
        var all = await _resources.GetAllAsync(cancellationToken);
        TotalCount = all.Count;

        IReadOnlyList<ResourceDto> filtered = all.ToArray();

        if (HasFilter)
        {
            var keyword = Search!.Trim();
            filtered = filtered
                .Where(resource =>
                    resource.Resource.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    (resource.DisplayName?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (resource.Description?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToArray();
        }

        Resources = filtered;

        // 引用计数：客户端数量级很小，一次拉完在内存里统计（与 Scope 页同一策略）
        var clients = await _clients.QueryAsync(1, ReferenceScanSize, null, cancellationToken);
        ReferenceCountsTruncated = clients.Total > ReferenceScanSize;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var client in clients.Items)
        {
            // 同一客户端里重复的资源只算一次；比较用序数，与资源校验一致
            foreach (var resource in client.AllowedResources.Distinct(StringComparer.Ordinal))
            {
                counts[resource] = counts.TryGetValue(resource, out var current) ? current + 1 : 1;
            }
        }

        ReferenceCounts = counts;
    }
}
