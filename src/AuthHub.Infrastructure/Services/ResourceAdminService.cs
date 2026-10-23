using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Resources;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Entities;
using AuthHub.Domain.Enums;
using AuthHub.Domain.Interfaces;
using AuthHub.Infrastructure.Configuration;
using AuthHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using Oidc = OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthHub.Infrastructure.Services;

/// <summary>
/// 资源白名单的管理实现(直连 <see cref="AuthHubDbContext"/>,不经 OpenIddict 管理器 ——
/// RegisteredResources 是 AuthHub 自有表)。
///
/// <para>所有写入路径都必须经 <see cref="AuthHubResourceRegistry.NormalizeResource"/> 规范化:
/// OpenIddict 资源校验是"请求原文 vs 登记值"的序数比对,登记形态不一致的后果是
/// 管理员登记了 A、客户端却拿着 A' 拿到 invalid_target。</para>
/// </summary>
public sealed class ResourceAdminService(
    AuthHubDbContext dbContext,
    IOpenIddictApplicationManager applications,
    IAuditLogService audit,
    IClock clock) : IResourceAdminService
{
    public async Task<IReadOnlyCollection<ResourceDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var resources = await dbContext.RegisteredResources
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return resources
            .OrderBy(resource => resource.Resource, StringComparer.Ordinal)
            .Select(ToDto)
            .ToArray();
    }

    public async Task<Result<ResourceDto>> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        var resource = await FindAsync(id, cancellationToken);
        if (resource is null)
        {
            return Result.Failure<ResourceDto>(Error.NotFound($"资源登记 {id} 不存在。"));
        }

        return Result.Success(ToDto(resource));
    }

    public async Task<Result<ResourceDto>> CreateAsync(CreateResourceRequest request, CancellationToken cancellationToken = default)
    {
        // 与 OpenIddict 请求侧校验同规则:绝对 URI、无 fragment;再统一成 AbsoluteUri 形态
        var normalized = AuthHubResourceRegistry.NormalizeResource(request.Resource);
        if (normalized is null)
        {
            return Result.Failure<ResourceDto>(Error.Validation(
                $"\"{request.Resource}\" 不是可登记的资源标识:资源必须是绝对 URI 且不含 fragment " +
                "(例如 https://mcp.example.com/mcp)。"));
        }

        if (await dbContext.RegisteredResources.AnyAsync(resource => resource.Resource == normalized, cancellationToken))
        {
            return Result.Failure<ResourceDto>(Error.Conflict($"资源 {normalized} 已登记。"));
        }

        var entity = new RegisteredResource
        {
            Resource = normalized,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Enabled = true,
            CreatedAt = clock.UtcNow
        };

        dbContext.RegisteredResources.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        await audit.LogAsync(
            new AuditEntry(AuditActionType.ResourceCreated, true, Details: $"Resource={normalized}"),
            cancellationToken);

        return Result.Success(ToDto(entity));
    }

    public async Task<Result<ResourceDto>> UpdateAsync(long id, UpdateResourceRequest request, CancellationToken cancellationToken = default)
    {
        var resource = await FindAsync(id, cancellationToken);
        if (resource is null)
        {
            return Result.Failure<ResourceDto>(Error.NotFound($"资源登记 {id} 不存在。"));
        }

        resource.DisplayName = request.DisplayName is null
            ? resource.DisplayName
            : string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim();
        resource.Description = request.Description is null
            ? resource.Description
            : string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        resource.Enabled = request.Enabled ?? resource.Enabled;
        resource.UpdatedAt = clock.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);

        await audit.LogAsync(
            new AuditEntry(
                AuditActionType.ResourceUpdated,
                true,
                Details: $"Resource={resource.Resource}；Enabled={resource.Enabled}"),
            cancellationToken);

        return Result.Success(ToDto(resource));
    }

    public async Task<Result> DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        var resource = await FindAsync(id, cancellationToken);
        if (resource is null)
        {
            return Result.Failure(Error.NotFound($"资源登记 {id} 不存在。"));
        }

        // 删除策略(默认拒绝的显式化):仍有客户端持 rsrc: 权限时禁止删除。
        // 资源删掉后,这些客户端的请求会立刻全部 invalid_target —— 这必须是显式的两步操作,
        // 先收客户端权限,再删资源,而不是一个 DELETE 顺带打断一批下游。
        var holders = new List<string>();

        await foreach (var application in applications.ListAsync(null, null, cancellationToken))
        {
            var permissions = await applications.GetPermissionsAsync(application, cancellationToken);

            if (permissions.Contains(Oidc.Permissions.Prefixes.Resource + resource.Resource, StringComparer.Ordinal))
            {
                holders.Add(await applications.GetClientIdAsync(application, cancellationToken) ?? "?");
            }
        }

        if (holders.Count > 0)
        {
            return Result.Failure(Error.Conflict(
                $"资源 {resource.Resource} 仍被 {holders.Count} 个客户端持有 rsrc: 权限" +
                $"（{string.Join("、", holders)}）。请先在客户端侧收回该资源的授权，再删除登记。"));
        }

        dbContext.RegisteredResources.Remove(resource);
        await dbContext.SaveChangesAsync(cancellationToken);

        await audit.LogAsync(
            new AuditEntry(AuditActionType.ResourceDeleted, true, Details: $"Resource={resource.Resource}"),
            cancellationToken);

        return Result.Success();
    }

    private Task<RegisteredResource?> FindAsync(long id, CancellationToken cancellationToken) =>
        dbContext.RegisteredResources.FirstOrDefaultAsync(resource => resource.Id == id, cancellationToken);

    private static ResourceDto ToDto(RegisteredResource resource) => new()
    {
        Id = resource.Id,
        Resource = resource.Resource,
        DisplayName = resource.DisplayName,
        Description = resource.Description,
        Enabled = resource.Enabled,
        CreatedAt = resource.CreatedAt,
        UpdatedAt = resource.UpdatedAt
    };
}
