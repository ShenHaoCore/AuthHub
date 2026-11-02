using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Clients;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using AuthHub.Domain.Enums;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using Oidc = OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthHub.Infrastructure.Services;

/// <summary>客户端管理，基于 <see cref="IOpenIddictApplicationManager"/>。</summary>
/// <remarks>
/// - 密钥只存哈希且 OpenIddict 不提供回读，"密钥已配置"经 EF Store 读哈希是否存在判断；
///   轮换用 <c>UpdateAsync(application, secret)</c> 重载，旧密钥立即失效。
/// - 更新走「取出原描述符、改字段、再写回」（见 UpdateAsync）：<c>PopulateAsync(application, descriptor)</c>
///   会无条件覆盖密钥与 Properties —— 空描述符会让 confidential 客户端丢密钥（校验直接抛异常），
///   并抹掉 CIMD 自动注册客户端的内部标记。
/// </remarks>
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

        // 客户端数量级很小（一个 IdP 通常几十到几百个），管理器又未提供按名称的服务端过滤，
        // 因此全量读取后内存过滤 / 分页，避免引入脆弱的表达式构造。
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

        var scopes = (request.Scopes ?? new[] { AuthHubConstants.Scopes.Profile, AuthHubConstants.Scopes.ApiRead })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var resources = (request.Resources ?? Array.Empty<string>())
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
            grantTypes, scopes, resources, redirectUris.Count > 0, postLogoutUris.Count > 0);

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
        var resources = request.Resources?.Distinct(StringComparer.Ordinal).ToArray()
                        ?? OpenIddictPermissionTranslator.ExtractResources(existingPermissions);

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

        // 先取出原描述符再改，不新建空描述符：UpdateAsync 内部的 PopulateAsync 会无条件把
        // descriptor.ClientSecret 写回（null 即清空）并覆盖 Properties —— 空描述符会让
        // confidential 客户端丢密钥（校验直接抛异常）、抹掉 CIMD 客户端的内部标记；
        // 原描述符带库里的哈希，原样写回则 Manager 走"不换密钥"分支，不会被二次哈希。
        var descriptor = new OpenIddictApplicationDescriptor();
        await _applicationManager.PopulateAsync(descriptor, application, cancellationToken);

        descriptor.DisplayName = request.DisplayName ?? await _applicationManager.GetDisplayNameAsync(application, cancellationToken);
        descriptor.ConsentType = request.RequireConsent is null
            ? await _applicationManager.GetConsentTypeAsync(application, cancellationToken)
            : request.RequireConsent.Value ? Oidc.ConsentTypes.Explicit : Oidc.ConsentTypes.Implicit;

        // 不触碰密钥：密钥轮换走 RotateSecretAsync
        descriptor.RedirectUris.Clear();
        descriptor.PostLogoutRedirectUris.Clear();

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
            grantTypes, scopes, resources, redirectUris.Count > 0, postLogoutUris.Count > 0);

        descriptor.Permissions.Clear();
        foreach (var permission in permissions)
        {
            descriptor.Permissions.Add(permission);
        }

        descriptor.Requirements.Clear();

        var keepPkce = request.RequirePkce
                       ?? existingRequirements.Contains(Oidc.Requirements.Features.ProofKeyForCodeExchange, StringComparer.Ordinal);

        if (keepPkce && grantTypes.Contains("authorization_code", StringComparer.Ordinal))
        {
            descriptor.Requirements.Add(Oidc.Requirements.Features.ProofKeyForCodeExchange);
        }

        await _applicationManager.UpdateAsync(application, descriptor, cancellationToken);

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

    public async Task<Result> SetAutoRegisteredDisabledAsync(
        string clientId,
        bool disabled,
        CancellationToken cancellationToken = default)
    {
        var application = await _applicationManager.FindByClientIdAsync(clientId, cancellationToken);
        if (application is null)
        {
            return Result.Failure(Error.NotFound($"客户端 {clientId} 不存在。"));
        }

        var properties = await _applicationManager.GetPropertiesAsync(application, cancellationToken);

        if (!properties.TryGetValue(CimdProperties.Marker, out var marker) || marker.ValueKind != JsonValueKind.True)
        {
            return Result.Failure(Error.Validation(
                "只有自动注册（CIMD）的客户端支持禁用；手工客户端请直接修改权限或删除。"));
        }

        // 直接从 Store 改 Properties 而不是走 UpdateAsync(descriptor)：后者会重建整条记录
        // （回调、权限、PKCE 漏抄一处就静默变行为），这里只碰"禁用标记"一个字段。
        var store = _services.GetService<IOpenIddictApplicationStore<OpenIddictEntityFrameworkCoreApplication>>();

        if (store is null || application is not OpenIddictEntityFrameworkCoreApplication entity)
        {
            return Result.Failure(Error.Failure(
                "NotSupported",
                "当前存储实现不支持修改客户端属性，无法禁用该客户端。"));
        }

        await store.SetPropertiesAsync(
            entity,
            properties.SetItem(CimdProperties.Disabled, JsonSerializer.SerializeToElement(disabled)),
            cancellationToken);
        await store.UpdateAsync(entity, cancellationToken);

        await _audit.LogAsync(
            new AuditEntry(
                AuditActionType.ClientUpdated,
                true,
                ClientId: clientId,
                Details: disabled ? "禁用自动注册的客户端" : "恢复自动注册的客户端"),
            cancellationToken);

        _logger.LogInformation(
            "CIMD 客户端状态已变更 | ClientId={ClientId} | 禁用={Disabled}",
            clientId,
            disabled);

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

        // CIMD 自动注册客户端与手工客户端共用 OpenIddictApplications 表，只能靠 Properties 里的标记区分。
        var properties = await _applicationManager.GetPropertiesAsync(application, cancellationToken);
        var isAutoRegistered = properties.TryGetValue(CimdProperties.Marker, out var marker)
                               && marker.ValueKind == JsonValueKind.True;

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
            AllowedScopes = OpenIddictPermissionTranslator.ExtractScopes(permissions),
            AllowedResources = OpenIddictPermissionTranslator.ExtractResources(permissions),
            IsAutoRegistered = isAutoRegistered,
            MetadataSourceUrl = isAutoRegistered
                && properties.TryGetValue(CimdProperties.Source, out var source)
                && source.ValueKind == JsonValueKind.String
                    ? source.GetString()
                    : null,
            IsDisabled = properties.TryGetValue(CimdProperties.Disabled, out var disabled)
                         && disabled.ValueKind == JsonValueKind.True
        };
    }

    /// <summary>「拿不到真实哈希、但客户端确实是 confidential」时的哨兵值，只用于 <see cref="ClientDto.HasSecret"/> 判断。</summary>
    private const string UnknownSecretHash = "unknown";

    /// <summary>
    /// 读取客户端密钥的哈希值。Manager API 刻意不暴露密钥，故下探到 EF Store；
    /// 当前不是 EF Store 时退化为"confidential 即视为已配置密钥"。
    /// </summary>
    private async Task<string?> TryGetSecretHashAsync(object application, CancellationToken cancellationToken)
    {
        var store = _services.GetService<IOpenIddictApplicationStore<OpenIddictEntityFrameworkCoreApplication>>();
        if (store is null || application is not OpenIddictEntityFrameworkCoreApplication entity)
        {
            var clientType = await _applicationManager.GetClientTypeAsync(application, cancellationToken);
            return clientType == Oidc.ClientTypes.Confidential ? UnknownSecretHash : null;
        }

        return await store.GetClientSecretAsync(entity, cancellationToken);
    }
}
