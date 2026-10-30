namespace AuthHub.Application.Options;

/// <summary>CIMD（Client ID Metadata Document）动态客户端注册的配置：<c>client_id</c> 本身就是一个 HTTPS URL，授权服务器到该地址拉取客户端元数据，调用方无需事先登记。</summary>
/// <remarks>
/// - 默认关闭是刻意的：打开它等于让本服务主动向外部 URL 发起请求，是本仓库唯一一处"由匿名请求驱动出站流量"的入口（SSRF 面）；默认关闭保证升级不会悄悄改变攻击面。
/// - 落库而非纯无状态（懒注册：首次见到某个 URL 时拉取并落库，之后按 TTL 刷新）：OpenIddict 的整条校验链（回调地址匹配、scope 权限、PKCE 要求）都读库里的应用记录，纯无状态得替换 <c>IOpenIddictApplicationManager</c> 或绕过全部内置校验，那是把安全边界交给自研代码。
/// </remarks>
public sealed class CimdOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "AuthHub:Cimd";

    /// <summary>总开关。关闭时 <c>/connect/authorize</c> 完全不认 URL 型 client_id。</summary>
    public bool Enabled { get; set; }

    /// <summary>元数据缓存有效期（小时）。到期后下一次授权请求重新拉取；拉取失败时降级使用过期缓存，第三方短暂不可用不该让用户登不进来。</summary>
    public int RefreshIntervalHours { get; set; } = 24;

    /// <summary>单次拉取（含重定向）的超时秒数。</summary>
    public int FetchTimeoutSeconds { get; set; } = 5;

    /// <summary>允许跟随的重定向跳数。0 表示不跟随。</summary>
    public int MaxRedirects { get; set; } = 3;

    /// <summary>元数据响应体上限（字节）。超出直接判失败，防止用超大响应拖垮内存。</summary>
    public int MaxMetadataBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// 自动注册的客户端可以申请的 scope 白名单。**不能**原样相信元数据里声明的 scope —— 否则同意页的措辞会替一个自称需要管理权限的应用背书；
    /// 声明了就用交集，没声明才用白名单全集。默认不含 <c>api:read</c> / <c>api:write</c> / <c>admin</c>（AuthHub 自身 API 的授权范围，不由外部服务器替用户决定）。
    /// </summary>
    public List<string> AllowedScopes { get; set; } =
        ["openid", "profile", "email", "roles"];

    /// <summary>
    /// 允许自动注册的域名白名单（精确匹配 Host，不区分大小写）。为空表示不限制（仅受 SSRF 防护约束）。
    /// 过渡期收窄 SSRF 面的手段：即使某处实现有疏漏，攻击者也只能指向白名单内的域名，无法把本服务当成内网探测器。
    /// </summary>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>
    /// 授予自动注册客户端的资源标识（RFC 8707 的 <c>rsrc:</c> 权限）。为空表示沿用 <c>AuthHub:Resources</c> 的全集 ——
    /// 资源本来就是服务端登记的白名单，真正的受众收窄由令牌的 aud 完成；不配这一项的话，自动注册客户端一带 <c>resource</c> 就会被「该客户端不允许使用指定资源」拒掉。
    /// </summary>
    public List<string> AllowedResources { get; set; } = [];

    /// <summary>是否强制用户确认（同意页）。默认 <c>true</c>：自动注册的客户端没有经过任何人审核，同意页是最后一道人为判断，关掉等于任何域名都能静默拿到用户令牌。</summary>
    public bool RequireConsent { get; set; } = true;

    public TimeSpan RefreshInterval => TimeSpan.FromHours(Math.Clamp(RefreshIntervalHours, 1, 24 * 30));

    public TimeSpan FetchTimeout => TimeSpan.FromSeconds(Math.Clamp(FetchTimeoutSeconds, 1, 30));

    public int EffectiveMaxRedirects => Math.Clamp(MaxRedirects, 0, 5);

    public long EffectiveMaxMetadataBytes => Math.Clamp(MaxMetadataBytes, 1024, 1024 * 1024);
}
