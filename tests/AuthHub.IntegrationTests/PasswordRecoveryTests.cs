using System.Net;
using System.Text;
using System.Text.Json;
using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace AuthHub.IntegrationTests;

/// <summary>
/// 忘记密码 / 重置密码 / 邮箱确认的集成测试。
///
/// 邮件通道在测试环境是 LoggingEmailSender（只写日志），
/// 所以这里只验证 HTTP 层行为：页面渲染、表单提交跳转、API 状态码，
/// 不验证邮件是否真的发出（那属于基础设施层，单元测试已覆盖）。
/// </summary>
[Collection(AuthHubFixture.CollectionName)]
public class PasswordRecoveryTests
{
    private readonly AuthHubFixture _fixture;

    public PasswordRecoveryTests(AuthHubFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ 忘记密码页面

    [Fact]
    public async Task Login_page_should_render_forgot_password_link()
    {
        using var session = new CookieSession(_fixture.Factory.Server);
        var html = await CookieSession.ReadHtmlAsync(await session.GetAsync("/account/login"));

        html.Should().Contain("/account/forgot-password");
        html.Should().Contain("忘记密码");
    }

    [Fact]
    public async Task Forgot_password_page_should_render_email_form()
    {
        using var session = new CookieSession(_fixture.Factory.Server);
        var html = await CookieSession.ReadHtmlAsync(await session.GetAsync("/account/forgot-password"));

        html.Should().Contain("action=\"/account/forgot-password\"");
        html.Should().Contain("name=\"email\"");
        html.Should().Contain("发送重置链接");
    }

    [Fact]
    public async Task Forgot_password_post_should_redirect_to_sent_page()
    {
        using var session = new CookieSession(_fixture.Factory.Server);
        var token = await GetAntiforgeryTokenAsync(session, "/account/forgot-password");

        var response = await session.PostFormAsync("/account/forgot-password", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["email"] = "nobody@example.com"
        });

        // 无论邮箱是否存在都跳同一提示页（防用户名枚举）
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location.Should().Be("/account/forgot-password/sent");
    }

    [Fact]
    public async Task Forgot_password_empty_email_should_redisplay_form_with_error()
    {
        using var session = new CookieSession(_fixture.Factory.Server);
        var token = await GetAntiforgeryTokenAsync(session, "/account/forgot-password");

        var response = await session.PostFormAsync("/account/forgot-password", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["email"] = ""
        });

        var html = await CookieSession.ReadHtmlAsync(response);
        html.Should().Contain("请输入邮箱");
    }

    // ------------------------------------------------------------------ 重置密码页面

    [Fact]
    public async Task Reset_password_page_without_params_should_show_invalid_link()
    {
        using var session = new CookieSession(_fixture.Factory.Server);
        var html = await CookieSession.ReadHtmlAsync(await session.GetAsync("/account/reset-password"));

        html.Should().Contain("链接无效");
        html.Should().NotContain("name=\"newPassword\"");
    }

    [Fact]
    public async Task Reset_password_page_with_params_should_render_form()
    {
        using var session = new CookieSession(_fixture.Factory.Server);
        var html = await CookieSession.ReadHtmlAsync(
            await session.GetAsync("/account/reset-password?email=alice@example.com&token=abc123"));

        html.Should().Contain("action=\"/account/reset-password\"");
        html.Should().Contain("name=\"email\" value=\"alice@example.com\"");
        html.Should().Contain("name=\"token\" value=\"abc123\"");
        html.Should().Contain("name=\"newPassword\"");
    }

    // ------------------------------------------------------------------ 邮箱确认页面

    [Fact]
    public async Task Confirm_email_without_params_should_show_invalid_link()
    {
        using var session = new CookieSession(_fixture.Factory.Server);
        var html = await CookieSession.ReadHtmlAsync(await session.GetAsync("/account/confirm-email"));

        html.Should().Contain("确认链接无效");
        html.Should().Contain("/account/resend-confirmation");
    }

    [Fact]
    public async Task Resend_confirmation_page_should_render_email_form()
    {
        using var session = new CookieSession(_fixture.Factory.Server);
        var html = await CookieSession.ReadHtmlAsync(await session.GetAsync("/account/resend-confirmation"));

        html.Should().Contain("action=\"/account/resend-confirmation\"");
        html.Should().Contain("name=\"email\"");
        html.Should().Contain("发送确认邮件");
    }

    [Fact]
    public async Task Resend_confirmation_post_should_redirect_to_sent_page()
    {
        using var session = new CookieSession(_fixture.Factory.Server);
        var token = await GetAntiforgeryTokenAsync(session, "/account/resend-confirmation");

        var response = await session.PostFormAsync("/account/resend-confirmation", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["email"] = "nobody@example.com"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location.Should().Be("/account/resend-confirmation/sent");
    }

    // ------------------------------------------------------------------ JSON API

    private static StringContent Json(object value)
        => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    [Fact]
    public async Task Forgot_password_api_should_return_204_regardless_of_email_existence()
    {
        var client = _fixture.Factory.CreateClient();
        var response = await client.PostAsync("/api/account/forgot-password",
            Json(new { email = "nobody@example.com" }));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Reset_password_api_with_invalid_token_should_return_400()
    {
        var client = _fixture.Factory.CreateClient();
        // 邮箱是否存在都返回 400（防枚举）；此处注册只是覆盖「用户存在 + 坏令牌」路径
        await client.PostAsync("/api/account/register", Json(new
        {
            userName = "resetuser",
            email = "resetuser@example.com",
            password = "Pass@1234",
            displayName = "Reset User"
        }));

        var response = await client.PostAsync("/api/account/reset-password", Json(new
        {
            email = "resetuser@example.com",
            token = "invalid-token",
            newPassword = "NewPass@123"
        }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Reset_password_api_for_unknown_email_should_return_400_not_404()
    {
        var client = _fixture.Factory.CreateClient();
        var response = await client.PostAsync("/api/account/reset-password", Json(new
        {
            email = "ghost-reset@example.com",
            token = "any-token",
            newPassword = "NewPass@123"
        }));

        // 与坏令牌同为 400，避免用 404 枚举邮箱
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Reset_password_api_with_weak_password_should_return_400()
    {
        var client = _fixture.Factory.CreateClient();
        var response = await client.PostAsync("/api/account/reset-password", Json(new
        {
            email = "nobody@example.com",
            token = "any-token",
            newPassword = "weak" // 不满足复杂度
        }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Resend_confirmation_api_should_return_204()
    {
        var client = _fixture.Factory.CreateClient();
        var response = await client.PostAsync("/api/account/email/resend-confirmation",
            Json(new { email = "nobody@example.com" }));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ------------------------------------------------------------------ 辅助

    private static async Task<string> GetAntiforgeryTokenAsync(CookieSession session, string path)
    {
        var html = await CookieSession.ReadHtmlAsync(await session.GetAsync(path));
        var token = AuthHubFixture.ExtractAntiforgeryToken(html);
        token.Should().NotBeNull($"{path} 上应当渲染出 __RequestVerificationToken");
        return token!;
    }
}
