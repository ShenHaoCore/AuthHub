using AuthHub.Infrastructure.Security;
using Microsoft.AspNetCore.HttpOverrides;

namespace AuthHub.Api.Extensions;

/// <summary>
/// 反向代理转发头的装配。
///
/// <para><b>要解决的问题</b>：<c>ForwardedHeadersOptions</c> 的信任范围默认只有 loopback。
/// 反代跑在另一个容器 / 另一台机器时，它发来的 <c>X-Forwarded-For</c> 与
/// <c>X-Forwarded-Proto</c> 会被 ASP.NET Core **静默丢弃** —— 结果是审计日志里所有来源 IP
/// 都记成网关地址，以及 <c>RequireHttps</c> 下 <c>Request.IsHttps</c> 恒为 false
/// 造成的重定向异常。这两种症状都不会直接把方向指向"代理信任范围"，因此这里刻意把
/// "只信任了什么"写进启动日志。</para>
///
/// <para><b>三种配置形态</b>（优先级从高到低）：
/// <list type="number">
///   <item><c>TrustAnyProxy=true</c> —— 显式声明"上游地址不固定"（容器编排里最常见），清空信任列表，等同信任任何直连来源；</item>
///   <item><c>KnownProxies</c> / <c>KnownNetworks</c> —— 显式列出网关地址或网段（推荐）；</item>
///   <item>都不配 —— 沿用 .NET 的 loopback 默认值，并**打一条警告**说明它意味着什么。</item>
/// </list></para>
/// </summary>
internal static class ForwardedHeadersExtensions
{
    public static void UseAuthHubForwardedHeaders(this WebApplication app)
    {
        if (!app.Configuration.GetValue("AuthHub:Security:TrustForwardedHeaders", false))
        {
            // 不部署在反代之后时不启用，避免允许客户端直接伪造 X-Forwarded-* 影响审计与限流分区键
            return;
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        };

        var trustAnyProxy = app.Configuration.GetValue("AuthHub:Security:TrustAnyProxy", false);
        var proxies = ProxyTrustParser.ParseAddresses(
            app.Configuration.GetSection("AuthHub:Security:KnownProxies").Get<string[]>());
        var networks = ProxyTrustParser.ParseNetworks(
            app.Configuration.GetSection("AuthHub:Security:KnownNetworks").Get<string[]>());

        if (trustAnyProxy)
        {
            // 清空默认的 loopback 列表 = 接受来自任何直连来源的转发头。
            // 此时来源 IP 的可信度完全由网络层（安全组 / 私有网络）保证，因此在日志里留下痕迹。
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();

            app.Logger.LogWarning(
                "反向代理信任范围被设为 TrustAnyProxy=true：将接受**任何**直连来源发来的 X-Forwarded-For / X-Forwarded-Proto。" +
                "请确认 AuthHub 只对可信网关暴露（安全组 / 私有网络隔离）—— 暴露到公网时，客户端可以自由伪造来源 IP，" +
                "审计日志与限流分区都会失真。若上游地址固定，改用 KnownProxies / KnownNetworks 更安全。");
        }
        else if (proxies.Count > 0 || networks.Count > 0)
        {
            // 配置了显式范围就整体替换默认值，而不是叠加：
            // 追加语义会让"我明明只信任 172.16/12"与"其实还信任着 loopback"并存，读配置时看不出全貌。
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();

            foreach (var proxy in proxies)
            {
                options.KnownProxies.Add(proxy);
            }

            foreach (var network in networks)
            {
                options.KnownNetworks.Add(network);
            }

            app.Logger.LogInformation(
                "反向代理转发头已启用 | 信任的代理地址=[{Proxies}] | 信任的网段=[{Networks}] | 仅这些来源的 X-Forwarded-* 会被采信",
                proxies.Count == 0 ? "（无）" : string.Join("、", proxies),
                networks.Count == 0 ? "（无）" : ProxyTrustParser.Describe(networks));
        }
        else
        {
            app.Logger.LogWarning(
                "已启用 AuthHub:Security:TrustForwardedHeaders，但没有配置 KnownProxies / KnownNetworks，" +
                "因此沿用 .NET 的默认信任范围：**仅 loopback**（127.0.0.1/8 与 ::1）。" +
                "反向代理若跑在另一个容器或另一台机器上，它发来的转发头会被静默忽略 —— " +
                "症状是审计日志里的来源 IP 全部记成网关地址、以及强制 HTTPS 下的重定向异常。" +
                "请按部署拓扑配置信任范围，或在上游地址确实不固定时用 AuthHub:Security:TrustAnyProxy=true 显式声明。");
        }

        app.UseForwardedHeaders(options);
    }
}
