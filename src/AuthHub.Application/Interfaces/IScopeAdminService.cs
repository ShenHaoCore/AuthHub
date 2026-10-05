using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Scopes;

namespace AuthHub.Application.Interfaces;

/// <summary>Scope 管理（内部使用 <c>IOpenIddictScopeManager</c>）。</summary>
public interface IScopeAdminService
{
    Task<IReadOnlyCollection<ScopeDto>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Result<ScopeDto>> GetAsync(string name, CancellationToken cancellationToken = default);

    Task<Result<ScopeDto>> CreateAsync(CreateScopeRequest request, CancellationToken cancellationToken = default);

    Task<Result<ScopeDto>> UpdateAsync(string name, UpdateScopeRequest request, CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(string name, CancellationToken cancellationToken = default);
}
