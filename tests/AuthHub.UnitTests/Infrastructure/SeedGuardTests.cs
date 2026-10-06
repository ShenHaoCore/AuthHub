using AuthHub.Domain.Constants;
using AuthHub.Infrastructure.Data.Seed;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace AuthHub.UnitTests.Infrastructure;

/// <summary>
/// 种子数据的生产期护栏。
///
/// 这是**默认拒绝**型的安全决策，因此四种组合都要钉住 —— 尤其是
/// "生产 + 开了总开关但没开二次确认"这一条：它正是历史注释声称
/// "只在 Development 调用"、而实现里其实只看总开关所留下的缺口。
/// </summary>
public class SeedGuardTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void Non_production_should_be_allowed_without_extra_opt_in()
    {
        // 开发 / 测试环境保持"开了总开关就播种"，改动不该影响既有开发体验
        var decision = AuthHubSeeder.Decide(Config(), isProduction: false);

        decision.Allowed.Should().BeTrue();
        decision.SkipReason.Should().BeNull();
    }

    [Fact]
    public void Production_without_the_second_opt_in_should_be_skipped()
    {
        var decision = AuthHubSeeder.Decide(
            Config(("AuthHub:Seeding:Enabled", "true")),
            isProduction: true);

        decision.Allowed.Should().BeFalse(because: "生产环境必须再显式确认一次，否则误开等于留下公开口令的账号");
        decision.SkipReason.Should().Contain("AllowInProduction");
    }

    [Fact]
    public void Skip_reason_should_name_the_demo_accounts_so_the_warning_is_actionable()
    {
        // 日志里只说"已跳过"没有意义，必须让人知道跳过的到底是什么风险
        var decision = AuthHubSeeder.Decide(Config(), isProduction: true);

        decision.SkipReason.Should().Contain(AuthHubConstants.SeedUsers.DemoUserName);
        decision.SkipReason.Should().Contain(AuthHubConstants.SeedUsers.AdminUserName);
    }

    [Fact]
    public void Production_with_the_second_opt_in_should_be_allowed()
    {
        // 生产首次初始化（建第一个管理员）必须仍然可用，只是要多走一步
        var decision = AuthHubSeeder.Decide(
            Config(("AuthHub:Seeding:AllowInProduction", "true")),
            isProduction: true);

        decision.Allowed.Should().BeTrue();
        decision.SkipReason.Should().BeNull();
    }

    [Fact]
    public void Missing_second_switch_should_default_to_deny_not_allow()
    {
        // 缺省方向是本次改动的核心：漏配 = 更安全，而不是漏配 = 更危险。
        // 这条用例是防止日后有人把默认值改成 true 的护栏。
        AuthHubSeeder.Decide(Config(), isProduction: true).Allowed
            .Should().BeFalse(because: "二次确认缺席时必须拒绝，不能靠「没配就是没限制」来兜底");
    }

    [Fact]
    public void Explicit_false_should_still_be_skipped()
    {
        // 显式写 false 是最容易被"顺手改回来"的形态，也必须拒绝
        var decision = AuthHubSeeder.Decide(
            Config(("AuthHub:Seeding:AllowInProduction", "false")),
            isProduction: true);

        decision.Allowed.Should().BeFalse();
    }
}
