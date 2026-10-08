using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Identity;
using AspNet.Security.OAuth.GitHub;

// 本文件内的 ExternalLoginOptions 是 Api 层的提供商凭据载体（GitHub/Google）；
// Application 层另有同名类承载应用层策略（如邮箱白名单），两者绑定同一配置节、各取所需字段，用别名消歧。
using ApplicationExternalLoginOptions = AuthHub.Application.Options.ExternalLoginOptions;

namespace AuthHub.Api.Extensions;

/// <summary>
/// 第三方登录（GitHub / Google）的认证 handler 注册。
///
/// <para>
/// 只注册 <see cref="ExternalLoginOptions"/> 里 Enabled=true 的提供商：登录页按钮按同一份
/// 配置点亮（未启用的渲染为禁用态预告），challenge 路由按白名单拦截 —— 配置即开关。
/// 启用却没给凭据在启动期直接抛异常（fail fast），而不是等到第一次点击才失败。
/// </para>
///
/// <para>
/// <b>邮箱信任是这里的重点</b>：回调后「外部邮箱 ↔ 本地账号」的匹配以邮箱为锚点，
/// 只信 <c>email_verified=true</c> 的邮箱（否则攻击者拿未验证邮箱即可接管同名本地账号）。
/// 两个处理器都不直接产出该声明 —— Google 的 ClaimActions 没映射 <c>email_verified</c>，
/// GitHub 选主邮箱时也不校验 verified 标志 —— 所以各自挂 <c>OnCreatingTicket</c>
/// 事件统一补写，回调侧的判定逻辑（见 AccountService）对两家一视同仁。
/// </para>
/// </summary>
internal static class ExternalAuthenticationExtensions
{
    public static IServiceCollection AddAuthHubExternalLogin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(ExternalLoginOptions.SectionName);
        services.Configure<ExternalLoginOptions>(section);
        // 应用层策略（邮箱白名单）绑定同一节：AccountService 只依赖 Application 层的选项类，
        // 不知道也不关心提供商凭据的存在。
        services.Configure<ApplicationExternalLoginOptions>(section);

        var options = section.Get<ExternalLoginOptions>() ?? new ExternalLoginOptions();

        if (options.GitHub.Enabled)
        {
            EnsureCredentials("GitHub", options.GitHub);
            services.AddAuthentication().AddGitHub(o =>
            {
                o.ClientId = options.GitHub.ClientId;
                o.ClientSecret = options.GitHub.ClientSecret;
                // 回调得到的身份暂存进 Identity 的外部 Cookie（短时效），供 /account/external-callback
                // 读取并决定「直接登录 / 绑定确认」。不落会话 Cookie —— 到建立会话为止都还是匿名。
                o.SignInScheme = IdentityConstants.ExternalScheme;
                o.CallbackPath = "/signin-github";
                // read:user 取基本资料；user:email 是解析「已验证主邮箱」的前提（公共邮箱字段不可信）
                o.Scope.Add("read:user");
                o.Scope.Add("user:email");
                // 关闭处理器自带的邮箱抓取：它只挑 primary、不校验 verified（见 GitHubAuthenticationHandler），
                // 改由下方事件统一解析，保证产出邮箱一定带 verified 标志。
                o.UserEmailsEndpoint = string.Empty;
                // 事件用的地址不取 o.UserEmailsEndpoint（已被置空），直接用官方默认端点常量
                o.Events.OnCreatingTicket = context =>
                    ResolveVerifiedEmailAsync(context, GitHubAuthenticationDefaults.UserEmailsEndpoint);
            });
        }

        if (options.Google.Enabled)
        {
            EnsureCredentials("Google", options.Google);
            services.AddAuthentication().AddGoogle(o =>
            {
                o.ClientId = options.Google.ClientId;
                o.ClientSecret = options.Google.ClientSecret;
                o.SignInScheme = IdentityConstants.ExternalScheme;
                o.CallbackPath = "/signin-google";
                // openid/profile/email 是默认 scope，无需重复添加；userinfo 响应里有 email_verified 字段
                o.Events.OnCreatingTicket = context =>
                {
                    // GoogleOptions 的 ClaimActions 只映射了 email，没映射 email_verified；
                    // userinfo 响应里的 email_verified 字段就是判定依据（标准 OIDC 声明）。
                    if (context.Identity is { } identity &&
                        context.User.ValueKind == JsonValueKind.Object &&
                        context.User.TryGetProperty("email_verified", out var verified))
                    {
                        identity.AddClaim(new Claim(
                            "email_verified",
                            verified.GetBoolean() ? "true" : "false",
                            ClaimValueTypes.String,
                            o.ClaimsIssuer));
                    }

                    return Task.CompletedTask;
                };
            });
        }

        if (options.WeCom.Enabled)
        {
            EnsureWeComCredentials(options.WeCom);
        }

        // 企业微信没有可用的标准认证 handler（回调参数是 auth_code 而非 OAuth 的 code，
        // aspnet-contrib 也无 provider），协议适配由 WeComLoginService 自实现。
        // 单例注册：内部缓存 access_token（7200 秒有效且有调用频率限制，不能每次登录都重取）。
        // 无论是否启用都注册 —— 未启用时控制器白名单会挡住请求，服务永远不会被调用。
        services.AddHttpClient(WeComLoginService.HttpClientName);
        services.AddSingleton<WeComLoginService>();

        return services;
    }

    /// <summary>
    /// GitHub 的已验证主邮箱解析：调 <paramref name="emailsEndpoint"/>（<c>/user/emails</c>），
    /// 取 primary 且 verified 的那枚写入 email 声明并补 <c>email_verified=true</c>；
    /// 没有匹配（或接口失败）时清掉处理器从公开资料映射来的 email 声明、写
    /// <c>email_verified=false</c>，让回调侧明确拒绝而不是猜测。
    /// </summary>
    private static async Task ResolveVerifiedEmailAsync(
        OAuthCreatingTicketContext context,
        string emailsEndpoint)
    {
        if (context.Identity is not { } identity)
        {
            return;
        }

        // 处理器从 /user 的公开邮箱字段映射来的 email 不可信（未必是已验证主邮箱），先移除
        var unverified = identity.FindFirst(ClaimTypes.Email);
        if (unverified is not null)
        {
            identity.RemoveClaim(unverified);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, emailsEndpoint);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.AccessToken);
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
            response.EnsureSuccessStatusCode();

            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));

            var match = payload.RootElement.EnumerateArray().FirstOrDefault(entry =>
                entry.TryGetProperty("primary", out var primary) && primary.GetBoolean() &&
                entry.TryGetProperty("verified", out var verified) && verified.GetBoolean() &&
                entry.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String);

            if (match.ValueKind == JsonValueKind.Object)
            {
                identity.AddClaim(new Claim(ClaimTypes.Email, match.GetProperty("email").GetString()!, ClaimValueTypes.String));
                identity.AddClaim(new Claim("email_verified", "true", ClaimValueTypes.String));
            }
            else
            {
                identity.AddClaim(new Claim("email_verified", "false", ClaimValueTypes.String));
            }
        }
        catch (Exception)
        {
            // 拿不到已验证邮箱就按「不可信」处理：回调侧会拒绝匹配并提示用户。
            // 不能让这里的异常炸掉整个外部登录流程 —— 提示页比 500 更可解释。
            identity.AddClaim(new Claim("email_verified", "false", ClaimValueTypes.String));
        }
    }

    /// <summary>启用却不给凭据是配置错误：启动期直接失败，而不是第一次点击第三方按钮才炸。</summary>
    private static void EnsureCredentials(string provider, ExternalProviderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            throw new InvalidOperationException(
                $"外部登录 {provider} 已启用（Enabled=true），但 ClientId / ClientSecret 未配置。" +
                "请通过环境变量或密钥库注入（如 AuthHub__Authentication__GitHub__ClientId），" +
                "或把 Enabled 改回 false。");
        }
    }

    /// <summary>企业微信的凭据口径与 OAuth 两家不同（CorpId/AgentId/Secret），单独校验。</summary>
    private static void EnsureWeComCredentials(WeComProviderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.CorpId) ||
            string.IsNullOrWhiteSpace(options.AgentId) ||
            string.IsNullOrWhiteSpace(options.Secret))
        {
            throw new InvalidOperationException(
                "外部登录 WeCom 已启用（Enabled=true），但 CorpId / AgentId / Secret 未配置。" +
                "请通过环境变量或密钥库注入（如 AuthHub__Authentication__WeCom__Secret），" +
                "或把 Enabled 改回 false。");
        }
    }
}

/// <summary>第三方登录配置（AuthHub:Authentication 节）。Enabled=false 的提供商不注册 handler，登录页按钮呈禁用态。</summary>
public sealed class ExternalLoginOptions
{
    public const string SectionName = "AuthHub:Authentication";

    public ExternalProviderOptions GitHub { get; set; } = new();

    public ExternalProviderOptions Google { get; set; } = new();

    public WeComProviderOptions WeCom { get; set; } = new();

    /// <summary>已启用的提供商（认证 scheme 名，顺序即登录页按钮顺序）。</summary>
    public IReadOnlyList<string> EnabledProviders
        => AllProviders.Where(entry => entry.Enabled).Select(entry => entry.Name).ToArray();

    /// <summary>
    /// 全部已知提供商及其启用状态（顺序即登录页按钮顺序）。登录页渲染完整入口：
    /// 未启用的按钮呈禁用态（仅作预告，点击不可提交），启用与否的拦截仍在服务端白名单。
    /// </summary>
    public IReadOnlyList<(string Name, bool Enabled)> AllProviders
        => new (string Name, bool Enabled)[]
        {
            ("GitHub", GitHub.Enabled),
            ("Google", Google.Enabled),
            ("WeCom", WeCom.Enabled)
        };
}

/// <summary>单个 OAuth 提供商的凭据配置（GitHub / Google）。</summary>
public sealed class ExternalProviderOptions
{
    public bool Enabled { get; set; }

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;
}

/// <summary>
/// 企业微信扫码登录（自建应用）的凭据配置。凭据口径与 OAuth 不同：CorpId 是企业 ID，
/// AgentId/Secret 来自管理后台的自建应用。注意回调域名必须在企业微信后台配置为可信域名
/// （需通过所有权验证），因此 localhost 无法联调，只能在有公网域名的环境启用。
/// </summary>
public sealed class WeComProviderOptions
{
    public bool Enabled { get; set; }

    public string CorpId { get; set; } = string.Empty;

    public string AgentId { get; set; } = string.Empty;

    public string Secret { get; set; } = string.Empty;
}
