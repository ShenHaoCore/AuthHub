using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using AuthHub.Application.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace AuthHub.Api.Extensions;

/// <summary>
/// 企业微信扫码登录（自建应用 SSO）的协议适配。
///
/// <para>
/// 为什么不像 GitHub/Google 那样注册认证 handler：企业微信扫码确认后的回调参数是
/// <c>auth_code</c> 而非 OAuth 标准的 <c>code</c>，aspnet-contrib 也没有企业微信 provider，
/// 协议适配只能自实现。本类只负责「企业微信协议 → ClaimsPrincipal」：
/// 构造扫码页链接 → 校验回调 state → auth_code 换 userid → 取成员详情。
/// 产出的身份由控制器写入 Identity 外部 Cookie，转交统一的
/// <c>/account/external-callback</c> —— 绑定/建号/白名单/审计与其它提供商共用同一条链路。
/// </para>
///
/// <para>
/// 二维码在登录页「扫码登录」页签内以 iframe 直嵌（而非加载官方 wwLogin.js）：
/// 该 SDK 本质上也是生成指向同一地址的 iframe，自嵌省去第三方 JS、符合本站
/// CSP「无 CDN 引用」约束；CSP 仅放行 frame-src 到企业微信扫码域。
/// </para>
///
/// <para>
/// 邮箱信任口径：企业微信没有 <c>email_verified</c> 概念，但通讯录邮箱由<b>企业管理员</b>
/// 维护（不是成员自行声明），与 GitHub/Google 的「提供商已验证」同级，因此取到邮箱即补写
/// <c>email_verified=true</c>；取不到（应用未开通通讯录权限或管理员未填写）写
/// <c>false</c>，由下游按既有判据拒绝 —— fail-closed，与其它提供商一致。
/// </para>
///
/// <para>
/// 接口口径见企业微信文档「网页授权登录 / 构造扫码登录链接」。注意 redirect_uri 的域名
/// 必须在管理后台配置为可信域名（需所有权验证），localhost 无法联调。
/// </para>
/// </summary>
public sealed class WeComLoginService : IDisposable
{
    /// <summary>提供商名：与配置节、登录页按钮值、UserLogins.LoginProvider 对齐。</summary>
    public const string ProviderName = "WeCom";

    /// <summary>HttpClientFactory 里的命名客户端。</summary>
    public const string HttpClientName = "authhub.wecom";

    /// <summary>本站回调路径；完整地址 = {对外地址} + 该路径，需在企业微信后台按可信域名注册。</summary>
    public const string CallbackPath = "/signin-wecom";

    private const string AuthorizeEndpoint = "https://login.work.weixin.qq.com/wwlogin/sso/login";
    private const string TokenEndpoint = "https://qyapi.weixin.qq.com/cgi-bin/gettoken";
    private const string UserInfoEndpoint = "https://qyapi.weixin.qq.com/cgi-bin/auth/getuserinfo";
    private const string UserDetailEndpoint = "https://qyapi.weixin.qq.com/cgi-bin/auth/getuserdetail";

    /// <summary>state 有效期：留给用户掏出手机扫码确认的时间，超时回调一律拒绝。</summary>
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    /// <summary>access_token 缓存的安全边距：提前 5 分钟换新，避免用到临界过期的 token。</summary>
    private static readonly TimeSpan TokenExpiryMargin = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ExternalLoginOptions _options;
    private readonly ITimeLimitedDataProtector _protector;
    private readonly ILogger<WeComLoginService> _logger;

    // access_token 全企业共享、7200 秒有效且获取接口有频率限制：必须缓存复用。
    // 双检锁保证并发登录回调里只有一个请求去换新 token，其余直接吃缓存结果。
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _cachedAccessToken;
    private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;

    public WeComLoginService(
        IHttpClientFactory httpClientFactory,
        IOptions<ExternalLoginOptions> options,
        IDataProtectionProvider dataProtection,
        ILogger<WeComLoginService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        // state 用 DataProtection 限时保护：不可伪造、不可篡改、到期自毁。
        // 密钥环独立 purpose，与其它用途的票据互不相通。
        _protector = dataProtection.CreateProtector("authhub.wecom.state").ToTimeLimitedDataProtector();
    }

    /// <summary>
    /// 生成扫码面板链接与 state。该链接用作登录页「扫码登录」页签里 iframe 的 src
    /// （也可用于顶层跳转，本站不那样用）。state 同时承担两个职责：相关性校验
    /// （防登录 CSRF，与 OAuth correlation 同构）+ 携带 returnUrl（企业微信后台注册的
    /// 回调地址是固定的，returnUrl 没法挂查询串，只能随 state 往返）。
    /// 调用方负责把 state 另写一份 SameSite=Lax 的 Cookie 做双提交校验。
    /// </summary>
    public string CreateChallengeUrl(string returnUrl, string callbackAbsoluteUrl, out string state)
    {
        var wecom = _options.WeCom;
        state = _protector.Protect($"{Guid.NewGuid():N}|{returnUrl}", StateLifetime);

        // 参数口径见文档「构造扫码登录链接」：login_type=CorpApp 表示自建应用扫码，
        // appid 是企业 ID（corpid），不是应用凭证
        return $"{AuthorizeEndpoint}?login_type=CorpApp" +
               $"&appid={Uri.EscapeDataString(wecom.CorpId)}" +
               $"&agentid={Uri.EscapeDataString(wecom.AgentId)}" +
               $"&redirect_uri={Uri.EscapeDataString(callbackAbsoluteUrl)}" +
               $"&state={Uri.EscapeDataString(state)}";
    }

    /// <summary>
    /// 双提交校验：Cookie 与查询串必须是同一份受保护票据。攻击者拿不到受害者浏览器里的
    /// Cookie 值，就无法构造通过校验的回调链接（登录 CSRF 防线，与 OAuth handler 的
    /// correlation 机制同构）。校验通过则解出当初塞进去的 returnUrl。
    /// </summary>
    public bool TryValidateState(string? cookieState, string? queryState, out string returnUrl)
    {
        returnUrl = "/";

        if (string.IsNullOrEmpty(cookieState) || string.IsNullOrEmpty(queryState) ||
            !string.Equals(cookieState, queryState, StringComparison.Ordinal))
        {
            return false;
        }

        string payload;
        try
        {
            payload = _protector.Unprotect(queryState);
        }
        catch (CryptographicException)
        {
            // 篡改与过期都落在同一个异常类型上，对外统一按「校验失败」处理
            return false;
        }

        var separator = payload.IndexOf('|', StringComparison.Ordinal);
        if (separator < 0)
        {
            return false;
        }

        returnUrl = payload[(separator + 1)..];
        return true;
    }

    /// <summary>
    /// 用 auth_code 换成员身份并组装声明。失败返回带用户可读信息的 Result，
    /// 不抛异常 —— 回调端点把失败信息带回登录页，而不是给用户看 500。
    /// </summary>
    public async Task<Result<ClaimsPrincipal>> CreatePrincipalAsync(string authCode, CancellationToken cancellationToken)
    {
        var tokenResult = await GetAccessTokenAsync(cancellationToken);
        if (tokenResult.IsFailure)
        {
            return Result.Failure<ClaimsPrincipal>(tokenResult.Error);
        }

        var accessToken = tokenResult.Value;

        // ① auth_code → userid + user_ticket（文档「获取访问用户身份」）
        var userInfoResult = await PostAsync(UserInfoEndpoint, accessToken,
            new { auth_code = authCode }, cancellationToken);
        if (userInfoResult.IsFailure)
        {
            return Result.Failure<ClaimsPrincipal>(userInfoResult.Error);
        }

        using var userInfo = userInfoResult.Value;
        var userInfoRoot = userInfo.RootElement;
        var userId = GetStringOrNull(userInfoRoot, "userid");
        if (string.IsNullOrWhiteSpace(userId))
        {
            // 返回里没有 userid 而只有 openid：扫码的是企业外部联系人，不属于本企业通讯录
            return Result.Failure<ClaimsPrincipal>(Error.Unauthorized(
                "该账号不是企业成员，无法使用企业微信扫码登录。"));
        }

        var userTicket = GetStringOrNull(userInfoRoot, "user_ticket");
        if (string.IsNullOrWhiteSpace(userTicket))
        {
            return Result.Failure<ClaimsPrincipal>(Error.Unauthorized(
                "企业微信未返回用户票据，无法获取成员信息。"));
        }

        // ② user_ticket → 姓名/邮箱（文档「获取访问用户敏感信息」）
        var detailResult = await PostAsync(UserDetailEndpoint, accessToken,
            new { user_ticket = userTicket }, cancellationToken);
        if (detailResult.IsFailure)
        {
            return Result.Failure<ClaimsPrincipal>(detailResult.Error);
        }

        using var detail = detailResult.Value;
        var detailRoot = detail.RootElement;
        // email 是管理员在通讯录里填写的成员邮箱；biz_mail 是企业邮箱（企业开通企业邮箱后才有）。
        // 两者都由企业侧管理，取得到哪一个用哪一个。
        var email = GetStringOrNull(detailRoot, "email") ?? GetStringOrNull(detailRoot, "biz_mail");
        var name = GetStringOrNull(detailRoot, "name");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(name) ? userId : name)
        };

        if (string.IsNullOrWhiteSpace(email))
        {
            claims.Add(new Claim("email_verified", "false"));
        }
        else
        {
            claims.Add(new Claim(ClaimTypes.Email, email));
            claims.Add(new Claim("email_verified", "true"));
        }

        return Result.Success(new ClaimsPrincipal(new ClaimsIdentity(claims, ProviderName)));
    }

    /// <summary>
    /// 取企业级 access_token：命中缓存直接返回；未命中时双检锁内换新并缓存
    /// （有效期减 5 分钟边距）。换取失败返回 Result 而不是抛异常，理由同 CreatePrincipalAsync。
    /// </summary>
    private async Task<Result<string>> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_cachedAccessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiresAt)
        {
            return Result.Success(_cachedAccessToken);
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedAccessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiresAt)
            {
                return Result.Success(_cachedAccessToken);
            }

            var wecom = _options.WeCom;
            var url = $"{TokenEndpoint}?corpid={Uri.EscapeDataString(wecom.CorpId)}" +
                      $"&corpsecret={Uri.EscapeDataString(wecom.Secret)}";

            JsonDocument payload;
            try
            {
                payload = await GetJsonAsync(url, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // 网络故障 / 超时 / 响应当不成 JSON：登录请求失败是常态事件，不能炸出 500
                _logger.LogWarning(ex, "企业微信 gettoken 请求失败");
                return Result.Failure<string>(Error.Unauthorized("暂时无法连接企业微信，请稍后重试。"));
            }

            using (payload)
            {
                var root = payload.RootElement;
                var errcode = GetInt32OrDefault(root, "errcode");
                if (errcode != 0)
                {
                    _logger.LogWarning("企业微信 gettoken 失败：errcode={ErrCode}, errmsg={ErrMsg}",
                        errcode, GetStringOrNull(root, "errmsg"));
                    return Result.Failure<string>(Error.Unauthorized(
                        "企业微信凭据校验失败，请检查 CorpId / Secret 配置。"));
                }

                var token = GetStringOrNull(root, "access_token");
                if (string.IsNullOrWhiteSpace(token))
                {
                    return Result.Failure<string>(Error.Unauthorized("企业微信未返回访问令牌。"));
                }

                var expiresIn = GetInt32OrDefault(root, "expires_in", 7200);
                _cachedAccessToken = token;
                _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn) - TokenExpiryMargin;
                return Result.Success(token);
            }
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>POST JSON 到企业微信接口并检查 errcode；失败写日志并返回统一的用户可读错误。</summary>
    private async Task<Result<JsonDocument>> PostAsync(
        string endpoint,
        string accessToken,
        object body,
        CancellationToken cancellationToken)
    {
        var url = $"{endpoint}?access_token={Uri.EscapeDataString(accessToken)}";
        using var response = await _httpClientFactory.CreateClient(HttpClientName)
            .PostAsJsonAsync(url, body, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("企业微信接口 {Endpoint} 返回 HTTP {StatusCode}", endpoint, (int)response.StatusCode);
            return Result.Failure<JsonDocument>(Error.Unauthorized("企业微信接口调用失败，请稍后重试。"));
        }

        var payload = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var errcode = GetInt32OrDefault(payload.RootElement, "errcode");
        if (errcode != 0)
        {
            _logger.LogWarning("企业微信接口 {Endpoint} 失败：errcode={ErrCode}, errmsg={ErrMsg}",
                endpoint, errcode, GetStringOrNull(payload.RootElement, "errmsg"));
            payload.Dispose();
            return Result.Failure<JsonDocument>(Error.Unauthorized("企业微信拒绝了登录请求，请重新扫码。"));
        }

        return Result.Success(payload);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClientFactory.CreateClient(HttpClientName).GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    /// <summary>单例由 DI 容器在应用关闭时释放（释放令牌锁）。</summary>
    public void Dispose() => _tokenLock.Dispose();

    private static string? GetStringOrNull(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetInt32OrDefault(JsonElement element, string property, int fallback = -1)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : fallback;
}
