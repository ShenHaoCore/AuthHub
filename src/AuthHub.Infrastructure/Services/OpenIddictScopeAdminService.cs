using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Scopes;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Enums;
using OpenIddict.Abstractions;

namespace AuthHub.Infrastructure.Services;

/// <summary>Scope 管理，基于 <see cref="IOpenIddictScopeManager"/>。</summary>
public sealed class OpenIddictScopeAdminService : IScopeAdminService
{
    private readonly IOpenIddictScopeManager _scopeManager;
    private readonly IAuditLogService _audit;

    public OpenIddictScopeAdminService(IOpenIddictScopeManager scopeManager, IAuditLogService audit)
    {
        _scopeManager = scopeManager;
        _audit = audit;
    }

    public async Task<IReadOnlyCollection<ScopeDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<ScopeDto>();

        await foreach (var scope in _scopeManager.ListAsync(null, null, cancellationToken))
        {
            result.Add(await ToDtoAsync(scope, cancellationToken));
        }

        return result.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<Result<ScopeDto>> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        var scope = await _scopeManager.FindByNameAsync(name, cancellationToken);
        if (scope is null)
        {
            return Result.Failure<ScopeDto>(Error.NotFound($"Scope {name} 不存在。"));
        }

        return Result.Success(await ToDtoAsync(scope, cancellationToken));
    }

    public async Task<Result<ScopeDto>> CreateAsync(CreateScopeRequest request, CancellationToken cancellationToken = default)
    {
        if (await _scopeManager.FindByNameAsync(request.Name, cancellationToken) is not null)
        {
            return Result.Failure<ScopeDto>(Error.Conflict($"Scope {request.Name} 已存在。"));
        }

        var descriptor = new OpenIddictScopeDescriptor
        {
            Name = request.Name,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? request.Name : request.DisplayName,
            Description = request.Description
        };

        // Scope 关联的 API 资源会成为令牌的 aud，资源服务器据此判断“这个令牌是否发给我的”
        foreach (var resource in request.Resources ?? Array.Empty<string>())
        {
            descriptor.Resources.Add(resource);
        }

        var scope = await _scopeManager.CreateAsync(descriptor, cancellationToken);

        await _audit.LogAsync(
            new AuditEntry(AuditActionType.ScopeCreated, true, Details: $"Scope={request.Name}；资源={string.Join(",", request.Resources ?? Array.Empty<string>())}"),
            cancellationToken);

        return Result.Success(await ToDtoAsync(scope, cancellationToken));
    }

    public async Task<Result<ScopeDto>> UpdateAsync(string name, UpdateScopeRequest request, CancellationToken cancellationToken = default)
    {
        var scope = await _scopeManager.FindByNameAsync(name, cancellationToken);
        if (scope is null)
        {
            return Result.Failure<ScopeDto>(Error.NotFound($"Scope {name} 不存在。"));
        }

        var resources = request.Resources
                        ?? (await _scopeManager.GetResourcesAsync(scope, cancellationToken)).ToArray();

        var descriptor = new OpenIddictScopeDescriptor
        {
            Name = name,
            DisplayName = request.DisplayName ?? await _scopeManager.GetDisplayNameAsync(scope, cancellationToken),
            Description = request.Description ?? await _scopeManager.GetDescriptionAsync(scope, cancellationToken)
        };

        foreach (var resource in resources)
        {
            descriptor.Resources.Add(resource);
        }

        await _scopeManager.UpdateAsync(scope, descriptor, cancellationToken);

        await _audit.LogAsync(
            new AuditEntry(AuditActionType.ScopeUpdated, true, Details: $"Scope={name}"),
            cancellationToken);

        return Result.Success(await ToDtoAsync(scope, cancellationToken));
    }

    public async Task<Result> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        var scope = await _scopeManager.FindByNameAsync(name, cancellationToken);
        if (scope is null)
        {
            return Result.Failure(Error.NotFound($"Scope {name} 不存在。"));
        }

        await _scopeManager.DeleteAsync(scope, cancellationToken);

        await _audit.LogAsync(
            new AuditEntry(AuditActionType.ScopeDeleted, true, Details: $"Scope={name}"),
            cancellationToken);

        return Result.Success();
    }

    private async Task<ScopeDto> ToDtoAsync(object scope, CancellationToken cancellationToken)
    {
        var resources = await _scopeManager.GetResourcesAsync(scope, cancellationToken);

        return new ScopeDto
        {
            Name = await _scopeManager.GetNameAsync(scope, cancellationToken) ?? string.Empty,
            DisplayName = await _scopeManager.GetDisplayNameAsync(scope, cancellationToken),
            Description = await _scopeManager.GetDescriptionAsync(scope, cancellationToken),
            Resources = resources.ToArray()
        };
    }
}
