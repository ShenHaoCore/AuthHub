using AuthHub.Application.Common;
using AuthHub.Application.DTOs.AuditLogs;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Entities;
using AuthHub.Domain.Enums;
using AuthHub.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace AuthHub.Application.Services;

/// <summary>
/// 审计日志服务。所有认证授权相关的关键动作都应通过这里落库，
/// 同时以结构化日志输出一份（便于接入 Seq / ELK）。
/// </summary>
public sealed class AuditLogService : IAuditLogService
{
    private readonly IAuditLogRepository _repository;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly ILogger<AuditLogService> _logger;

    public AuditLogService(
        IAuditLogRepository repository,
        ICurrentUser currentUser,
        IClock clock,
        ILogger<AuditLogService> logger)
    {
        _repository = repository;
        _currentUser = currentUser;
        _clock = clock;
        _logger = logger;
    }

    public async Task LogAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        var log = new AuditLog
        {
            Action = entry.Action,
            Succeeded = entry.Succeeded,
            UserId = entry.UserId ?? _currentUser.UserId,
            UserName = entry.UserName ?? _currentUser.UserName,
            ClientId = entry.ClientId ?? _currentUser.ClientId,
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            Details = entry.Details,
            CreatedAt = _clock.UtcNow
        };

        await _repository.AddAsync(log, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);

        // 认证失败属于安全事件，提升日志级别便于告警
        if (entry.Succeeded)
        {
            _logger.LogInformation(
                "审计事件 {Action} | 用户={UserId} | 客户端={ClientId} | IP={IpAddress} | 明细={Details}",
                log.Action, log.UserId, log.ClientId, log.IpAddress, log.Details);
        }
        else
        {
            _logger.LogWarning(
                "审计事件（失败） {Action} | 用户={UserId} | 客户端={ClientId} | IP={IpAddress} | 明细={Details}",
                log.Action, log.UserId, log.ClientId, log.IpAddress, log.Details);
        }
    }

    public async Task<PagedResult<AuditLogDto>> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken = default)
    {
        var (items, total) = await _repository.QueryAsync(
            query.Action,
            query.UserId,
            query.ClientId,
            query.From,
            query.To,
            query.Page,
            query.PageSize,
            cancellationToken);

        var dtos = items.Select(ToDto).ToArray();
        return new PagedResult<AuditLogDto>(dtos, total, query.Page, query.PageSize);
    }

    public Task LogLoginSucceededAsync(string userId, string userName, string? clientId = null, CancellationToken cancellationToken = default)
        => LogAsync(new AuditEntry(AuditActionType.UserLoginSucceeded, true, userId, userName, clientId), cancellationToken);

    public Task LogLoginFailedAsync(string? userId, string userName, string? reason = null, CancellationToken cancellationToken = default)
        => LogAsync(new AuditEntry(AuditActionType.UserLoginFailed, false, userId, userName, Details: reason), cancellationToken);

    public Task LogTokensRevokedAsync(string subject, int tokenCount, CancellationToken cancellationToken = default)
        => LogAsync(
            new AuditEntry(
                AuditActionType.TokenRevoked,
                true,
                UserId: subject,
                Details: $"撤销令牌数量：{tokenCount}"),
            cancellationToken);

    private static AuditLogDto ToDto(AuditLog log) => new()
    {
        Id = log.Id,
        Action = log.Action,
        Succeeded = log.Succeeded,
        UserId = log.UserId,
        UserName = log.UserName,
        ClientId = log.ClientId,
        IpAddress = log.IpAddress,
        UserAgent = log.UserAgent,
        Details = log.Details,
        CreatedAt = log.CreatedAt
    };
}
