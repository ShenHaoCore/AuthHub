using AuthHub.Application.Common;

namespace AuthHub.Application.DTOs.AuditLogs;

/// <summary>审计日志视图模型。</summary>
public sealed record AuditLogDto
{
    public string Id { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public bool Succeeded { get; init; }

    public string? UserId { get; init; }

    public string? UserName { get; init; }

    public string? ClientId { get; init; }

    public string? IpAddress { get; init; }

    public string? UserAgent { get; init; }

    public string? Details { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>审计日志查询条件。</summary>
public sealed class AuditLogQuery : PagedQuery
{
    public string? Action { get; set; }

    public string? UserId { get; set; }

    public string? ClientId { get; set; }

    public DateTimeOffset? From { get; set; }

    public DateTimeOffset? To { get; set; }
}
