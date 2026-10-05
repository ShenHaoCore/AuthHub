namespace AuthHub.Api.Pages.Shared;

/// <summary>
/// 查询串构造的唯一实现。
///
/// 抽出来是因为后台有两个地方在重建"带筛选条件的 URL"，各写了一遍转义与空值判断：
///   - <see cref="PaginationModel.UrlFor"/>（翻页链接要保留当前筛选条件，否则翻到第二页筛选就丢了）；
///   - <c>AuditLogsModel.BuildUrl</c>（快捷时间范围链接要保留其余筛选条件）。
/// 两处的规则必须一致：值为空白就整条丢掉（不能留下 <c>from=</c> 这种空参数，
/// 那会被后端解析成"条件存在但为空"，与不传不等价）。
/// </summary>
public static class QueryStringBuilder
{
    /// <summary>拼成不含 <c>?</c> 的查询串；所有值都为空时返回空串。</summary>
    public static string Build(IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        var parts = new List<string>();

        foreach (var (key, value) in parameters)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            parts.Add(string.Concat(
                Uri.EscapeDataString(key),
                "=",
                Uri.EscapeDataString(value)));
        }

        return string.Join('&', parts);
    }

    /// <summary>拼成完整地址；无参数时原样返回 <paramref name="path"/>（不带尾随 <c>?</c>）。</summary>
    public static string Append(string path, IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        var query = Build(parameters);

        return query.Length == 0 ? path : string.Concat(path, "?", query);
    }
}
