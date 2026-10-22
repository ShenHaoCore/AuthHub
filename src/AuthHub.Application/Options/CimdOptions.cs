namespace AuthHub.Application.Options;

/// <summary>
/// CIMD（Client ID Metadata Document）动态客户端注册的配置。
///
/// <para><b>是什么</b>：<c>client_id</c> 本身就是一个 HTTPS URL，授权服务器去该地址
/// 拉取客户端元数据（名称、回调地址、授权类型……），因此调用方无需事先在后台登记。
/// MCP 2026-07-28 把它列为客户端注册的**首选**方式。</para>
///
/// <para><b>为什么默认关闭</b>：打开它等于让本服务**主动向外部 URL 发起请求**，
/// 这是本仓库唯一一处"由匿名请求驱动出站流量"的入口（SSRF 面）。默认关闭意味着
/// 升级不会悄悄改变攻击面；要用的部署显式打开，并在打开时读到这里的每一项限制。</para>
///
/// <para><b>为什么落库而不是纯无状态</b>：规范描述的是无状态形态（每次按 URL 取元数据），
/// 但 OpenIddict 的整条校验链（回调地址匹配、scope 权限、PKCE 要求）都读库里的应用记录。
/// 纯无状态意味着要替换 <c>IOpenIddictApplicationManager</c> 或绕过全部内置校验 ——
/// 那是把安全边界交给自研代码。这里选择**懒注册**：首次见到某个 URL 时拉取并落库，
/// 之后按 TTL 刷新。既保住了内置校验，又不会让"客户端记录"随请求无限增长
/// （同一个 URL 只有一条记录）。</para>
/// </summary>
public sealed class CimdOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "AuthHub:Cimd";

    /// <summary>总开关。关闭时 <c>/connect/authorize</c> 完全不认 URL 型 client_id。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 元数据缓存有效期（小时）。到期后下一次授权请求会重新拉取。
    /// 拉取失败时**降级**使用过期缓存 —— 第三方短暂不可用不应该让用户登不进来。
    /// </summary>
    public int RefreshIntervalHours { get; set; } = 24;

    /// <summary>单次拉取（含重定向）的超时秒数。</summary>
    public int FetchTimeoutSeconds { get; set; } = 5;

    /// <summary>允许跟随的重定向跳数。0 表示不跟随。</summary>
    public int MaxRedirects { get; set; } = 3;

    /// <summary>元数据响应体上限（字节）。超出直接判失败，防止用超大响应拖垮内存。</summary>
    public int MaxMetadataBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// 自动注册的客户端可以申请的 scope 白名单。
    ///
    /// <para>关键取舍：**不能**原样相信元数据里声明的 scope。否则任何人在自己的域名上放一份
    /// 元数据、写上 <c>scope: "admin"</c>，就能让用户在同意页看到一个自称需要管理权限的应用 ——
    /// 而同意页的措辞会替攻击者背书。这里用交集：元数据声明了就用交集，没声明才用白名单全集。</para>
    ///
    /// <para>默认不含 <c>api:read</c> / <c>api:write</c> / <c>admin</c>：这些是 AuthHub 自身 API 的
    /// 授权范围，不该由一台外部的 MCP 服务器替用户决定。</para>
    /// </summary>
    public List<string> AllowedScopes { get; set; } =
        ["openid", "profile", "email", "roles"];

    /// <summary>
    /// 允许自动注册的域名白名单（精确匹配 Host，不区分大小写）。为空表示不限制（仅受 SSRF 防护约束）。
    ///
    /// <para>过渡期把 SSRF 面收窄到"已知的 MCP 客户端厂商"的手段：即使某处实现有疏漏，
    /// 攻击者也只能指向白名单内的域名，无法把本服务当成内网探测器。</para>
    /// </summary>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>
    /// 授予自动注册客户端的资源标识（RFC 8707 的 <c>rsrc:</c> 权限）。为空表示沿用
    /// <c>AuthHub:Resources</c> 的全集 —— 资源本来就已经是服务端登记的白名单，
    /// 客户端请求其中任何一项都合理（真正的受众收窄由令牌的 aud 完成）。
    ///
    /// <para>不配这一项的话，自动注册的客户端一带 <c>resource</c> 就会被
    /// 「该客户端不允许使用指定资源」拒掉 —— 也就是 #5 修好的能力在这里又断一次。</para>
    /// </summary>
    public List<string> AllowedResources { get; set; } = [];

    /// <summary>
    /// 是否强制用户确认（同意页）。默认 **true**。
    ///
    /// <para>自动注册的客户端没有经过任何人审核，让用户看见"哪个应用、要哪些权限"是最后一道
    /// 人为判断。把它关掉等于任何域名都能静默拿到用户令牌。</para>
    /// </summary>
    public bool RequireConsent { get; set; } = true;

    /// <summary>规范化为可用的缓存有效期。</summary>
    public TimeSpan RefreshInterval => TimeSpan.FromHours(Math.Clamp(RefreshIntervalHours, 1, 24 * 30));

    /// <summary>规范化为可用的拉取超时。</summary>
    public TimeSpan FetchTimeout => TimeSpan.FromSeconds(Math.Clamp(FetchTimeoutSeconds, 1, 30));

    /// <summary>规范化为可用的重定向跳数。</summary>
    public int EffectiveMaxRedirects => Math.Clamp(MaxRedirects, 0, 5);

    /// <summary>规范化为可用的响应体上限。</summary>
    public long EffectiveMaxMetadataBytes => Math.Clamp(MaxMetadataBytes, 1024, 1024 * 1024);
}
