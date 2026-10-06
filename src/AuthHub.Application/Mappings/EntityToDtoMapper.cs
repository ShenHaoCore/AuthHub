using AuthHub.Application.DTOs.AuditLogs;
using AuthHub.Application.DTOs.Roles;
using AuthHub.Application.DTOs.Users;
using AuthHub.Domain.Entities;

namespace AuthHub.Application.Mappings;

/// <summary>
/// 实体 → DTO 的映射。手写扩展方法，不再依赖 AutoMapper。
///
/// 为什么移除：AutoMapper 13.0.1 存在已知高危漏洞 CVE-2026-32933（深层嵌套对象图
/// 触发不可捕获的 StackOverflowException，进程直接崩溃），且 13.x 线没有补丁——
/// 修复版本 15.1.1+ 已改为 RPL-1.5 / 商业双许可。本项目的映射只有三张表、
/// 全是扁平字段，手写的成本低于"为一个映射库引入 copyleft 许可与 CVE"。
///
/// 与原 AutoMapper 配置的约定一致（调用方依赖此约定，别改）：
///  - <see cref="ToDto(ApplicationUser)"/> 不填 <c>Roles</c> / <c>LockedOut</c>，
///    它们分别来自角色表与锁定期判定，由调用方在映射后补齐；
///  - <see cref="ToDto(ApplicationRole)"/> 不填 <c>Permissions</c> / <c>UserCount</c>，
///    分别来自 RolePermissionMap 与关联计数，同样由调用方补齐。
/// </summary>
public static class EntityToDtoMapper
{
    /// <summary>用户实体 → DTO。Roles / LockedOut 留空，由调用方补齐。</summary>
    public static UserDto ToDto(this ApplicationUser user) => new()
    {
        Id = user.Id,
        UserName = user.UserName ?? string.Empty,
        Email = user.Email,
        DisplayName = user.DisplayName == string.Empty ? user.UserName ?? string.Empty : user.DisplayName,
        PhoneNumber = user.PhoneNumber,
        EmailConfirmed = user.EmailConfirmed,
        TwoFactorEnabled = user.TwoFactorEnabled,
        IsActive = user.IsActive,
        CreatedAt = user.CreatedAt,
        LastLoginAt = user.LastLoginAt,
    };

    /// <summary>角色实体 → DTO。Permissions / UserCount 留空，由调用方补齐。</summary>
    public static RoleDto ToDto(this ApplicationRole role) => new()
    {
        Id = role.Id,
        Name = role.Name ?? string.Empty,
        Description = role.Description,
        IsSystemRole = role.IsSystemRole,
        CreatedAt = role.CreatedAt,
    };

    /// <summary>审计日志实体 → DTO。字段一一对应，无特殊规则。</summary>
    public static AuditLogDto ToDto(this AuditLog log) => new()
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
        CreatedAt = log.CreatedAt,
    };
}
