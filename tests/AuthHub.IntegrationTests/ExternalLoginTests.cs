using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using System.Net;

namespace AuthHub.IntegrationTests;

/// <summary>
/// 第三方登录（GitHub / Google）的集成测试。
///
/// 集成环境走不完真的 OAuth 握手（需要真实凭据与外网），这里钉住能确定性验证的部分：
///   1) 未启用时登录页仍渲染第三方按钮作预告，但全部禁用（不可提交）；
///      challenge 路由白名单兜底，绕过页面直发请求也进不来；
///   2) 回调端点在没有外部状态时回登录页带提示，而不是 500；
///   3) 启用后按钮可用，challenge 把浏览器送到正确的授权端点（client_id / 回调地址 / state 齐全）。
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
    public async Task Login_page_should_render_external_providers_as_disabled_when_not_enabled()
    {
        using var session = new CookieSession(_fixture.Factory.Server);

        var response = await session.GetAsync("/account/login");
        var html = await CookieSession.ReadHtmlAsync(response);

        // GitHub / Google：入口渲染为禁用按钮（预告），disabled 按钮不会提交表单
        html.Should().Contain("action=\"/account/external-login\"");
        html.Should().Contain("value=\"GitHub\" disabled");
        html.Should().Contain("使用 GitHub 登录（暂未开放）");
        html.Should().Contain("value=\"Google\" disabled");
        html.Should().Contain("使用 Google 登录（暂未开放）");

        // 按钮带品牌图标（内联 SVG，无外链）
        html.Should().Contain("provider-icon");
        html.Should().Contain("viewBox=\"0 0 16 16\"", because: "GitHub 图标应为 16x16 单色 SVG");
        html.Should().Contain("viewBox=\"0 0 48 48\"", because: "Google 图标应为 48x48 四色 SVG");

        // 企业微信：由「扫码登录」页签承载，未启用时页签内是占位而非二维码
        html.Should().Contain("data-login-tab=\"scan\"");
        html.Should().Contain("企业微信扫码登录暂未开放");
        html.Should().NotContain("wwlogin/sso/login", because: "未启用时不渲染二维码 iframe");
        html.Should().NotContain("value=\"WeCom\"", because: "企业微信不是跳转按钮，由扫码页签承载");
        response.Headers.TryGetValues("Set-Cookie", out var cookies);
        (cookies ?? []).Should().NotContain(
            header => header.StartsWith("authhub.wecom.state=", StringComparison.Ordinal),
            because: "未启用时不应生成 state，也不应下发双提交 Cookie");
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

        // 登录页上已启用的 GitHub 按钮可用，未启用的 Google 呈禁用态，扫码页签为占位
        var html = await CookieSession.ReadHtmlAsync(await session.GetAsync("/account/login"));
        html.Should().Contain("使用 GitHub 登录</button>");
        html.Should().NotContain("value=\"GitHub\" disabled");
        html.Should().Contain("value=\"Google\" disabled");
        html.Should().Contain("使用 Google 登录（暂未开放）");
        html.Should().Contain("企业微信扫码登录暂未开放", because: "该宿主只启用了 GitHub，扫码页签应为占位");

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

    // ==================================================================== 企业微信扫码

    [Fact]
    public async Task WeCom_enabled_login_page_should_embed_scan_qr_iframe_and_set_state_cookie()
    {
        using var factory = WeComEnabledFactory();
        using var session = new CookieSession(factory.Server);

        var response = await session.GetAsync("/account/login?returnUrl=/admin");
        var html = await CookieSession.ReadHtmlAsync(response);

        // 二维码 iframe 直嵌登录页「扫码登录」页签，参数齐全（appid 是企业 ID，不是应用凭据）
        html.Should().Contain("data-login-tab=\"scan\"");
        html.Should().Contain("https://login.work.weixin.qq.com/wwlogin/sso/login");
        html.Should().Contain("appid=test-corp-id");
        html.Should().Contain("agentid=1000002");
        html.Should().Contain("state=");
        Uri.UnescapeDataString(html).Should().Contain("/signin-wecom",
            because: "二维码内的回调地址应指向本站的 /signin-wecom 路由");

        // state 双提交 Cookie 随登录页一起下发（回调时与查询串里的 state 做相关性校验）
        response.Headers.GetValues("Set-Cookie").Should().Contain(
            header => header.StartsWith("authhub.wecom.state=", StringComparison.Ordinal),
            because: "state 双提交 Cookie 必须随二维码一起下发");

        // 跳转式 challenge 已移除：直接构造 WeCom 的 POST 一律带回登录页（扫码入口就在那里）
        var post = await session.PostFormAsync("/account/external-login", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AuthHubFixture.ExtractAntiforgeryToken(html)!,
            ["provider"] = "WeCom",
            ["returnUrl"] = "/admin"
        });
        post.StatusCode.Should().Be(HttpStatusCode.Found);
        post.Headers.Location!.OriginalString.Should().StartWith("/account/login");
    }

    [Fact]
    public async Task WeCom_callback_should_be_rejected_when_provider_disabled()
    {
        using var session = new CookieSession(_fixture.Factory.Server);

        var response = await session.GetAsync("/signin-wecom?auth_code=x&state=y");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.OriginalString
            .Should().StartWith("/account/login", because: "共享夹具未启用 WeCom，回调必须先被白名单挡下");
    }

    [Fact]
    public async Task WeCom_callback_with_tampered_state_should_be_rejected()
    {
        using var factory = WeComEnabledFactory();
        using var session = new CookieSession(factory.Server);

        // 先打开登录页，让会话拿到合法的 state Cookie（渲染二维码时下发）
        await session.GetAsync("/account/login");

        // 再用篡改过的 state 回调：双提交校验必须挡下（Cookie 值与查询串不一致）
        var response = await session.GetAsync("/signin-wecom?auth_code=x&state=tampered-state");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var location = response.Headers.Location!.OriginalString;
        location.Should().StartWith("/account/login");
        location.Should().Contain("error=");
    }

    /// <summary>只启用 WeCom 的独立宿主（不污染共享夹具）。</summary>
    private static AuthHubWebApplicationFactory WeComEnabledFactory() => new(new Dictionary<string, string>
    {
        ["AuthHub__Authentication__WeCom__Enabled"] = "true",
        ["AuthHub__Authentication__WeCom__CorpId"] = "test-corp-id",
        ["AuthHub__Authentication__WeCom__AgentId"] = "1000002",
        ["AuthHub__Authentication__WeCom__Secret"] = "test-secret"
    });

    /// <summary>取指定页面上的防伪令牌（并顺带让会话拿到对应的防伪 Cookie）。</summary>
    private static async Task<string> AntiforgeryTokenAsync(CookieSession session, string path)
    {
        var html = await CookieSession.ReadHtmlAsync(await session.GetAsync(path));
        var token = AuthHubFixture.ExtractAntiforgeryToken(html);

        token.Should().NotBeNull(because: $"{path} 上应当渲染出 __RequestVerificationToken");
        return token!;
    }
}
