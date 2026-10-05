using System.Globalization;
using AuthHub.Application.Common;
using AuthHub.Application.DTOs.AuditLogs;
using AuthHub.Application.DTOs.Users;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuthHub.Api.Pages.Admin;

/// <summary>
/// 审计日志查询（只读）。
///
/// 两处与后端能力相关的、必须讲清楚的取舍：
///
/// 1) **时间区间是闭区间**。仓储里的实现是 <c>CreatedAt &gt;= From &amp;&amp; CreatedAt &lt;= To</c>，
///    因此"选择到 10 月 5 日"不能直接把 10 月 5 日 00:00 传给 To（那样会漏掉当天几乎所有记录）。
///    这里把用户选的日期解释为"整天"：To = 次日零点减 1 tick。页面上标注了"含当天"。
///
/// 2) **后端只支持按 UserId 精确匹配**（`AuditLogRepository` 用的是 `x.UserId == userId`），
///    而日志表里同时存了 UserName。直接让管理员填 GUID 太不友好，因此在页面层做一次
///    "用户名 → ID"的解析：看起来像 GUID 就按 ID 用，否则去用户表里搜一次。
///    搜不到时把原值当 ID 传下去 —— 自然查不到任何记录，比悄悄返回全量要安全。
/// </summary>
[Authorize(Policy = AuthHubConstants.Policies.Ui.AuditRead)]
public class AuditLogsModel : PageModel
{
    private const int DefaultPageSize = 25;

    /// <summary>快捷范围链接里输出的日期格式（也是筛选框 placeholder 展示的格式）。</summary>
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// 日期筛选接受的写法。原生浏览器的日期控件把值提交成 yyyy-MM-dd，
    /// 但输入框是普通文本框，管理员键盘手输时更常见 yyyy/M/d 这种省零写法 ——
    /// 这里三种分隔符、月日 1~2 位都收，避免"输了个 2026/10/6 被拒"的无谓摩擦。
    /// </summary>
    private static readonly string[] AcceptedDateFormats =
        { "yyyy-MM-dd", "yyyy-M-d", "yyyy/M/d", "yyyy.M.d" };

    private readonly IAuditLogService _audit;
    private readonly IUserAdminService _users;

    public AuditLogsModel(IAuditLogService audit, IUserAdminService users)
    {
        _audit = audit;
        _users = users;
    }

    // ------------------------------------------------------------------ 查询条件（GET）

    [BindProperty(SupportsGet = true, Name = "page")]
    public int CurrentPage { get; set; } = 1;

    [BindProperty(SupportsGet = true, Name = "action")]
    public string? ActionFilter { get; set; }

    /// <summary>用户 ID 或用户名（页面层会解析成 ID）。</summary>
    [BindProperty(SupportsGet = true, Name = "user")]
    public string? UserFilter { get; set; }

    [BindProperty(SupportsGet = true, Name = "client")]
    public string? ClientFilter { get; set; }

    /// <summary>起始日期（含当天 00:00:00 UTC）。</summary>
    [BindProperty(SupportsGet = true, Name = "from")]
    public string? FromFilter { get; set; }

    /// <summary>结束日期（含当天 23:59:59.9999999 UTC）。</summary>
    [BindProperty(SupportsGet = true, Name = "to")]
    public string? ToFilter { get; set; }

    // ------------------------------------------------------------------ 视图状态

    public PagedResult<AuditLogDto> Logs { get; private set; }
        = PagedResult<AuditLogDto>.Empty(1, DefaultPageSize);

    /// <summary>条件被忽略或解析失败时的提示（不阻断查询，只是别让用户以为条件生效了）。</summary>
    public string? FilterWarning { get; private set; }

    /// <summary>用户名解析结果（用于在界面上确认"我筛的是谁"）。</summary>
    public string? ResolvedUserName { get; private set; }

    /// <summary>用户名没匹配到任何账号。</summary>
    public bool UserNotFound { get; private set; }

    public bool HasFilter
        => !string.IsNullOrWhiteSpace(ActionFilter)
           || !string.IsNullOrWhiteSpace(UserFilter)
           || !string.IsNullOrWhiteSpace(ClientFilter)
           || !string.IsNullOrWhiteSpace(FromFilter)
           || !string.IsNullOrWhiteSpace(ToFilter);

    public static IReadOnlyList<AuditActionGroup> ActionGroups => AuditDisplay.ActionGroups;

    public Pages.Shared.PaginationModel Pagination => new()
    {
        Page = Logs.Page,
        PageSize = Logs.PageSize,
        Total = Logs.Total,
        Path = Request.Path,
        Query = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["action"] = ActionFilter,
            ["user"] = UserFilter,
            ["client"] = ClientFilter,
            ["from"] = FromFilter,
            ["to"] = ToFilter
        }
    };

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "审计日志";
        ViewData["NavKey"] = "audit";

        var query = new AuditLogQuery
        {
            Page = CurrentPage,
            PageSize = DefaultPageSize
        };

        if (!string.IsNullOrWhiteSpace(ActionFilter))
        {
            // 非法动作直接忽略：既避免拿脏值去查库，也让用户看到"筛选没生效"而不是"结果莫名为空"
            if (AuditDisplay.IsKnown(ActionFilter))
            {
                query.Action = ActionFilter;
            }
            else
            {
                FilterWarning = $"未知的审计动作「{ActionFilter}」，该条件已忽略。";
            }
        }

        if (!string.IsNullOrWhiteSpace(ClientFilter))
        {
            query.ClientId = ClientFilter.Trim();
        }

        if (!string.IsNullOrWhiteSpace(UserFilter))
        {
            query.UserId = await ResolveUserIdAsync(UserFilter.Trim(), cancellationToken);
        }

        var from = ParseDate(FromFilter, "起始日期");
        var to = ParseDate(ToFilter, "结束日期");

        query.From = from;

        if (to is not null)
        {
            // 闭区间 + "含当天"：把结束日期扩到当天最后一刻
            query.To = to.Value.AddDays(1).AddTicks(-1);
        }

        if (from is not null && to is not null && from > to)
        {
            FilterWarning = "起始日期晚于结束日期，已自动交换两端。";
            (query.From, query.To) = (
                to.Value,
                from.Value.AddDays(1).AddTicks(-1));
        }

        Logs = await _audit.QueryAsync(query, cancellationToken);
    }

    // ------------------------------------------------------------------ 快捷时间范围

    /// <summary>最近 N 天（含今天）的链接。</summary>
    public string RangeUrl(int days)
    {
        var today = DateTimeOffset.UtcNow.Date;
        return BuildUrl(
            today.AddDays(-(days - 1)).ToString(DateFormat, CultureInfo.InvariantCulture),
            today.ToString(DateFormat, CultureInfo.InvariantCulture));
    }

    /// <summary>清空时间范围的链接（保留其它筛选条件）。</summary>
    public string AllTimeUrl() => BuildUrl(null, null);

    /// <summary>清空全部筛选条件的链接。</summary>
    public static string ResetUrl() => "/admin/audit-logs";

    private string BuildUrl(string? from, string? to)
        // 刻意不带 page：换时间范围后回到第一页，否则容易停在超出范围的空页上。
        // 转义与空值剔除交给 QueryStringBuilder，与翻页链接共用同一套规则。
        => Pages.Shared.QueryStringBuilder.Append("/admin/audit-logs", new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["action"] = ActionFilter,
            ["user"] = UserFilter,
            ["client"] = ClientFilter,
            ["from"] = from,
            ["to"] = to
        });

    // ------------------------------------------------------------------ 内部辅助

    /// <summary>
    /// 把用户填的"用户 ID 或用户名"解析成 ID。
    /// 解析不到时原样返回 —— 该值不会匹配任何 GUID 主键，因此结果是空集而不是全量。
    /// </summary>
    private async Task<string> ResolveUserIdAsync(string value, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(value, out _))
        {
            return value;
        }

        var matches = await _users.QueryAsync(
            new UserQuery { Page = 1, PageSize = 1, Search = value },
            cancellationToken);

        if (matches.Items.Count == 0)
        {
            UserNotFound = true;
            return value;
        }

        ResolvedUserName = matches.Items[0].UserName;
        return matches.Items[0].Id;
    }

    /// <summary>解析日期筛选条件；为空或格式错误时返回 null 并记录提示。</summary>
    private DateTimeOffset? ParseDate(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParseExact(
                value.Trim(),
                AcceptedDateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        FilterWarning = string.IsNullOrEmpty(FilterWarning)
            ? $"{label}「{value}」格式不正确（应为 yyyy-MM-dd，如 2026-10-06），该条件已忽略。"
            : string.Concat(FilterWarning, " ", $"{label}「{value}」格式不正确，该条件已忽略。");

        return null;
    }
}
