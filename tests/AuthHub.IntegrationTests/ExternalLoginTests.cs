using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using System.Net;

namespace AuthHub.IntegrationTests;

/// <summary>
/// 第三方登录（GitHub / Google）的集成测试。
///
/// 集成环境走不完真的 OAuth 握手（需要真实凭据与外网），这里钉住能确定性验证的部分：
///   1) 默认（未启用）时登录页不渲染第三方按钮，challenge 路由拒绝未列出的提供商；
///   2) 回调端点在没有外部状态时回登录页带提示，而不是 500；
///   3) 启用后 challenge 把浏览器送到正确的授权端点（client_id / 回调地址 / state 齐全）。
///
/// 回调后的账号关联决策（绑定确认、自动建号）在 AccountService，依赖外部声明构造，
/// 属于单元层验证的范畴，不在这里造外部 Cookie 伪造身份。
/// </summary>
[Collection(AuthHubFixture.CollectionName)]
public class ExternalLoginTests
{
    private readonly AuthHubFixture _fixture;

    public ExternalLoginTests(AuthHubFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Login_page_should_not_render_external_providers_when_disabled()
    {
        using var session = new CookieSession(_fixture.Factory.Server);

        var html = await CookieSession.ReadHtmlAsync(await session.GetAsync("/account/login"));

        html.Should().NotContain("external-login", because: "未启用任何提供商时不应渲染第三方登录入口");
        html.Should().NotContain("使用 GitHub 登录");
        html.Should().NotContain("使用 Google 登录");
    }

    [Fact]
    public async Task External_login_with_unlisted_provider_should_redirect_back_to_login_page()
    {
        using var session = new CookieSession(_fixture.Factory.Server);
        var token = await AntiforgeryTokenAsync(session, "/account/login");

        var response = await session.PostFormAsync("/account/external-login", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["provider"] = "GitHub", // 共享夹具未启用任何提供商
            ["returnUrl"] = "/"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var location = response.Headers.Location!.OriginalString;
        location.Should().StartWith("/account/login", because: "未启用的提供商应被白名单挡下并带回提示");
        location.Should().Contain("error=");
    }

    [Fact]
    public async Task External_callback_without_external_state_should_redirect_to_login_page()
    {
        using var session = new CookieSession(_fixture.Factory.Server);

        var response = await session.GetAsync("/account/external-callback?returnUrl=/");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.OriginalString
            .Should().StartWith("/account/login", because: "没有外部身份 Cookie 的回调只能回登录页，绝不能 500");
    }

    [Fact]
    public async Task Enabled_provider_should_challenge_to_the_remote_authorization_endpoint()
    {
        // 独立宿主：只在这一组用例里启用 GitHub，不污染共享夹具的配置
        using var factory = new AuthHubWebApplicationFactory(new Dictionary<string, string>
        {
            ["AuthHub__Authentication__GitHub__Enabled"] = "true",
            ["AuthHub__Authentication__GitHub__ClientId"] = "test-client-id",
            ["AuthHub__Authentication__GitHub__ClientSecret"] = "test-client-secret"
        });
        using var session = new CookieSession(factory.Server);

        // 登录页渲染已启用的 GitHub 按钮，不渲染未启用的 Google
        var html = await CookieSession.ReadHtmlAsync(await session.GetAsync("/account/login"));
        html.Should().Contain("使用 GitHub 登录");
        html.Should().NotContain("使用 Google 登录");

        var token = AuthHubFixture.ExtractAntiforgeryToken(html);
        token.Should().NotBeNull();

        var response = await session.PostFormAsync("/account/external-login", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token!,
            ["provider"] = "GitHub",
            ["returnUrl"] = "/admin"
        });

        // challenge 应把浏览器送到 GitHub 的授权端点，并带上本站回调地址与 state
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var location = response.Headers.Location!.OriginalString;
        location.Should().StartWith("https://github.com/login/oauth/authorize");
        location.Should().Contain("client_id=test-client-id");
        location.Should().Contain("state=");

        var unescaped = Uri.UnescapeDataString(location);
        unescaped.Should().Contain("signin-github", because: "回调地址应指向本站的 /signin-github 路由");
        unescaped.Should().Contain("redirect_uri=");
    }

    [Fact]
    public async Task External_cancel_should_clear_pending_state_and_return_to_login_page()
    {
        using var session = new CookieSession(_fixture.Factory.Server);

        var response = await session.GetAsync("/account/external/cancel?returnUrl=/account/login");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.OriginalString.Should().Be("/account/login?returnUrl=%2Faccount%2Flogin");
    }

    /// <summary>取指定页面上的防伪令牌（并顺带让会话拿到对应的防伪 Cookie）。</summary>
    private static async Task<string> AntiforgeryTokenAsync(CookieSession session, string path)
    {
        var html = await CookieSession.ReadHtmlAsync(await session.GetAsync(path));
        var token = AuthHubFixture.ExtractAntiforgeryToken(html);

        token.Should().NotBeNull(because: $"{path} 上应当渲染出 __RequestVerificationToken");
        return token!;
    }
}
