using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace AuthHub.IntegrationTests;

/// <summary>
/// 登录页冒烟：当前仅密码登录，不得再出现第三方入口或扫码页签。
/// </summary>
[Collection(AuthHubFixture.CollectionName)]
public class LoginPageTests
{
    private readonly AuthHubFixture _fixture;

    public LoginPageTests(AuthHubFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Login_page_should_be_password_only_without_external_login_surface()
    {
        using var session = new CookieSession(_fixture.Factory.Server);

        var response = await session.GetAsync("/account/login");
        var html = await CookieSession.ReadHtmlAsync(response);

        html.Should().Contain("action=\"/account/login\"");
        html.Should().Contain("<h1>登录</h1>");
        html.Should().Contain("brand-name\">AuthHub");

        html.Should().NotContain("external-login");
        html.Should().NotContain("demo-external");
        html.Should().NotContain("signin-wecom");
        html.Should().NotContain("data-login-tab=\"scan\"");
        html.Should().NotContain("wecom", because: "企业微信扫码面已移除");
        html.Should().NotContain("本机快速登录");
        html.Should().NotContain("/js/authhub-login.js");
    }
}
