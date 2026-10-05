namespace AuthHub.Application.Common;

/// <summary>分页结果。</summary>
public sealed class PagedResult<T>
{
    public PagedResult(IReadOnlyList<T> items, long total, int page, int pageSize)
    {
        Items = items;
        Total = total;
        Page = page;
        PageSize = pageSize;
    }

    public IReadOnlyList<T> Items { get; }

    public long Total { get; }

    public int Page { get; }

    public int PageSize { get; }

    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);

    public bool HasNext => Page < TotalPages;

    public bool HasPrevious => Page > 1;

    public static PagedResult<T> Empty(int page, int pageSize) => new(Array.Empty<T>(), 0, page, pageSize);
}

/// <summary>分页查询基类。</summary>
public abstract class PagedQuery
{
    private const int MaxPageSize = 100;
    private int _page = 1;
    private int _pageSize = 20;

    /// <summary>页码，从 1 开始（小于 1 时归一到 1，避免 Skip 变成负数）。</summary>
    public int Page
    {
        get => _page;
        set => _page = value < 1 ? 1 : value;
    }

    /// <summary>每页条数（越界时归一到 [1,100]，防止一次拉全表）。</summary>
    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = value switch
        {
            <= 0 => 20,
            > MaxPageSize => MaxPageSize,
            _ => value
        };
    }

    public int Skip => (Page - 1) * PageSize;
}
