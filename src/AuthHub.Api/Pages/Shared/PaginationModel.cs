using System.Globalization;

namespace AuthHub.Api.Pages.Shared;

/// <summary>
/// 分页组件（<c>_Pagination.cshtml</c>）的视图模型。
///
/// 关键点：翻页链接必须**保留当前筛选条件**，否则用户翻到第二页筛选就被清空了。
/// 因此这里由页面把原始查询参数一并传入，<see cref="UrlFor"/> 负责重建查询串。
/// </summary>
public sealed class PaginationModel
{
    /// <summary>当前页码（从 1 开始）。</summary>
    public int Page { get; init; } = 1;

    /// <summary>每页条数。</summary>
    public int PageSize { get; init; } = 20;

    /// <summary>总条数。</summary>
    public long Total { get; init; }

    /// <summary>当前页面路径（不含查询串）。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>需要保留的查询参数（值为空则忽略）。</summary>
    public IReadOnlyDictionary<string, string?> Query { get; init; }
        = new Dictionary<string, string?>(StringComparer.Ordinal);

    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);

    public bool HasPrevious => Page > 1;

    public bool HasNext => Page < TotalPages;

    /// <summary>本页第一条在整体中的序号（无数据时为 0）。</summary>
    public int FirstItemNumber => Total == 0 ? 0 : ((Page - 1) * PageSize) + 1;

    /// <summary>本页最后一条在整体中的序号。</summary>
    public int LastItemNumber => (int)Math.Min(Total, (long)Page * PageSize);

    /// <summary>“上一页”链接的样式类。</summary>
    public string PreviousCss => HasPrevious ? "ah-page-link" : "ah-page-link is-disabled";

    /// <summary>“下一页”链接的样式类。</summary>
    public string NextCss => HasNext ? "ah-page-link" : "ah-page-link is-disabled";

    /// <summary>页码按钮（null 表示省略号）。</summary>
    public IReadOnlyList<int?> PageNumbers
    {
        get
        {
            const int windowSize = 7;

            if (TotalPages <= windowSize)
            {
                return Enumerable.Range(1, TotalPages).Cast<int?>().ToArray();
            }

            var end = Math.Min(TotalPages, Math.Max(1, Page - 3) + windowSize - 1);
            var start = Math.Max(1, end - windowSize + 1);

            var numbers = new List<int?>();
            if (start > 1)
            {
                numbers.Add(1);
                if (start > 2)
                {
                    numbers.Add(null);
                }
            }

            for (var page = start; page <= end; page++)
            {
                numbers.Add(page);
            }

            if (end < TotalPages)
            {
                if (end < TotalPages - 1)
                {
                    numbers.Add(null);
                }
                numbers.Add(TotalPages);
            }

            return numbers;
        }
    }

    /// <summary>生成带完整筛选条件的翻页链接。</summary>
    public string? UrlFor(int page)
    {
        if (page < 1 || page > TotalPages)
        {
            return null;
        }

        var parts = new List<string>();

        foreach (var (key, value) in Query)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                string.Equals(key, "page", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            parts.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0}={1}",
                Uri.EscapeDataString(key),
                Uri.EscapeDataString(value)));
        }

        parts.Add(string.Format(CultureInfo.InvariantCulture, "page={0}", page));

        return string.Format(CultureInfo.InvariantCulture, "{0}?{1}", Path, string.Join('&', parts));
    }
}
