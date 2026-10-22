using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthHub.Application.Common;
using AuthHub.Application.Interfaces;
using AuthHub.Application.Options;
using AuthHub.Domain.Enums;
using AuthHub.Domain.Interfaces;
using AuthHub.Infrastructure.Configuration;
using AuthHub.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using Oidc = OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthHub.Infrastructure.Services;

/// <summary>
/// CIMD 客户端的"懒注册"：首次见到某个 URL 型 <c>client_id</c> 时拉取元数据并落库，
/// 之后按 TTL 刷新。
///
/// <para><b>为什么不每次请求都去拉</b>：授权码流程里同一个 client_id 会出现在授权请求、
/// 令牌请求（兑换与刷新）等多个环节；每次出站请求既慢又把可用性绑在第三方站点上。
/// 而**完全不拉**（只在第一次拉）又会让客户端改了回调地址之后永远连不上。
/// TTL 是这两者之间的折中：默认 24 小时，且拉取失败时降级用缓存。</para>
///
/// <para><b>为什么可以降级</b>：CIMD 的元数据由客户端自己托管，第三方站点抖动是常态。
/// 缓存未过期却因为一次网络失败把用户挡在门外，收益（更早发现地址变更）远小于代价。
/// 但"从未成功拉过"没有缓存可降级，只能拒。</para>
/// </summary>
internal sealed class CimdClientRegistrar : ICimdClientRegistrar
{
    private readonly IOpenIddictApplicationManager _applications;
    private readonly ICimdMetadataFetcher _fetcher;
    private readonly IAuditLogService _audit;
    private readonly IClock _clock;
    private readonly CimdOptions _options;
    private readonly string[] _resources;
    private readonly ILogger<CimdClientRegistrar> _logger;

    public CimdClientRegistrar(
        IOpenIddictApplicationManager applications,
        ICimdMetadataFetcher fetcher,
        IAuditLogService audit,
        IClock clock,
        IOptions<CimdOptions> options,
        IConfiguration configuration,
        ILogger<CimdClientRegistrar> logger)
    {
        _applications = applications;
        _fetcher = fetcher;
        _audit = audit;
        _clock = clock;
        _options = options.Value;
        _logger = logger;

        // 资源权限：显式配了就用配置，否则沿用服务端认定的资源全集。
        // 资源本身已经是白名单（AuthHub:Resources），客户端请求其中任何一项都合理 ——
        // 真正的受众收窄发生在令牌的 aud 上，不在这里。
        _resources = _options.AllowedResources.Count > 0
            ? _options.AllowedResources
                .Where(resource => !string.IsNullOrWhiteSpace(resource))
                .Select(resource => resource.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : AuthHubResourceRegistry.Load(configuration);
    }

    public async Task<CimdResolution> ResolveAsync(string clientId, CancellationToken cancellationToken = default)
    {
        // 不是 https:443 的绝对地址 → 不是 CIMD，交给 OpenIddict 按普通 client_id 处理。
        // 这一步也让"攻击者拿任意字符串当 client_id"无法触发任何出站请求。
        if (!Uri.TryCreate(clientId, UriKind.Absolute, out var metadataUri)
            || !CimdAddressPolicy.IsAllowedMetadataUri(metadataUri))
        {
            return CimdResolution.NotApplicable;
        }

        var application = await _applications.FindByClientIdAsync(clientId, cancellationToken);

        // 同名的应用存在但不是自动注册来的 → 手工登记优先，行为完全不变
        if (application is not null && !await IsCimdRegisteredAsync(application, cancellationToken))
        {
            return CimdResolution.NotApplicable;
        }

        if (application is not null && await IsDisabledAsync(application, cancellationToken))
        {
            return CimdResolution.Rejected("该客户端已被管理员禁用，请联系服务提供方。");
        }

        var fetchedAt = application is null ? null : await GetFetchedAtAsync(application, cancellationToken);
        var isFresh = fetchedAt is not null && _clock.UtcNow - fetchedAt.Value < _options.RefreshInterval;

        if (isFresh)
        {
            return CimdResolution.Allowed;
        }

        var fetched = await _fetcher.FetchAsync(metadataUri, cancellationToken);

        if (!fetched.IsSuccess)
        {
            return await DegradeOrRejectAsync(application, clientId, fetched.Error ?? "拉取元数据失败。", cancellationToken);
        }

        if (!CimdClientMetadata.TryParse(fetched.Document!, clientId, _options, out var metadata, out var error))
        {
            return await DegradeOrRejectAsync(application, clientId, error ?? "元数据校验未通过。", cancellationToken);
        }

        var digest = ComputeDigest(fetched.Document!);

        if (application is null)
        {
            await CreateAsync(metadata!, digest, cancellationToken);
        }
        else
        {
            await UpdateAsync(application, metadata!, digest, cancellationToken);
        }

        return CimdResolution.Allowed;
    }

    // ------------------------------------------------------------------ 注册与刷新

    private async Task CreateAsync(CimdClientMetadata metadata, string digest, CancellationToken cancellationToken)
    {
        var descriptor = BuildDescriptor(metadata, digest);
        descriptor.Properties[CimdProperties.FetchedAt] =
            JsonSerializer.SerializeToElement(_clock.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        descriptor.Properties[CimdProperties.Disabled] = JsonSerializer.SerializeToElement(false);

        try
        {
            await _applications.CreateAsync(descriptor, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 并发首访：两个请求同时发现"未注册"，一个成功、另一个撞唯一索引。
            // 这不是错误 —— 只要库里已经有这条记录，本次请求就继续。
            if (await _applications.FindByClientIdAsync(metadata.ClientId, cancellationToken) is not null)
            {
                _logger.LogDebug("CIMD 客户端 {ClientId} 已由并发请求注册，本次跳过创建。", metadata.ClientId);
                return;
            }

            throw;
        }

        await _audit.LogAsync(
            new AuditEntry(
                AuditActionType.ClientCreated,
                true,
                ClientId: metadata.ClientId,
                Details: $"CIMD 自动注册：{metadata.ClientName}；" +
                         $"application_type={metadata.ApplicationType}；" +
                         $"scope={string.Join(' ', metadata.Scopes)}"),
            cancellationToken);

        _logger.LogInformation(
            "CIMD 客户端已自动注册 | ClientId={ClientId} | 名称={ClientName} | 类型={ApplicationType} | 回调={RedirectUris}",
            metadata.ClientId,
            metadata.ClientName,
            metadata.ApplicationType,
            metadata.RedirectUris);
    }

    private async Task UpdateAsync(
        object application,
        CimdClientMetadata metadata,
        string digest,
        CancellationToken cancellationToken)
    {
        var descriptor = BuildDescriptor(metadata, digest);

        // 保留管理员设置的禁用标记与原始首次拉取时间：刷新元数据不该顺带"解禁"
        var properties = await _applications.GetPropertiesAsync(application, cancellationToken);

        descriptor.Properties[CimdProperties.Disabled] = properties.TryGetValue(CimdProperties.Disabled, out var disabled)
            ? disabled
            : JsonSerializer.SerializeToElement(false);

        descriptor.Properties[CimdProperties.FetchedAt] =
            JsonSerializer.SerializeToElement(_clock.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        await _applications.UpdateAsync(application, descriptor, cancellationToken);

        await _audit.LogAsync(
            new AuditEntry(
                AuditActionType.ClientUpdated,
                true,
                ClientId: metadata.ClientId,
                Details: "CIMD 元数据已刷新"),
            cancellationToken);

        _logger.LogInformation(
            "CIMD 客户端元数据已刷新 | ClientId={ClientId} | 摘要={Digest}",
            metadata.ClientId,
            digest[..12]);
    }

    /// <summary>
    /// 构建 OpenIddict 的应用描述。CIMD 客户端一律是 **public + 授权码 + PKCE**：
    /// 没有登记密钥的环节，所以不可能是 confidential；不强制 PKCE 则授权码在
    /// 公共客户端手里等于明文口令。
    /// </summary>
    private OpenIddictApplicationDescriptor BuildDescriptor(CimdClientMetadata metadata, string digest)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = metadata.ClientId,
            DisplayName = metadata.ClientName,
            ApplicationType = metadata.ApplicationType,
            ClientType = Oidc.ClientTypes.Public,
            ConsentType = _options.RequireConsent ? Oidc.ConsentTypes.Explicit : Oidc.ConsentTypes.Implicit
        };

        foreach (var uri in metadata.RedirectUris)
        {
            descriptor.RedirectUris.Add(new Uri(uri));
        }

        descriptor.Permissions.Add(Oidc.Permissions.Endpoints.Authorization);
        descriptor.Permissions.Add(Oidc.Permissions.GrantTypes.AuthorizationCode);
        descriptor.Permissions.Add(Oidc.Permissions.ResponseTypes.Code);

        if (metadata.GrantTypes.Contains("refresh_token", StringComparer.Ordinal))
        {
            descriptor.Permissions.Add(Oidc.Permissions.GrantTypes.RefreshToken);
        }

        foreach (var scope in metadata.Scopes)
        {
            descriptor.Permissions.Add(Oidc.Permissions.Prefixes.Scope + scope);
        }

        foreach (var resource in _resources)
        {
            descriptor.Permissions.Add(Oidc.Permissions.Prefixes.Resource + resource);
        }

        descriptor.Requirements.Add(Oidc.Requirements.Features.ProofKeyForCodeExchange);

        descriptor.Properties[CimdProperties.Marker] = JsonSerializer.SerializeToElement(true);
        descriptor.Properties[CimdProperties.Source] = JsonSerializer.SerializeToElement(metadata.ClientId);
        descriptor.Properties[CimdProperties.Digest] = JsonSerializer.SerializeToElement(digest);

        return descriptor;
    }

    // ------------------------------------------------------------------ 降级与判定

    /// <summary>
    /// 拉取或校验失败时的分岔：有缓存 → 记一条警告继续用；无缓存 → 拒。
    /// </summary>
    private async Task<CimdResolution> DegradeOrRejectAsync(
        object? application,
        string clientId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (application is not null)
        {
            _logger.LogWarning(
                "CIMD 元数据刷新失败，继续使用缓存 | ClientId={ClientId} | 原因={Reason}",
                clientId,
                reason);

            return CimdResolution.Allowed;
        }

        await _audit.LogAsync(
            new AuditEntry(
                AuditActionType.ClientCreated,
                false,
                ClientId: clientId,
                Details: $"CIMD 自动注册被拒：{reason}"),
            cancellationToken);

        _logger.LogWarning("CIMD 客户端注册被拒 | ClientId={ClientId} | 原因={Reason}", clientId, reason);

        return CimdResolution.Rejected(reason);
    }

    private async Task<bool> IsCimdRegisteredAsync(object application, CancellationToken cancellationToken)
    {
        var properties = await _applications.GetPropertiesAsync(application, cancellationToken);

        return properties.TryGetValue(CimdProperties.Marker, out var marker)
               && marker.ValueKind == JsonValueKind.True;
    }

    private async Task<bool> IsDisabledAsync(object application, CancellationToken cancellationToken)
    {
        var properties = await _applications.GetPropertiesAsync(application, cancellationToken);

        return properties.TryGetValue(CimdProperties.Disabled, out var disabled)
               && disabled.ValueKind == JsonValueKind.True;
    }

    private async Task<DateTimeOffset?> GetFetchedAtAsync(object application, CancellationToken cancellationToken)
    {
        var properties = await _applications.GetPropertiesAsync(application, cancellationToken);

        if (!properties.TryGetValue(CimdProperties.FetchedAt, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// 内容摘要：同一 URL 返回了不同内容时，日志里能一眼看出"变了"。
    /// 缓存投毒本身由 TTL + 摘要记录两条线索兜住（发现问题时能回溯到具体版本）。
    /// </summary>
    private static string ComputeDigest(string document) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document))).ToLowerInvariant();
}

/// <summary>
/// CIMD 相关属性在 OpenIddict 应用 <c>Properties</c> 里的键名。
///
/// <para>收在一处是因为它们同时被三处读写：注册器（写）、解析判定（读）、
/// 管理后台（展示与禁用）。字符串散落在各处的后果是"禁用按钮点了没反应"，
/// 而编译期不会有任何提示。</para>
/// </summary>
public static class CimdProperties
{
    /// <summary>标记这条应用是由 CIMD 自动注册的（而非手工登记）。</summary>
    public const string Marker = "authhub:cimd";

    /// <summary>元数据来源 URL（等于 client_id，单独存一份便于排查）。</summary>
    public const string Source = "authhub:cimd:source";

    /// <summary>最近一次成功拉取的时间（ISO 8601）。</summary>
    public const string FetchedAt = "authhub:cimd:fetched_at";

    /// <summary>最近一次成功拉取的内容摘要（SHA-256）。</summary>
    public const string Digest = "authhub:cimd:digest";

    /// <summary>管理员手动禁用的标记。禁用后授权端点直接拒绝。</summary>
    public const string Disabled = "authhub:cimd:disabled";
}
