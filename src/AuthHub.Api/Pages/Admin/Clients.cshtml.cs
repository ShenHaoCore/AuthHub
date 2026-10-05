using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Clients;
using AuthHub.Application.DTOs.Scopes;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuthHub.Api.Pages.Admin;

/// <summary>可勾选的 Scope。<see cref="IsProtocol"/> 标记 OIDC 标准 scope（不落数据库）。</summary>
public sealed record ScopeOption(string Name, string? DisplayName, bool IsProtocol);

/// <summary>
/// OAuth 客户端管理。
///
/// 两个必须小心的点（都会静默破坏客户端，页面里也做了提示）：
///
/// 1) **Scope 是整体覆盖而非增量**。<c>UpdateAsync</c> 用请求里的 scope 重建权限集合，
///    而 <c>openid</c> / <c>offline_access</c> 由 OpenIddict 内部处理、**不存在于
///    OpenIddictScope 表**里。如果编辑表单只列出数据库中的 scope，保存一次就会把
///    这两个删掉 —— 该客户端随后再也签不出 ID Token，刷新令牌也会失效。
///    因此这里把它们作为“协议 scope”一并列出，参与往返；客户端若还带着目录之外的
///    scope，也在弹窗里单独成组，保证不会被无声丢掉。
///
/// 2) **明文密钥只出现一次**。创建与轮换都只会返回一次明文 ——
///    密钥在库里只存哈希，没有任何接口能读回来。因此用 TempData
///    把密钥带到重定向后的 GET 上做一次性展示（TempData 读取即删除，且由
///    Data Protection 加密、HttpOnly，不会落到浏览器可读的位置）。
/// </summary>
[Authorize(Policy = AuthHubConstants.Policies.Ui.ClientsManage)]
public class ClientsModel : AdminPageModel
{
    private const int DefaultPageSize = 20;

    /// <summary>一次性明文密钥的 TempData 键（读取即删除）。</summary>
    private const string SecretValueKey = "ClientSecretValue";

    /// <summary>明文密钥所属客户端的 TempData 键。</summary>
    private const string SecretClientIdKey = "ClientSecretClientId";

    private readonly IClientAdminService _clients;
    private readonly IScopeAdminService _scopes;

    public ClientsModel(IClientAdminService clients, IScopeAdminService scopes)
    {
        _clients = clients;
        _scopes = scopes;
    }

    // ------------------------------------------------------------------ 查询条件（GET）

    [BindProperty(SupportsGet = true, Name = "page")]
    public int CurrentPage { get; set; } = 1;

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Search { get; set; }

    // ------------------------------------------------------------------ 表单字段（POST）

    [BindProperty] public string? NewClientId { get; set; }

    [BindProperty] public string? NewDisplayName { get; set; }

    [BindProperty] public string? NewClientType { get; set; }

    [BindProperty] public string? NewApplicationType { get; set; }

    [BindProperty] public string? NewClientSecret { get; set; }

    [BindProperty] public string? NewRedirectUris { get; set; }

    [BindProperty] public string? NewPostLogoutRedirectUris { get; set; }

    [BindProperty] public string[] NewGrantTypes { get; set; } = Array.Empty<string>();

    [BindProperty] public string[] NewScopes { get; set; } = Array.Empty<string>();

    [BindProperty] public bool NewRequirePkce { get; set; } = true;

    [BindProperty] public bool NewRequireConsent { get; set; }

    [BindProperty] public string? EditClientId { get; set; }

    [BindProperty] public string? EditDisplayName { get; set; }

    [BindProperty] public string? EditRedirectUris { get; set; }

    [BindProperty] public string? EditPostLogoutRedirectUris { get; set; }

    [BindProperty] public string[] EditGrantTypes { get; set; } = Array.Empty<string>();

    [BindProperty] public string[] EditScopes { get; set; } = Array.Empty<string>();

    [BindProperty] public bool EditRequirePkce { get; set; }

    [BindProperty] public bool EditRequireConsent { get; set; }

    [BindProperty] public string? TargetClientId { get; set; }

    // ------------------------------------------------------------------ 视图状态

    public PagedResult<ClientDto> Clients { get; private set; } = PagedResult<ClientDto>.Empty(1, DefaultPageSize);

    /// <summary>可勾选的 Scope（协议 scope + 数据库中的 scope）。</summary>
    public IReadOnlyList<ScopeOption> ScopeOptions { get; private set; } = Array.Empty<ScopeOption>();

    /// <summary>支持的授权类型（与验证器 / 翻译器的白名单一致）。</summary>
    public static IReadOnlyList<string> GrantTypeOptions { get; } = new[]
    {
        "authorization_code", "refresh_token", "client_credentials", "password", "implicit"
    };

    /// <summary>一次性展示的明文密钥（创建 / 轮换后）。</summary>
    public string? RevealedSecret { get; private set; }

    /// <summary>明文密钥所属的客户端。</summary>
    public string? RevealedSecretClientId { get; private set; }

    // ------------------------------------------------------------------ 基类钩子（见 AdminPageModel）

    protected override string ListPath => "/admin/clients";

    protected override string? EditingId => EditClientId;

    public Pages.Shared.PaginationModel Pagination => new()
    {
        Page = Clients.Page,
        PageSize = Clients.PageSize,
        Total = Clients.Total,
        Path = Request.Path,
        Query = new Dictionary<string, string?>(StringComparer.Ordinal) { ["q"] = Search }
    };

    /// <summary>某个客户端的 scope 选项：目录选项 + 该客户端独有的 scope（避免编辑一次就丢配置）。</summary>
    public IReadOnlyList<ScopeOption> ScopeOptionsFor(ClientDto client)
    {
        var known = ScopeOptions.Select(option => option.Name).ToHashSet(StringComparer.Ordinal);
        var extras = client.AllowedScopes
            .Where(scope => !known.Contains(scope))
            .Select(scope => new ScopeOption(scope, "该客户端已有，但不在 Scope 目录中", IsProtocol: false))
            .ToArray();

        return extras.Length == 0 ? ScopeOptions : ScopeOptions.Concat(extras).ToArray();
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "客户端";
        ViewData["NavKey"] = "clients";

        // 读取即删除：密钥只展示这一次
        if (TempData[SecretValueKey] is string secret)
        {
            RevealedSecret = secret;
            RevealedSecretClientId = TempData[SecretClientIdKey] as string;
        }

        await LoadDataAsync(cancellationToken);
    }

    // ------------------------------------------------------------------ POST：新建

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(NewClientId))
        {
            return await FailAsync(CreateDialog, Error.Validation("ClientId 不能为空。"), cancellationToken);
        }

        var grantTypes = NewGrantTypes.Length > 0
            ? NewGrantTypes
            : new[] { "authorization_code", "refresh_token" };

        var scopes = NewScopes.Length > 0
            ? NewScopes
            : new[] { AuthHubConstants.Scopes.OpenId, AuthHubConstants.Scopes.Profile, AuthHubConstants.Scopes.ApiRead };

        var result = await _clients.CreateAsync(
            new CreateClientRequest(
                ClientId: NewClientId.Trim(),
                DisplayName: TrimToNull(NewDisplayName),
                ClientSecret: TrimToNull(NewClientSecret),
                ApplicationType: string.IsNullOrWhiteSpace(NewApplicationType) ? null : NewApplicationType,
                ClientType: string.IsNullOrWhiteSpace(NewClientType) ? null : NewClientType,
                RedirectUris: SplitLines(NewRedirectUris),
                PostLogoutRedirectUris: SplitLines(NewPostLogoutRedirectUris),
                GrantTypes: grantTypes,
                Scopes: scopes,
                RequirePkce: NewRequirePkce,
                RequireConsent: NewRequireConsent),
            cancellationToken);

        if (result.IsFailure)
        {
            return await FailAsync(CreateDialog, result.Error, cancellationToken);
        }

        return RedirectWithSecret(
            result.Value.Client.ClientId,
            result.Value.PlainTextSecret,
            $"已创建客户端 {result.Value.Client.ClientId}。");
    }

    // ------------------------------------------------------------------ POST：保存编辑

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(EditClientId))
        {
            return await FailAsync(EditDialog, Error.Validation("缺少 ClientId。"), cancellationToken);
        }

        var result = await _clients.UpdateAsync(
            EditClientId,
            new UpdateClientRequest(
                DisplayName: TrimToNull(EditDisplayName),
                RedirectUris: SplitLines(EditRedirectUris),
                PostLogoutRedirectUris: SplitLines(EditPostLogoutRedirectUris),
                GrantTypes: EditGrantTypes,
                Scopes: EditScopes,
                RequirePkce: EditRequirePkce,
                RequireConsent: EditRequireConsent),
            cancellationToken);

        if (result.IsFailure)
        {
            return await FailAsync(EditDialog, result.Error, cancellationToken);
        }

        return RedirectBackWithSuccess($"已保存客户端 {EditClientId} 的配置。");
    }

    // ------------------------------------------------------------------ POST：轮换密钥

    public async Task<IActionResult> OnPostRotateSecretAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(TargetClientId))
        {
            return await FailAsync(null, Error.Validation("缺少 ClientId。"), cancellationToken);
        }

        var result = await _clients.RotateSecretAsync(TargetClientId, cancellationToken);
        if (result.IsFailure)
        {
            return await FailAsync(null, result.Error, cancellationToken);
        }

        return RedirectWithSecret(
            result.Value.ClientId,
            result.Value.PlainTextSecret,
            $"已轮换客户端 {result.Value.ClientId} 的密钥，旧密钥立即失效。");
    }

    // ------------------------------------------------------------------ POST：删除

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(TargetClientId))
        {
            return await FailAsync(null, Error.Validation("缺少 ClientId。"), cancellationToken);
        }

        var result = await _clients.DeleteAsync(TargetClientId, cancellationToken);
        if (result.IsFailure)
        {
            return await FailAsync(null, result.Error, cancellationToken);
        }

        return RedirectBackWithSuccess($"已删除客户端 {TargetClientId}。该客户端已签发的令牌会在下次校验时被拒绝。");
    }

    // ------------------------------------------------------------------ 内部辅助

    protected override async Task LoadDataAsync(CancellationToken cancellationToken)
    {
        var scopes = await _scopes.GetAllAsync(cancellationToken);

        // openid / offline_access 由 OpenIddict 内部处理、不落 OpenIddictScope 表，
        // 但它们会出现在客户端的 scp: 权限里，必须列出来参与往返（见类注释）
        var options = new List<ScopeOption>
        {
            new(AuthHubConstants.Scopes.OpenId, "OIDC 标准 · 签发 ID Token 的前提", IsProtocol: true),
            new(AuthHubConstants.Scopes.OfflineAccess, "OIDC 标准 · 请求刷新令牌的前提", IsProtocol: true)
        };

        options.AddRange(scopes.Select(scope => new ScopeOption(
            scope.Name,
            string.IsNullOrWhiteSpace(scope.Description) ? scope.DisplayName : scope.Description,
            IsProtocol: false)));

        ScopeOptions = options;

        Clients = await _clients.QueryAsync(CurrentPage, DefaultPageSize, Search, cancellationToken);
    }

    /// <summary>带着一次性明文密钥回到列表页做展示。</summary>
    private IActionResult RedirectWithSecret(string clientId, string? secret, string message)
    {
        SetSuccessMessage(string.IsNullOrWhiteSpace(secret)
            ? message
            : $"{message} 明文密钥只在下方显示这一次，请立即转交调用方。");

        if (!string.IsNullOrWhiteSpace(secret))
        {
            TempData[SecretValueKey] = secret;
            TempData[SecretClientIdKey] = clientId;
        }

        return RedirectBack();
    }
}
