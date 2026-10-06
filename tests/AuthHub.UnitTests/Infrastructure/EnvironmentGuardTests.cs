using AuthHub.Infrastructure.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AuthHub.UnitTests.Infrastructure;

/// <summary>
/// 启动期环境护栏。
///
/// 这套规则的价值全在"**边界**"上：判严了会把正当的部署形态挡在门外（然后被人直接关掉护栏），
/// 判松了等于没做。所以用例刻意成对出现 —— 同一项配置的"该拦"与"不该拦"各有断言，
/// 并逐条钉住 Fail 与 Warn 的分界：Fail 只留给"在部署环境里必然不成立"的取值。
/// </summary>
public class EnvironmentGuardTests
{
    /// <summary>
    /// 一份"完全自洽的部署配置"作为基线，各用例只覆盖自己要测的那一项。
    /// 不这么做的话，每个用例都要把全部无关项都写对才能断言，改一项就会牵动一片。
    /// </summary>
    private static IConfiguration Config(params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Database:Provider"] = "SqlServer",
            ["ConnectionStrings:DefaultConnection"] =
                "Server=db.internal,1433;Database=AuthHub;User Id=authhub;Password=correct-horse;Encrypt=True;",
            ["AuthHub:Issuer"] = "https://authhub.internal/",
            ["AuthHub:Features:EnableApiDocs"] = "false",
            ["AuthHub:Features:EnablePasswordFlow"] = "false",
            ["AuthHub:Security:RequireHttps"] = "true"
        };

        foreach (var (key, value) in overrides)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static EnvironmentGuardFinding? Find(IReadOnlyList<EnvironmentGuardFinding> findings, string key)
        => findings.FirstOrDefault(finding => finding.Key == key);

    // ---------------------------------------------------------------- 环境判定

    [Fact]
    public void A_self_consistent_production_configuration_should_produce_no_findings()
    {
        // 这条是基线的对照：如果它开始报错，说明护栏把"正常生产"也算成了问题
        EnvironmentGuard.Evaluate(Config(), Environments.Production).Should().BeEmpty();
    }

    [Fact]
    public void Development_should_be_exempt()
    {
        // 开发机连本机数据库、开着接口文档、RequireHttps=false 都是天经地义。
        // 对这些环境做检查只会制造噪音，然后被顺手关掉 —— 那才是真正的损失。
        var findings = EnvironmentGuard.Evaluate(
            Config(
                ("ConnectionStrings:DefaultConnection", "Server=(local);Database=AuthHub.Dev;Trusted_Connection=True"),
                ("AuthHub:Issuer", ""),
                ("AuthHub:Features:EnableApiDocs", "true"),
                ("AuthHub:Security:RequireHttps", "false")),
            Environments.Development);

        findings.Should().BeEmpty();
    }

    [Fact]
    public void Testing_should_be_exempt()
    {
        // 集成测试的宿主环境（WebApplicationFactory 用 UseEnvironment("Testing")）。
        // 它的配置本来就是"不该拿去部署"的样子：SQLite 临时库、明文演示口令、
        // RequireHttps=false。检查它会全线误伤。
        var findings = EnvironmentGuard.Evaluate(
            Config(
                ("Database:Provider", "Sqlite"),
                ("AuthHub:Issuer", ""),
                ("AuthHub:Features:EnableApiDocs", "true")),
            EnvironmentGuard.TestingEnvironmentName);

        findings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Development", false)]
    [InlineData("development", false)]
    [InlineData("Testing", false)]
    [InlineData("testing", false)]
    [InlineData("Production", true)]
    [InlineData("Staging", true)]
    [InlineData("PreProd", true)]
    public void Protected_environments_should_be_everything_except_development_and_testing(
        string environmentName, bool expected)
    {
        // 判据刻意是"不是开发、也不是测试"而不是"是生产"：自定义的环境名（Staging、
        // PreProd）因此天然被保护。漏保护的代价是某个环境悄悄跑着开发配置，
        // 而多保护的代价只是多一次启动失败 —— 两者不对称，所以往严的一边倒。
        EnvironmentGuard.IsProtectedEnvironment(environmentName).Should().Be(expected);
    }

    // ---------------------------------------------------------------- 连接串

    [Theory]
    [InlineData("Server=(local);Database=AuthHub;Trusted_Connection=True")]
    [InlineData("Server=(localdb)\\MSSQLLocalDB;Database=AuthHub;Trusted_Connection=True")]
    [InlineData("Server=.;Database=AuthHub;Trusted_Connection=True")]
    [InlineData("Data Source=(local);Database=AuthHub;Trusted_Connection=True")]
    public void Local_only_connection_strings_should_block_startup(string connectionString)
    {
        // 这是本次护栏最想抓的场景：开发配置被带到了服务器上。
        // (local) 走共享内存、(localdb) 是 LocalDB 实例 —— 离开本机进程就不可达，
        // 在部署环境里没有任何"凑合能跑"的可能，所以是 Fail 而不是 Warn。
        var findings = EnvironmentGuard.Evaluate(
            Config(("ConnectionStrings:DefaultConnection", connectionString)),
            Environments.Production);

        var finding = Find(findings, "ConnectionStrings:DefaultConnection");
        finding.Should().NotBeNull();
        finding!.Severity.Should().Be(GuardSeverity.Failure);
        finding.Message.Should().Contain("开发配置被带到了服务器上");
    }

    [Theory]
    [InlineData("Server=localhost;Database=AuthHub")]
    [InlineData("Server=localhost,1433;Database=AuthHub")]
    [InlineData("Server=localhost\\SQLEXPRESS;Database=AuthHub")]
    [InlineData("Server=127.0.0.1;Database=AuthHub")]
    [InlineData("Server=tcp:localhost,1433;Database=AuthHub")]
    [InlineData("Server=[::1],1433;Database=AuthHub")]
    public void Loopback_connection_strings_should_only_warn(string connectionString)
    {
        // 与上一条的分界就在这里：localhost 在单机部署里是**正当**的，
        // 判成 Fail 会把这类部署直接挡在门外。所以降一级，只要求它被看见。
        // 顺带钉住解析要能剥掉端口、实例名、tcp: 前缀与 IPv6 方括号。
        var findings = EnvironmentGuard.Evaluate(
            Config(("ConnectionStrings:DefaultConnection", connectionString)),
            Environments.Production);

        var finding = Find(findings, "ConnectionStrings:DefaultConnection");
        finding.Should().NotBeNull(because: $"Server 值 {connectionString} 里的主机是 loopback");
        finding!.Severity.Should().Be(GuardSeverity.Warning);
    }

    [Fact]
    public void A_realistic_remote_connection_string_should_not_be_reported()
    {
        // 反向对照：别把正常的远程地址也当成 loopback 或本机写法
        var findings = EnvironmentGuard.Evaluate(
            Config(("ConnectionStrings:DefaultConnection",
                "Server=db.internal,1433;Database=AuthHub;User Id=authhub;Password=p;Encrypt=True;")),
            Environments.Production);

        Find(findings, "ConnectionStrings:DefaultConnection").Should().BeNull();
    }

    [Fact]
    public void An_unreplaced_placeholder_should_block_startup()
    {
        // 仓库里 appsettings.json 刻意留的假地址就长这样：
        // 主机名解析不了、口令里带 CHANGE_ME。以前它只会换来一句语焉不详的连接失败。
        var findings = EnvironmentGuard.Evaluate(
            Config(("ConnectionStrings:DefaultConnection",
                "Server=prod-db-server;Database=AuthHub;User Id=sa;Password=CHANGE_ME_IN_SECRET_STORE;")),
            Environments.Production);

        var finding = Find(findings, "ConnectionStrings:DefaultConnection");
        finding.Should().NotBeNull();
        finding!.Severity.Should().Be(GuardSeverity.Failure);
        finding.Message.Should().Contain("CHANGE_ME");
    }

    [Fact]
    public void Sqlite_in_a_protected_environment_should_warn()
    {
        // SQLite 不是非法配置（小规模自用可能真这么部署），但它没有并发写入能力、
        // 且启动走 EnsureCreated 而不是迁移 —— 结构变更无法增量演进。所以是 Warn。
        var findings = EnvironmentGuard.Evaluate(
            Config(
                ("Database:Provider", "Sqlite"),
                ("ConnectionStrings:DefaultConnection", "Data Source=authhub.db")),
            Environments.Production);

        var finding = Find(findings, "Database:Provider");
        finding.Should().NotBeNull();
        finding!.Severity.Should().Be(GuardSeverity.Warning);
    }

    [Fact]
    public void A_missing_connection_string_should_not_be_reported_twice()
    {
        // 连接串缺失由 PersistenceExtensions 在更早的阶段抛出（那时连 DbContext 都注册不了）。
        // 两条消息描述同一件事只会让人怀疑还存在第二个问题。
        var findings = EnvironmentGuard.Evaluate(
            Config(("ConnectionStrings:DefaultConnection", "")),
            Environments.Production);

        Find(findings, "ConnectionStrings:DefaultConnection").Should().BeNull();
    }

    // ---------------------------------------------------------------- Issuer

    [Fact]
    public void A_placeholder_issuer_should_block_startup()
    {
        // example.com 是 RFC 2606 的保留域名，永远不会是真实地址 —— 出现即模板没改。
        // 它的下游表现是"令牌验签失败"，排查方向很难指回配置。
        var findings = EnvironmentGuard.Evaluate(
            Config(("AuthHub:Issuer", "https://authhub.example.com/")),
            Environments.Production);

        var finding = Find(findings, "AuthHub:Issuer");
        finding.Should().NotBeNull();
        finding!.Severity.Should().Be(GuardSeverity.Failure);
    }

    [Fact]
    public void A_missing_issuer_should_block_startup()
    {
        var findings = EnvironmentGuard.Evaluate(
            Config(("AuthHub:Issuer", "")),
            Environments.Production);

        Find(findings, "AuthHub:Issuer")!.Severity.Should().Be(GuardSeverity.Failure);
    }

    // ---------------------------------------------------------------- 开关

    [Fact]
    public void Api_docs_in_a_protected_environment_should_block_startup()
    {
        // 它是一份"有哪些接口、收什么参数"的完整清单，不该对部署环境常开。
        var findings = EnvironmentGuard.Evaluate(
            Config(("AuthHub:Features:EnableApiDocs", "true")),
            Environments.Production);

        var finding = Find(findings, "AuthHub:Features:EnableApiDocs");
        finding.Should().NotBeNull();
        finding!.Severity.Should().Be(GuardSeverity.Failure);
        finding.Message.Should().Contain("GuardMode", because: "要在消息里告诉运维怎么表达「这是有意为之」");
    }

    [Fact]
    public void Relaxed_transport_should_only_warn()
    {
        // TLS 在网关卸载时关掉 RequireHttps 是正当形态，不能拦。
        var findings = EnvironmentGuard.Evaluate(
            Config(("AuthHub:Security:RequireHttps", "false")),
            Environments.Production);

        Find(findings, "AuthHub:Security:RequireHttps")!.Severity.Should().Be(GuardSeverity.Warning);
    }

    [Fact]
    public void Password_flow_should_only_warn()
    {
        // ROPC 有正当的兼容场景（老客户端只支持它），因此只提醒不拦。
        var findings = EnvironmentGuard.Evaluate(
            Config(("AuthHub:Features:EnablePasswordFlow", "true")),
            Environments.Production);

        Find(findings, "AuthHub:Features:EnablePasswordFlow")!.Severity.Should().Be(GuardSeverity.Warning);
    }

    // ---------------------------------------------------------------- GuardMode

    [Fact]
    public void Guard_mode_warn_should_downgrade_every_failure()
    {
        // 「我知道我在做什么，但请把它记下来」—— 降级而不是静默，
        // 这样日志里仍然留有痕迹，事后能看出这个取值是有意选的。
        var findings = EnvironmentGuard.Evaluate(
            Config(
                ("AuthHub:Security:GuardMode", "Warn"),
                ("ConnectionStrings:DefaultConnection", "Server=(local);Database=AuthHub;Trusted_Connection=True"),
                ("AuthHub:Issuer", "https://authhub.example.com/"),
                ("AuthHub:Features:EnableApiDocs", "true")),
            Environments.Production);

        // 三个原本是 Failure 的项都要出现，且全部降级 —— 少一条就说明降级把结论吞掉了
        findings.Should().HaveCount(3);
        findings.Should().OnlyContain(finding => finding.Severity == GuardSeverity.Warning);
    }

    [Fact]
    public void Guard_mode_off_should_skip_every_check()
    {
        var findings = EnvironmentGuard.Evaluate(
            Config(
                ("AuthHub:Security:GuardMode", "Off"),
                ("ConnectionStrings:DefaultConnection", "Server=(local);Database=AuthHub;Trusted_Connection=True"),
                ("AuthHub:Issuer", "https://authhub.example.com/")),
            Environments.Production);

        findings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Strict")]
    [InlineData("strict")]
    [InlineData("这不是一个合法的取值")]
    public void Guard_mode_should_fall_back_to_strict_on_anything_unparseable(string raw)
    {
        // 出错方向必须是"查得更严"：写错一个枚举值不该变成关掉护栏，
        // 否则它就成了一个安静的绕过开关，而"写错了"恰恰是最常见的状态。
        var configuration = Config(
            ("AuthHub:Security:GuardMode", raw),
            ("ConnectionStrings:DefaultConnection", "Server=(local);Database=AuthHub;Trusted_Connection=True"));

        EnvironmentGuard.ReadGuardMode(configuration).Should().Be(GuardMode.Strict);
        EnvironmentGuard.Evaluate(configuration, Environments.Production)
            .Should().Contain(finding => finding.Severity == GuardSeverity.Failure);
    }
}
