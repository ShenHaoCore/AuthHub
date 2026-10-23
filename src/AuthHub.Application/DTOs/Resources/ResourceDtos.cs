namespace AuthHub.Application.DTOs.Resources;

/// <summary>已登记资源(RFC 8707 资源标识白名单)的视图模型。</summary>
public sealed record ResourceDto
{
    public long Id { get; init; }

    /// <summary>资源标识(绝对 URI,规范化的 <c>Uri.AbsoluteUri</c> 形态)。</summary>
    public string Resource { get; init; } = string.Empty;

    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    /// <summary>false = 等同未登记(请求被 invalid_target 拒绝),但保留登记记录。</summary>
    public bool Enabled { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <param name="Resource">资源标识。接受任意写法,入库前规范化为 <c>Uri.AbsoluteUri</c>。</param>
/// <param name="DisplayName">显示名。空/空白视为不设置。</param>
/// <param name="Description">说明。空/空白视为不设置。</param>
public record CreateResourceRequest(
    string Resource,
    string? DisplayName = null,
    string? Description = null);

/// <summary>部分更新:null = 保持不变。资源标识本身不可修改(它是白名单的业务键)。</summary>
public record UpdateResourceRequest(
    string? DisplayName = null,
    string? Description = null,
    bool? Enabled = null);
