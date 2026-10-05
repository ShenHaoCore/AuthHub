using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Clients;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using Oidc = OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthHub.Infrastructure.Services;

/// <summary>
/// 客户端管理，基于 <see cref="IOpenIddictApplicationManager"/>。
///
/// 两点设计说明：
/// 1) 客户端密钥只以哈希形式存储，OpenIddict 不提供回读明文的接口，
///    因此“密钥已配置”这一状态通过 EF Store 读取哈希是否存在来判断；
///    轮换时使用 <c>UpdateAsync(application, secret)</c> 重载，旧密钥立即失效。
/// 2) 更新客户端时如果 Populate 覆盖了密钥字段，会显式把原哈希写回，
///    保证“不改密钥的更新”不会意外让客户端失去密钥（见 PreserveSecretAsync）。
/// </summary>
public sealed class OpenIddictClientAdminService : IClientAdminService
{
    private readonly IOpenIddictApplicationManager _applicationManager;
    private readonly IServiceProvider _services;
    private readonly IAuditLogService _audit;
    private readonly ILogger<OpenIddictClientAdminService> _logger;

    public OpenIddictClientAdminService(
        IOpenIddictApplicationManager applicationManager,
        IServiceProvider services,
        IAuditLogService audit,
        ILogger<OpenIddictClientAdminService> logger)
    {
        _applicationManager = applicationManager;
        _services = services;
        _audit = audit;
        _logger = logger;
    }

    public async Task<PagedResult<ClientDto>> QueryAsync(
        int page,
        int pageSize,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = pageSize switch { <= 0 => 20, > 200 => 200, _ => pageSize };

        // 客户端数量级很小（一个 IdP 通常几十到几百个），
        // 而 IOpenIddictApplicationManager 未提供按名称的服务端过滤，
        // 因此这里全量读取后内存过滤 / 分页，避免引入脆弱的表达式构造。
        var all = new List<ClientDto>();
        await foreach (var application in _applicationManager.ListAsync(null, null, cancellationToken))
        {
            all.Add(await ToDtoAsync(application, cancellationToken));
        }

        IEnumerable<ClientDto> filtered = all;

        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(c =>
                c.ClientId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (c.DisplayName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var ordered = filtered.OrderBy(c => c.ClientId, StringComparer.OrdinalIgnoreCase).ToArray();
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArray();

        return new PagedResult<ClientDto>(items, ordered.Length, page, pageSize);
    }

    public async Task<Result<ClientDto>> GetAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var application = await _applicationManager.FindByClientIdAsync(clientId, cancellationToken);
        if (application is null)
        {
            return Result.Failure<ClientDto>(Error.NotFound($"客户端 {clientId} 不存在。"));
        }

        return Result.Success(await ToDtoAsync(application, cancellationToken));
    }

    public async Task<Result<ClientCreatedResponse>> CreateAsync(
        CreateClientRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await _applicationManager.FindByClientIdAsync(request.ClientId, cancellationToken) is not null)
        {
            return Result.Failure<ClientCreatedResponse>(Error.Conflict($"客户端 {request.ClientId} 已存在。"));
        }

        var grantTypes = (request.GrantTypes ?? new[] { "authorization_code", "refresh_token" })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var invalid = grantTypes.Where(g => !OpenIddictPermissionTranslator.IsSupportedGrantType(g)).ToArray();
        if (invalid.Length > 0)
        {
            return Result.Failure<ClientCreatedResponse>(
                Error.Validation($"不支持的授权类型：{string.Join(", ", invalid)}。"));
        }

        var scopes = (request.Scopes ?? new[] { "profile", "api:read" })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var redirectUris = request.RedirectUris ?? Array.Empty<string>();
        var postLogoutUris = request.PostLogoutRedirectUris ?? Array.Empty<string>();

        // 机器客户端（client_credentials）必须持密钥，且没有交互式重定向
        var needsSecret = grantTypes.Contains("client_credentials", StringComparer.Ordinal);

        var clientType = request.ClientType
                         ?? (needsSecret || !string.IsNullOrWhiteSpace(request.ClientSecret)
                             ? Oidc.ClientTypes.Confidential
                             : Oidc.ClientTypes.Public);

        if (clientType == Oidc.ClientTypes.Public && needsSecret)
        {
            return Result.Failure<ClientCreatedResponse>(
                Error.Validation("client_credentials 授权类型必须使用 confidential 客户端并配置密钥。"));
        }

        var secret = request.ClientSecret;
        if (string.IsNullOrWhiteSpace(secret) && clientType == Oidc.ClientTypes.Confidential)
        {
            secret = OpenIddictPermissionTranslator.GenerateSecret();
        }

        var usesInteractiveFlow = grantTypes.Contains("authorization_code", StringComparer.Ordinal)
                                  || grantTypes.Contains("implicit", StringComparer.Ordinal);

        var applicationType = request.ApplicationType;
        if (string.IsNullOrWhiteSpace(applicationType) && usesInteractiveFlow)
        {
            applicationType = clientType == Oidc.ClientTypes.Public ? Oidc.ApplicationTypes.Native : Oidc.ApplicationTypes.Web;
        }

        if (usesInteractiveFlow && redirectUris.Count == 0)
        {
            return Result.Failure<ClientCreatedResponse>(
                Error.Validation("使用授权码 / 隐式流程的客户端必须至少配置一个 redirectUri。"));
        }

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = request.ClientId,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? request.ClientId : request.DisplayName,
            ClientType = clientType,
            ConsentType = request.RequireConsent ? Oidc.ConsentTypes.Explicit : Oidc.ConsentTypes.Implicit
        };

        if (!string.IsNullOrWhiteSpace(applicationType))
        {
            descriptor.ApplicationType = applicationType;
        }

        if (!string.IsNullOrWhiteSpace(secret))
        {
            descriptor.ClientSecret = secret;
        }

        foreach (var uri in redirectUris)
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                descriptor.RedirectUris.Add(parsed);
            }
        }

        foreach (var uri in postLogoutUris)
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                descriptor.PostLogoutRedirectUris.Add(parsed);
            }
        }

        var permissions = OpenIddictPermissionTranslator.BuildPermissions(
            grantTypes, scopes, redirectUris.Count > 0, postLogoutUris.Count > 0);

        foreach (var permission in permissions)
        {
            descriptor.Permissions.Add(permission);
        }

        // public 客户端（SPA / 移动端）强制 PKCE，防止授权码被拦截后直接兑换
        var requirePkce = request.RequirePkce || clientType == Oidc.ClientTypes.Public;
        if (requirePkce && grantTypes.Contains("authorization_code", StringComparer.Ordinal))
        {
            descriptor.Requirements.Add(Oidc.Requirements.Features.ProofKeyForCodeExchange);
        }

        var application = await _applicationManager.CreateAsync(descriptor, cancellationToken);

        await _audit.LogAsync(
            new AuditEntry(AuditActionType.ClientCreated, true, ClientId: request.ClientId, Details: $"授权类型：{string.Join(",", grantTypes)}；Scope：{string.Join(",", scopes)}"),
            cancellationToken);

        var dto = await ToDtoAsync(application, cancellationToken);

        // 明文密钥只在创建响应里出现这一次
        return Result.Success(new ClientCreatedResponse(dto, string.IsNullOrWhiteSpace(secret) ? null : secret));
    }

    public async Task<Result<ClientDto>> UpdateAsync(
        string clientId,
        UpdateClientRequest request,
        CancellationToken cancellationToken = default)
    {
        var application = await _applicationManager.FindByClientIdAsync(clientId, cancellationToken);
        if (application is null)
        {
            return Result.Failure<ClientDto>(Error.NotFound($"客户端 {clientId} 不存在。"));
        }

        var existingPermissions = await _applicationManager.GetPermissionsAsync(application, cancellationToken);
        var existingGrantTypes = OpenIddictPermissionTranslator.ExtractGrantTypes(existingPermissions);
        var existingScopes = OpenIddictPermissionTranslator.ExtractScopes(existingPermissions);

        var grantTypes = request.GrantTypes?.Distinct(StringComparer.Ordinal).ToArray() ?? existingGrantTypes;
        var scopes = request.Scopes?.Distinct(StringComparer.Ordinal).ToArray() ?? existingScopes;

        var invalid = grantTypes.Where(g => !OpenIddictPermissionTranslator.IsSupportedGrantType(g)).ToArray();
        if (invalid.Length > 0)
        {
            return Result.Failure<ClientDto>(Error.Validation($"不支持的授权类型：{string.Join(", ", invalid)}。"));
        }

        var redirectUris = request.RedirectUris
                           ?? (await _applicationManager.GetRedirectUrisAsync(application, cancellationToken))
                               .ToArray();

        var postLogoutUris = request.PostLogoutRedirectUris
                             ?? (await _applicationManager.GetPostLogoutRedirectUrisAsync(application, cancellationToken))
                                 .ToArray();

        var existingRequirements = await _applicationManager.GetRequirementsAsync(application, cancellationToken);

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            DisplayName = request.DisplayName ?? await _applicationManager.GetDisplayNameAsync(application, cancellationToken),
            ApplicationType = await _applicationManager.GetApplicationTypeAsync(application, cancellationToken),
            ClientType = await _applicationManager.GetClientTypeAsync(application, cancellationToken),
            ConsentType = request.RequireConsent is null
                ? await _applicationManager.GetConsentTypeAsync(application, cancellationToken)
                : request.RequireConsent.Value ? Oidc.ConsentTypes.Explicit : Oidc.ConsentTypes.Implicit
        };

        // 不触碰密钥：密钥轮换走 RotateSecretAsync
        descriptor.ClientSecret = null;

        foreach (var uri in redirectUris)
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                descriptor.RedirectUris.Add(parsed);
            }
        }

        foreach (var uri in postLogoutUris)
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                descriptor.PostLogoutRedirectUris.Add(parsed);
            }
        }

        var permissions = OpenIddictPermissionTranslator.BuildPermissions(
            grantTypes, scopes, redirectUris.Count > 0, postLogoutUris.Count > 0);

        foreach (var permission in permissions)
        {
            descriptor.Permissions.Add(permission);
        }

        var keepPkce = request.RequirePkce
                       ?? existingRequirements.Contains(Oidc.Requirements.Features.ProofKeyForCodeExchange, StringComparer.Ordinal);

        if (keepPkce && grantTypes.Contains("authorization_code", StringComparer.Ordinal))
        {
            descriptor.Requirements.Add(Oidc.Requirements.Features.ProofKeyForCodeExchange);
        }

        var secretHashBefore = await TryGetSecretHashAsync(application, cancellationToken);

        await _applicationManager.UpdateAsync(application, descriptor, cancellationToken);
        await PreserveSecretAsync(application, secretHashBefore, cancellationToken);

        await _audit.LogAsync(
            new AuditEntry(AuditActionType.ClientUpdated, true, ClientId: clientId),
            cancellationToken);

        return Result.Success(await ToDtoAsync(application, cancellationToken));
    }

    public async Task<Result<ClientSecretResponse>> RotateSecretAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        var application = await _applicationManager.FindByClientIdAsync(clientId, cancellationToken);
        if (application is null)
        {
            return Result.Failure<ClientSecretResponse>(Error.NotFound($"客户端 {clientId} 不存在。"));
        }

        var clientType = await _applicationManager.GetClientTypeAsync(application, cancellationToken);
        if (clientType == Oidc.ClientTypes.Public)
        {
            return Result.Failure<ClientSecretResponse>(
                Error.Validation("public 客户端不使用客户端密钥，无需轮换。"));
        }

        var secret = OpenIddictPermissionTranslator.GenerateSecret();
        await _applicationManager.UpdateAsync(application, secret, cancellationToken);

        await _audit.LogAsync(
            new AuditEntry(AuditActionType.ClientUpdated, true, ClientId: clientId, Details: "轮换客户端密钥"),
            cancellationToken);

        return Result.Success(new ClientSecretResponse(clientId, secret));
    }

    public async Task<Result> DeleteAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var application = await _applicationManager.FindByClientIdAsync(clientId, cancellationToken);
        if (application is null)
        {
            return Result.Failure(Error.NotFound($"客户端 {clientId} 不存在。"));
        }

        await _applicationManager.DeleteAsync(application, cancellationToken);

        await _audit.LogAsync(
            new AuditEntry(AuditActionType.ClientDeleted, true, ClientId: clientId),
            cancellationToken);

        return Result.Success();
    }

    // ------------------------------------------------------------------ 内部辅助

    private async Task<ClientDto> ToDtoAsync(object application, CancellationToken cancellationToken)
    {
        var permissions = await _applicationManager.GetPermissionsAsync(application, cancellationToken);
        var redirectUris = await _applicationManager.GetRedirectUrisAsync(application, cancellationToken);
        var postLogoutUris = await _applicationManager.GetPostLogoutRedirectUrisAsync(application, cancellationToken);
        var requirements = await _applicationManager.GetRequirementsAsync(application, cancellationToken);
        var clientType = await _applicationManager.GetClientTypeAsync(application, cancellationToken);

        var secretHash = await TryGetSecretHashAsync(application, cancellationToken);

        return new ClientDto
        {
            ClientId = await _applicationManager.GetClientIdAsync(application, cancellationToken) ?? string.Empty,
            DisplayName = await _applicationManager.GetDisplayNameAsync(application, cancellationToken),
            ApplicationType = await _applicationManager.GetApplicationTypeAsync(application, cancellationToken),
            ClientType = clientType,
            ConsentType = await _applicationManager.GetConsentTypeAsync(application, cancellationToken),
            RedirectUris = redirectUris.ToArray(),
            PostLogoutRedirectUris = postLogoutUris.ToArray(),
            Permissions = permissions.ToArray(),
            Requirements = requirements.ToArray(),
            HasSecret = secretHash is not null,
            GrantTypes = OpenIddictPermissionTranslator.ExtractGrantTypes(permissions),
            AllowedScopes = OpenIddictPermissionTranslator.ExtractScopes(permissions)
        };
    }

    /// <summary>
    /// 读取客户端密钥的哈希值。OpenIddict 的 Manager API 刻意不暴露密钥，
    /// 因此这里下探到 EF Store；若当前使用的不是 EF Store，
    /// 则退化为“confidential 即视为已配置密钥”。
    /// </summary>
    private async Task<string?> TryGetSecretHashAsync(object application, CancellationToken cancellationToken)
    {
        var store = _services.GetService<IOpenIddictApplicationStore<OpenIddictEntityFrameworkCoreApplication>>();
        if (store is null || application is not OpenIddictEntityFrameworkCoreApplication entity)
        {
            var clientType = await _applicationManager.GetClientTypeAsync(application, cancellationToken);
            return clientType == Oidc.ClientTypes.Confidential ? "unknown" : null;
        }

        return await store.GetClientSecretAsync(entity, cancellationToken);
    }

    /// <summary>
    /// 更新时保证密钥不被清空：若更新前存在哈希、更新后哈希消失，则写回原值。
    /// 这样“只改显示名”之类的操作不会意外导致客户端失去密钥。
    /// </summary>
    private async Task PreserveSecretAsync(object application, string? secretHashBefore, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(secretHashBefore) || secretHashBefore == "unknown")
        {
            return;
        }

        try
        {
            var store = _services.GetService<IOpenIddictApplicationStore<OpenIddictEntityFrameworkCoreApplication>>();
            if (store is null || application is not OpenIddictEntityFrameworkCoreApplication entity)
            {
                return;
            }

            var currentHash = await store.GetClientSecretAsync(entity, cancellationToken);
            if (!string.IsNullOrEmpty(currentHash))
            {
                return;
            }

            await store.SetClientSecretAsync(entity, secretHashBefore, cancellationToken);
            await store.UpdateAsync(entity, cancellationToken);

            _logger.LogDebug("客户端 {ClientId} 更新时密钥字段被清空，已恢复原密钥哈希。",
                await _applicationManager.GetClientIdAsync(application, cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "恢复客户端密钥哈希失败，密钥可能已失效，请通过轮换接口重新签发。");
        }
    }
}
