using System.Net;
using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace AuthHub.IntegrationTests;

/// <summary>
/// 环境护栏在**真实宿主**里的行为。
///
/// 判定规则由单元测试逐条钉住（<c>EnvironmentGuardTests</c>），这里只验证两件单元测试
/// 看不到的事：护栏确实被接进了启动序列，以及它触发时的**失败形态** —— 进程起不来、
/// 消息里带着出问题的配置项路径与那个豁免开关。一个"写了检查却没人调用"的护栏
/// 与完全没有护栏，在行为上是无法区分的。
///
/// 另有一条用例从反面确认豁免有效：测试宿主所在的 <c>Testing</c> 环境不该被这些规则挡住，
/// 否则整套集成测试会在加上护栏的当天集体变红。
/// </summary>
public class EnvironmentGuardIntegrationTests : IDisposable
{
    private const string GuardModeEnvironmentVariable = "AuthHub__Security__GuardMode";
    private const string LocalConnectionString =
        "Server=(local);Database=AuthHub;Trusted_Connection=True;MultipleActiveResultSets=true";

    public void Dispose()
    {
        // 夹具只维护它自己那批基线键，这里是本类**额外**引入的。不清掉的话它会残留成
        // 后续用例的环境（环境变量是进程级的），而症状是别的测试莫名其妙地变了行为。
        Environment.SetEnvironmentVariable(GuardModeEnvironmentVariable, null);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_local_only_connection_string_should_block_startup_in_production()
    {
        // 这条模拟的正是"开发配置被带到了服务器上"：连接串还是 (local)。
        // Provider 要一起改成 SqlServer，否则会先命中"受保护环境用 SQLite"那条规则
        // 而不再检查连接串 —— 那样测的就不是这个场景了。
        using var factory = new AuthHubWebApplicationFactory(
            new Dictionary<string, string>
            {
                ["Database__Provider"] = "SqlServer",
                ["ConnectionStrings__DefaultConnection"] = LocalConnectionString
            },
            environment: "Production");

        var exception = Record.Exception(() => factory.CreateClient());

        exception.Should().NotBeNull(because: "受保护环境连本机专用地址时必须阻止启动");
        exception!.ToString().Should()
            .Contain("ConnectionStrings:DefaultConnection", because: "要指出是哪个配置项出的问题")
            .And.Contain("GuardMode", because: "要告诉运维怎么表达「这是有意为之」");
    }

    [Fact]
    public void A_placeholder_issuer_should_block_startup_in_production()
    {
        // Issuer 没改的下游表现是"令牌验签失败"，排查方向很难指回配置。
        using var factory = new AuthHubWebApplicationFactory(
            new Dictionary<string, string>
            {
                ["AuthHub__Issuer"] = "https://authhub.example.com/"
            },
            environment: "Production");

        var exception = Record.Exception(() => factory.CreateClient());

        exception.Should().NotBeNull();
        exception!.ToString().Should().Contain("AuthHub:Issuer");
    }

    [Fact]
    public async Task Guard_mode_warn_should_let_a_suspect_configuration_start()
    {
        // 豁免开关必须真的管用，否则遇到"我就是要这么部署"的场景时，
        // 运维唯一的出路是把护栏从代码里删掉 —— 那才是真正的失控。
        using var factory = new AuthHubWebApplicationFactory(
            new Dictionary<string, string>
            {
                ["AuthHub__Issuer"] = "https://authhub.example.com/",
                [GuardModeEnvironmentVariable] = "Warn"
            },
            environment: "Production");

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            because: "降级后只记日志，服务应当照常提供");
    }

    [Fact]
    public async Task The_testing_environment_should_be_exempt_from_the_same_rules()
    {
        // 夹具的配置（占位符 Issuer、SQLite 临时库、明文演示口令、RequireHttps=false）
        // 就是"不该拿去部署"的样子 —— 豁免它正是为了让集成测试能跑。
        using var factory = new AuthHubWebApplicationFactory(
            new Dictionary<string, string>
            {
                ["AuthHub__Issuer"] = "https://authhub.example.com/"
            },
            environment: "Testing");

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
