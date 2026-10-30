using AuthHub.Application.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuthHub.Api.Pages.Admin;

/// <summary>
/// 后台管理页面的公共基类：把用户 / 角色 / 客户端 / Scope 四个 CRUD 页面逐字重复的
/// 「PRG + 弹窗重开 + 回跳」样板收在一处。
/// </summary>
/// <remarks>
/// - 交互契约（与 <c>wwwroot/js/authhub.js</c> 配合）：POST 成功 → <see cref="RedirectBackWithSuccess"/> 写一条一次性提示后 302 回列表；POST 失败 → <see cref="FailAsync"/> 原地重渲染，把哪个弹窗出错记进 <see cref="OpenDialog"/>，视图据此把 <c>data-dialog-autoopen</c> 渲染成显式小写 "true"，用户填过的值不丢。
/// - 局部刷新（<see cref="IsPartial"/>，authhub.js 的 data-ajax 请求带 <c>X-AH-Partial: 1</c>）：视图把 Layout 置空后 <see cref="Page()"/> 产出的就是 #ah-content 片段本身，失败路径无需分支；成功用 <see cref="RedirectBackWithSuccessAsync"/> 重载列表 + <c>X-AH-Toast</c> 头原地返回。不带该头的请求（无 JS、集成测试、CSRF 重放）走原有整页流程。
/// - 视图里不要把 <see cref="AutoOpenCreate"/> 直接插值进属性 —— <c>data-dialog-autoopen="@Model.AutoOpenCreate"</c> 渲染成 "False" 但属性照样存在，前端按「属性是否存在」判断会所有弹窗一起弹；必须写 <c>@(Model.AutoOpenCreate ? "true" : "false")</c>。
/// - 失败重渲染前必须重新载入列表数据（<see cref="LoadDataAsync"/>），否则是「弹窗开着、背后列表全空」的页面。
/// - 两条约束都有契约测试守着（<c>AdminUiTests</c>）。
/// </remarks>
public abstract class AdminPageModel : PageModel
{
    protected const string CreateDialog = "create";

    protected const string EditDialog = "edit";

    /// <summary>成功提示的 TempData 键，由 <c>Pages/Shared/_AdminLayout.cshtml</c> 读取并展示。</summary>
    private const string SuccessTempDataKey = "Success";

    /// <summary>本次请求是否来自前端局部刷新层（authhub.js 的 data-ajax 表单 / data-ajax-link 链接）；视图据此把 Layout 置空，<see cref="Page()"/> 即产出 #ah-content 内容片段。</summary>
    public bool IsPartial =>
        Request.Headers.TryGetValue("X-AH-Partial", out var values)
        && values.Count > 0;

    /// <summary>列表地址（含筛选条件），随表单往返，POST 后跳回原位置。</summary>
    [BindProperty]
    public string? ReturnUrl { get; set; }

    /// <summary>POST 失败时展示的错误文本。</summary>
    public string? FormError { get; private set; }

    /// <summary>POST 失败时需要重新打开的弹窗：create / edit；成功或首次进入时为 null。</summary>
    public string? OpenDialog { get; private set; }

    /// <summary>新建弹窗是否需要在页面加载后自动打开（上一次提交失败）。</summary>
    public bool AutoOpenCreate => string.Equals(OpenDialog, CreateDialog, StringComparison.Ordinal);

    /// <summary>当前列表地址（含筛选条件）。</summary>
    public string CurrentUrl => $"{Request.Path}{Request.QueryString}";

    /// <summary>本页列表地址 —— <see cref="ReturnUrl"/> 缺失或非法时的兜底（本站绝对路径）。</summary>
    protected abstract string ListPath { get; }

    /// <summary>编辑表单正在编辑的标识，用来判断该行的弹窗要不要重开。</summary>
    protected abstract string? EditingId { get; }

    /// <summary>比较编辑标识的策略。角色名等按名称查找的场景不区分大小写。</summary>
    protected virtual StringComparison EditingIdComparison => StringComparison.Ordinal;

    /// <summary>载入本页列表数据。GET 与 POST 失败时都要调用 —— 失败重渲染不能是空白页。</summary>
    protected abstract Task LoadDataAsync(CancellationToken cancellationToken);

    public bool IsEditFailure(string id) => IsDialogFailure(EditDialog, id);

    /// <summary>指定弹窗是否因上一次提交失败而需要自动打开。给同一行上有多个表单弹窗的页面用（共用 <see cref="EditingId"/> 定位到同一行，靠 <paramref name="dialog"/> 区分）。</summary>
    public bool IsDialogFailure(string dialog, string? id)
        => string.Equals(OpenDialog, dialog, StringComparison.Ordinal)
           && !string.IsNullOrEmpty(id)
           && string.Equals(EditingId, id, EditingIdComparison);

    /// <summary>载入数据后原地返回本页，并重新打开出错的弹窗。</summary>
    protected async Task<IActionResult> FailAsync(string? dialog, Error error, CancellationToken cancellationToken)
    {
        await LoadDataAsync(cancellationToken);

        FormError = error.Message;
        OpenDialog = dialog;

        return Page();
    }

    /// <summary>退回列表页。只接受站内地址，防开放重定向。</summary>
    protected IActionResult RedirectBack()
        => Redirect(!string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : ListPath);

    /// <summary><see cref="RedirectBack"/> 的局部刷新版：局部请求时不做 302，重载列表数据后原地返回内容片段（片段里没有 302 可跟）。</summary>
    protected async Task<IActionResult> RedirectBackAsync(CancellationToken cancellationToken)
    {
        if (!IsPartial)
        {
            return RedirectBack();
        }

        await LoadDataAsync(cancellationToken);
        return Page();
    }

    /// <summary>写一条一次性成功提示（布局页读取后即删除）。</summary>
    protected void SetSuccessMessage(string message) => TempData[SuccessTempDataKey] = message;

    /// <summary>写一条一次性成功提示，然后退回列表页。</summary>
    protected IActionResult RedirectBackWithSuccess(string message)
    {
        SetSuccessMessage(message);
        return RedirectBack();
    }

    /// <summary><see cref="RedirectBackWithSuccess"/> 的局部刷新版：提示不走 TempData + 整页渲染，改写进 <c>X-AH-Toast</c> 响应头（<see cref="SetPartialToast"/>），由 authhub.js 渲染成轻提示。</summary>
    protected async Task<IActionResult> RedirectBackWithSuccessAsync(string message, CancellationToken cancellationToken)
    {
        if (!IsPartial)
        {
            return RedirectBackWithSuccess(message);
        }

        SetPartialToast("success", message);
        await LoadDataAsync(cancellationToken);
        return Page();
    }

    /// <summary>局部刷新动作的反馈提示：URI 编码后的 {"t":类型,"m":文案} JSON（HTTP 头只允许 ASCII，中文文案整体转义，前端 decodeURIComponent 还原）。</summary>
    protected void SetPartialToast(string type, string message)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new { t = type, m = message });
        Response.Headers["X-AH-Toast"] = Uri.EscapeDataString(payload);
    }

    /// <summary>
    /// 空白串归一为 null，其余去掉首尾空白。
    /// 注意不等于业务上的「清空」：本项目更新请求统一是「null 表示不修改」语义，
    /// 需要表达"清空"的字段（如用户手机号）要另按空串传递，见 <c>UsersModel.OnPostSaveAsync</c>。
    /// </summary>
    protected static string? TrimToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>把多行文本切成去重后的数组（重定向地址、Scope、资源标识等都按行输入）。</summary>
    protected static string[] SplitLines(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Distinct(StringComparer.Ordinal)
                   .ToArray();
}
