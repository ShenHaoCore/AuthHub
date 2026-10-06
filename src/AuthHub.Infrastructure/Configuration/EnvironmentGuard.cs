using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AuthHub.Infrastructure.Configuration;

/// <summary>
/// 启动期的**环境一致性**护栏：检查"这份配置与它所在的环境"是否自洽。
///
/// <para><b>要解决什么</b>：环境串用的错误几乎都是静默的。开发配置被带进生产时，
/// 连接串会指向一台本机实例 —— 表现是"启动很慢然后超时"，看不出是连错了地方；
/// 反过来生产配置用在开发机上，则可能真的连上某台共享库。这类问题的共同点是
/// <b>没有一条报错指向真正的原因</b>，而它们又都是"看一眼配置就能确认"的事，
/// 所以值得在启动期一次性问清楚。</para>
///
/// <para><b>为什么判定逻辑是纯函数</b>：输入只有 <see cref="IConfiguration"/> 与环境名，
/// 输出是一组结论。这样整套规则表可以在没有数据库、没有主机的单元测试里逐条钉住 ——
/// 与 <c>ProxyTrustParser</c>、<c>AuthHubSeeder.Decide</c> 是同一个模式。</para>
///
/// <para><b>为什么只查"受保护环境"</b>：见 <see cref="IsProtectedEnvironment"/>。
/// 开发机连本机数据库天经地义，对这些环境做检查只会制造噪音，然后被人关掉。</para>
/// </summary>
public static class EnvironmentGuard
{
    /// <summary>护栏强度。默认 <see cref="GuardMode.Strict"/>。</summary>
    public const string GuardModeKey = "AuthHub:Security:GuardMode";

    /// <summary>
    /// 集成测试用的环境名。它与 <c>WebApplicationFactory</c> 里 <c>UseEnvironment("Testing")</c>
    /// 的值必须一致 —— 对不上会让护栏去检查测试宿主，而测试夹具的配置（SQLite 临时库、
    /// 明文演示口令）本来就是"不该拿去部署"的样子，检查它会全线误伤。
    /// </summary>
    public const string TestingEnvironmentName = "Testing";

    /// <summary>占位符哨兵：连接串里留着它就说明从来没被替换成真实值。</summary>
    private const string PlaceholderMarker = "CHANGE_ME";

    /// <summary>
    /// 受保护环境 = 需要认真对待部署配置的环境。
    ///
    /// 判据刻意是"**不是** Development、也**不是** Testing"，而不是"是 Production"：
    /// 这样自定义的环境名（Staging、PreProd）天然也被保护 —— 漏保护的代价是某个环境
    /// 悄悄跑着开发配置，而多保护的代价只是多一次启动失败。
    /// </summary>
    public static bool IsProtectedEnvironment(string environmentName)
        => !string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase)
           && !string.Equals(environmentName, TestingEnvironmentName, StringComparison.OrdinalIgnoreCase);

    /// <summary>读护栏强度。缺失或写错都按 <see cref="GuardMode.Strict"/> —— 出错方向必须是"查得更严"。</summary>
    public static GuardMode ReadGuardMode(IConfiguration configuration)
    {
        var raw = configuration[GuardModeKey];
        return Enum.TryParse<GuardMode>(raw, ignoreCase: true, out var mode) ? mode : GuardMode.Strict;
    }

    /// <summary>
    /// 评估这份配置与这个环境是否自洽。
    ///
    /// 不受保护的环境直接返回空集；<see cref="GuardMode.Off"/> 同理。
    /// <see cref="GuardMode.Warn"/> 把全部结论降级为警告（"我知道我在做什么，但请记下来"）。
    /// </summary>
    public static IReadOnlyList<EnvironmentGuardFinding> Evaluate(
        IConfiguration configuration,
        string environmentName)
    {
        if (!IsProtectedEnvironment(environmentName))
        {
            return [];
        }

        var mode = ReadGuardMode(configuration);
        if (mode == GuardMode.Off)
        {
            return [];
        }

        var findings = new List<EnvironmentGuardFinding>();

        InspectConnectionString(configuration, findings);
        InspectIssuer(configuration, findings);
        InspectApiDocs(configuration, findings);
        InspectTransport(configuration, findings);
        InspectPasswordFlow(configuration, findings);

        return mode == GuardMode.Warn
            ? findings.Select(finding => finding with { Severity = GuardSeverity.Warning }).ToList()
            : findings;
    }

    /// <summary>
    /// 连接串：这是最容易"看起来对、连的是别处"的一项。
    /// </summary>
    private static void InspectConnectionString(IConfiguration configuration, List<EnvironmentGuardFinding> findings)
    {
        const string key = "ConnectionStrings:DefaultConnection";

        var provider = configuration["Database:Provider"];
        if (string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new EnvironmentGuardFinding(
                "Database:Provider",
                GuardSeverity.Warning,
                "受保护环境使用 SQLite。它只适合开发与集成测试：没有并发写入能力，" +
                "且启动路径走的是 EnsureCreated 而不是迁移，结构变更无法增量演进。"));
            return;
        }

        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // 连接串缺失由 PersistenceExtensions 在更早的阶段抛出（那时连 DbContext 都注册不了），
            // 这里不重复报 —— 两条消息描述同一件事只会让人怀疑有第二个问题。
            return;
        }

        if (connectionString.Contains(PlaceholderMarker, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new EnvironmentGuardFinding(
                key,
                GuardSeverity.Failure,
                $"连接串里还留着 {PlaceholderMarker} 占位符 —— 说明它从未被真实凭据替换过。" +
                "请用环境变量 ConnectionStrings__DefaultConnection 注入，或指向密钥库。"));
            return;
        }

        var server = ExtractServer(connectionString);
        if (server is null)
        {
            return;
        }

        if (IsLocalOnlyServer(server))
        {
            findings.Add(new EnvironmentGuardFinding(
                key,
                GuardSeverity.Failure,
                $"连接串指向本机专用写法（Server={server}）。这类写法在部署环境里必然连不上：" +
                "(local) 走共享内存、(localdb) 是 LocalDB 实例，两者都只在本机进程内可达。" +
                "这通常意味着开发配置被带到了服务器上 —— " +
                "请用环境变量 ConnectionStrings__DefaultConnection 覆盖它。"));
            return;
        }

        if (IsLoopbackServer(server))
        {
            findings.Add(new EnvironmentGuardFinding(
                key,
                GuardSeverity.Warning,
                $"连接串指向 loopback（Server={server}）。单机部署时这是合理的，" +
                "但如果数据库在另一台机器或另一个容器上，说明地址还没改过来。"));
        }
    }

    /// <summary>
    /// Issuer 是令牌里 <c>iss</c> 的取值，也是发现文档对外公布的地址。
    /// 错的下游表现是"验签失败 / 发现文档与令牌对不上"，排查方向很难指回配置。
    /// </summary>
    private static void InspectIssuer(IConfiguration configuration, List<EnvironmentGuardFinding> findings)
    {
        const string key = "AuthHub:Issuer";
        var issuer = configuration[key];

        if (string.IsNullOrWhiteSpace(issuer))
        {
            findings.Add(new EnvironmentGuardFinding(
                key, GuardSeverity.Failure,
                "没有配置 Issuer。它决定令牌里的 iss 与发现文档中的地址，必须显式指向本服务对外可达的地址。"));
            return;
        }

        // example.com 是 RFC 2606 的保留域名，永远不会是真实的生产地址 ——
        // 出现在这里只可能是模板没改。
        if (issuer.Contains("example.com", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new EnvironmentGuardFinding(
                key, GuardSeverity.Failure,
                $"Issuer 仍是模板占位符（{issuer}）。请改成这个部署实际对外的地址，" +
                "并注意结尾要带 / —— 否则令牌的 iss 与下游配置的 authority 会对不上。"));
        }
    }

    /// <summary>
    /// 接口文档是一份"有哪些接口、收什么参数"的清单，不该对外可见。
    /// </summary>
    private static void InspectApiDocs(IConfiguration configuration, List<EnvironmentGuardFinding> findings)
    {
        const string key = "AuthHub:Features:EnableApiDocs";
        if (configuration.GetValue(key, false))
        {
            findings.Add(new EnvironmentGuardFinding(
                key, GuardSeverity.Failure,
                "受保护环境打开了接口文档（EnableApiDocs）。它会暴露完整的端点与参数清单；" +
                "需要临时查看时请在本机连生产库排查，不要把它对部署环境常开。" +
                "确实有内网文档站等正当需求时，把 AuthHub:Security:GuardMode 设为 Warn 表明这是有意为之。"));
        }
    }

    /// <summary>
    /// 传输层：RequireHttps 关掉之后，会话 Cookie 不再强制 Secure，也没有 HSTS。
    /// </summary>
    private static void InspectTransport(IConfiguration configuration, List<EnvironmentGuardFinding> findings)
    {
        const string key = "AuthHub:Security:RequireHttps";
        if (configuration.GetValue(key, true))
        {
            return;
        }

        findings.Add(new EnvironmentGuardFinding(
            key, GuardSeverity.Warning,
            "受保护环境关闭了 RequireHttps：HSTS 与跳转都不会生效，会话 Cookie 也不再强制 Secure。" +
            "在 TLS 于网关处卸载、由网关保证对外 HTTPS 的部署里这是合理的；" +
            "否则请打开它。"));
    }

    /// <summary>
    /// 资源所有者密码流程（ROPC）会让本服务经手用户的明文口令，且无法支持 MFA。
    /// </summary>
    private static void InspectPasswordFlow(IConfiguration configuration, List<EnvironmentGuardFinding> findings)
    {
        const string key = "AuthHub:Features:EnablePasswordFlow";
        if (configuration.GetValue(key, false))
        {
            findings.Add(new EnvironmentGuardFinding(
                key, GuardSeverity.Warning,
                "受保护环境打开了密码流程（ROPC）：用户口令会经手本服务，且这条路径无法接入 MFA。" +
                "如果只是为了兼容某个老客户端，建议改用授权码 + PKCE。"));
        }
    }

    /// <summary>
    /// 从连接串里取出 Server / Data Source 的值。
    ///
    /// 刻意不用 <c>SqlConnectionStringBuilder</c>：那会把判定绑死在 SQL Server 上，
    /// 而这里还要辨认 SQLite 与未来的其它提供程序（Provider 已经在调用方判过了），
    /// 一次解析失败就会静默漏报 —— 漏报正是护栏最不该有的失败方式。
    /// </summary>
    private static string? ExtractServer(string connectionString)
    {
        foreach (var segment in connectionString.Split(';'))
        {
            var part = segment.Trim();
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = part[..separator].Trim();
            if (!IsServerKey(key))
            {
                continue;
            }

            var value = part[(separator + 1)..].Trim().Trim('"', '\'');
            return value.Length == 0 ? null : value;
        }

        return null;
    }

    private static bool IsServerKey(string key)
        => key.Equals("Server", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Data Source", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Addr", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Address", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Network Address", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 本机**专用**写法：这些 Server 值离开本机进程就不可达，在任何部署环境里都必然是配置没改。
    /// </summary>
    private static bool IsLocalOnlyServer(string server)
        => server.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase)
           || server.StartsWith("(local)", StringComparison.OrdinalIgnoreCase)
           || server.Equals(".", StringComparison.Ordinal)
           || server.StartsWith(@".\", StringComparison.Ordinal)
           || server.StartsWith("./", StringComparison.Ordinal);

    /// <summary>
    /// loopback：单机部署时正当，因此只是警告。剥掉实例名、端口与 <c>tcp:</c> 前缀再判断。
    /// </summary>
    private static bool IsLoopbackServer(string server)
    {
        var host = server;

        if (host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        var cut = host.IndexOfAny([',', '\\', '/']);
        if (cut > 0)
        {
            host = host[..cut];
        }

        host = host.Trim();

        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }

        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }
}

/// <summary>护栏强度。</summary>
public enum GuardMode
{
    /// <summary>默认。Fail 项让进程起不来，Warn 项写进启动日志。</summary>
    Strict,

    /// <summary>全部只写日志。用于"这个看起来可疑的取值是有意为之"的部署。</summary>
    Warn,

    /// <summary>完全不检查。用于排查"是不是护栏挡住了我"这类问题。</summary>
    Off
}

/// <summary>结论的严重度。</summary>
public enum GuardSeverity
{
    /// <summary>写进启动日志。</summary>
    Warning,

    /// <summary>阻止启动（<see cref="GuardMode.Strict"/> 下）。</summary>
    Failure
}

/// <summary>一条护栏结论。<paramref name="Key"/> 是配置项路径，方便直接照着改。</summary>
/// <param name="Key">出问题的配置项。</param>
/// <param name="Severity">严重度。</param>
/// <param name="Message">给运维看的说明：问题是什么、为什么、怎么改。</param>
public sealed record EnvironmentGuardFinding(string Key, GuardSeverity Severity, string Message);
