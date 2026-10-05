using System.Globalization;
using AuthHub.Application.DTOs.AuditLogs;
using AuthHub.Application.DTOs.Users;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using AuthHub.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuthHub.Api.Pages.Admin;

/// <summary>单日审计事件数量（仪表盘柱状图的一个柱）。</summary>
public sealed record DailyAuditPoint(string Label, long Count, int Percent)
{
    /// <summary>该柱是否为“无数据”状态（用于换成灰色）。</summary>
    public bool IsEmpty => Count == 0;
}

/// <summary>
/// 管理后台首页（仪表盘）。
///
/// 数据全部来自既有的 Application 服务，不直接碰 DbContext ——
/// 保证后台看到的数字与 API 返回的口径完全一致。
/// </summary>
[Authorize(Policy = AuthHubConstants.Policies.Ui.Admin)]
public class IndexModel : PageModel
{
    /// <summary>柱状图统计的天数（含当天）。</summary>
    private const int ChartDays = 7;

    /// <summary>首页展示的最近审计事件条数。</summary>
    private const int RecentAuditCount = 8;

    private readonly IUserAdminService _users;
    private readonly IRoleAdminService _roles;
    private readonly IClientAdminService _clients;
    private readonly IScopeAdminService _scopes;
    private readonly IAuditLogService _audit;

    public IndexModel(
        IUserAdminService users,
        IRoleAdminService roles,
        IClientAdminService clients,
        IScopeAdminService scopes,
        IAuditLogService audit)
    {
        _users = users;
        _roles = roles;
        _clients = clients;
        _scopes = scopes;
        _audit = audit;
    }

    public long UserCount { get; private set; }

    public long InactiveUserCount { get; private set; }

    public int RoleCount { get; private set; }

    public long ClientCount { get; private set; }

    public int ScopeCount { get; private set; }

    public long AuditEventsLast24Hours { get; private set; }

    public long FailedLoginsLast7Days { get; private set; }

    public IReadOnlyList<DailyAuditPoint> DailyAudit { get; private set; } = Array.Empty<DailyAuditPoint>();

    public IReadOnlyList<AuditLogDto> RecentAudit { get; private set; } = Array.Empty<AuditLogDto>();

    /// <summary>权限点总数（用于说明内置角色的权限覆盖情况）。</summary>
    public int PermissionCount => AuthHubConstants.Permissions.All.Count;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "仪表盘";
        ViewData["NavKey"] = "dashboard";

        var now = DateTimeOffset.UtcNow;

        // 用 PageSize=1 只取 Total：这些数字只需要计数，不需要真的把数据拉回来
        var allUsers = await _users.QueryAsync(new UserQuery { Page = 1, PageSize = 1 }, cancellationToken);
        var inactiveUsers = await _users.QueryAsync(
            new UserQuery { Page = 1, PageSize = 1, IsActive = false },
            cancellationToken);

        UserCount = allUsers.Total;
        InactiveUserCount = inactiveUsers.Total;

        RoleCount = (await _roles.GetAllAsync(cancellationToken)).Count;
        ScopeCount = (await _scopes.GetAllAsync(cancellationToken)).Count;

        var clients = await _clients.QueryAsync(1, 1, cancellationToken: cancellationToken);
        ClientCount = clients.Total;

        var last24Hours = await _audit.QueryAsync(
            new AuditLogQuery { Page = 1, PageSize = 1, From = now.AddHours(-24), To = now },
            cancellationToken);
        AuditEventsLast24Hours = last24Hours.Total;

        var from7Days = now.AddDays(-ChartDays + 1).UtcDateTime.Date;
        var failedLogins = await _audit.QueryAsync(
            new AuditLogQuery
            {
                Page = 1,
                PageSize = 1,
                Action = AuditActionType.UserLoginFailed,
                From = new DateTimeOffset(from7Days, TimeSpan.Zero),
                To = now
            },
            cancellationToken);
        FailedLoginsLast7Days = failedLogins.Total;

        DailyAudit = await BuildDailyAsync(now, cancellationToken);

        var recent = await _audit.QueryAsync(new AuditLogQuery { Page = 1, PageSize = RecentAuditCount }, cancellationToken);
        RecentAudit = recent.Items;
    }

    /// <summary>
    /// 按天统计审计事件数量。
    ///
    /// 注意边界：仓储实现用的是闭区间（<c>CreatedAt &gt;= from &amp;&amp; CreatedAt &lt;= to</c>），
    /// 因此一天的结束时间要取「次日零点减 1 tick」，否则跨天的那一毫秒会被两个桶各算一次。
    /// </summary>
    private async Task<IReadOnlyList<DailyAuditPoint>> BuildDailyAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var today = now.UtcDateTime.Date;
        var counters = new List<(DateTime Date, long Count)>(ChartDays);

        for (var offset = ChartDays - 1; offset >= 0; offset--)
        {
            var date = today.AddDays(-offset);
            var from = new DateTimeOffset(date, TimeSpan.Zero);
            var to = from.AddDays(1).AddTicks(-1);

            var page = await _audit.QueryAsync(
                new AuditLogQuery { Page = 1, PageSize = 1, From = from, To = to },
                cancellationToken);

            counters.Add((date, page.Total));
        }

        var max = counters.Count == 0 ? 0 : counters.Max(x => x.Count);

        return counters.Select(x => new DailyAuditPoint(
            x.Date.ToString("MM-dd", CultureInfo.InvariantCulture),
            x.Count,
            Percent: max <= 0 ? 0 : (int)Math.Round(x.Count * 100d / max, MidpointRounding.AwayFromZero)))
            .ToArray();
    }
}
