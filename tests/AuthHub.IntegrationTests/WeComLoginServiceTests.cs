using System.Net;
using System.Security.Claims;
using System.Text;
using AuthHub.Api.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AuthHub.IntegrationTests;

/// <summary>
/// WeComLoginService 的协议适配测试（单元级，不走 TestServer —— 放在本项目只是因为
/// 被测类在 Api 层，只有这里引用了它）。
///
/// 用内存 HttpMessageHandler 按 URL 路径分发响应，钉住四件"改一行就可能悄悄坏掉"的事：
///   1) access_token 必须缓存复用（gettoken 有全企业频率限制，每次登录都取会被限流）；
///   2) auth_code → userid → user_ticket → 成员详情 的两段交换与声明映射；
///   3) 邮箱缺失时必须写 email_verified=false（fail-closed，不能静默当成可信）；
///   4) state 的双提交校验：往返一致才放行，篡改/缺失/对不上都拒绝。
/// </summary>
public class WeComLoginServiceTests
{
    private const string TokenJson = """{"errcode":0,"errmsg":"ok","access_token":"token-1","expires_in":7200}""";
    private const string UserInfoJson = """{"errcode":0,"errmsg":"ok","userid":"zhangsan","user_ticket":"ticket-1"}""";
    private const string UserDetailJson = """{"errcode":0,"errmsg":"ok","userid":"zhangsan","name":"张三","email":"zhangsan@corp.example"}""";

    private static WeComLoginService CreateService(HttpMessageHandler handler)
    {
        var options = new ExternalLoginOptions
        {
            WeCom = new WeComProviderOptions
            {
                Enabled = true,
                CorpId = "corp-1",
                AgentId = "1000002",
                Secret = "s3cret"
            }
        };

        return new WeComLoginService(
            new StubHttpClientFactory(new HttpClient(handler)),
            Options.Create(options),
            new EphemeralDataProtectionProvider(),
            NullLogger<WeComLoginService>.Instance);
    }

    /// <summary>按请求路径分发响应的内存桩；可统计各端点被调用的次数。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string, string> _responder;

        public StubHandler(Func<string, string> responder) => _responder = responder;

        public int TokenCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/gettoken", StringComparison.Ordinal))
            {
                TokenCalls++;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responder(path), Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    [Fact]
    public async Task CreatePrincipal_should_map_userid_name_and_trusted_email_claims()
    {
        using var service = CreateService(new StubHandler(path => path switch
        {
            "/cgi-bin/gettoken" => TokenJson,
            "/cgi-bin/auth/getuserinfo" => UserInfoJson,
            _ => UserDetailJson
        }));

        var result = await service.CreatePrincipalAsync("auth-code-1", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var principal = result.Value;
        principal.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be("zhangsan",
            because: "ProviderKey 取 NameIdentifier，绑定关系就建在它上面");
        principal.FindFirstValue(ClaimTypes.Name).Should().Be("张三");
        principal.FindFirstValue(ClaimTypes.Email).Should().Be("zhangsan@corp.example");
        principal.Claims.Should().Contain(c => c.Type == "email_verified" && c.Value == "true",
            because: "企业通讯录邮箱由管理员维护，视为已验证（理由见 WeComLoginService 类注释）");
    }

    [Fact]
    public async Task Access_token_should_be_cached_across_logins()
    {
        var handler = new StubHandler(path => path switch
        {
            "/cgi-bin/gettoken" => TokenJson,
            "/cgi-bin/auth/getuserinfo" => UserInfoJson,
            _ => UserDetailJson
        });
        using var service = CreateService(handler);

        await service.CreatePrincipalAsync("auth-code-1", CancellationToken.None);
        await service.CreatePrincipalAsync("auth-code-2", CancellationToken.None);

        handler.TokenCalls.Should().Be(1,
            because: "gettoken 有全企业频率限制，第二次登录必须吃缓存而不是重新取 token");
    }

    [Fact]
    public async Task Missing_email_should_be_marked_unverified_fail_closed()
    {
        using var service = CreateService(new StubHandler(path => path switch
        {
            "/cgi-bin/gettoken" => TokenJson,
            "/cgi-bin/auth/getuserinfo" => UserInfoJson,
            // 通讯录里没填邮箱：email 与 biz_mail 都缺席
            _ => """{"errcode":0,"errmsg":"ok","userid":"zhangsan","name":"张三"}"""
        }));

        var result = await service.CreatePrincipalAsync("auth-code-1", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FindFirstValue(ClaimTypes.Email).Should().BeNull();
        result.Value.Claims.Should().Contain(c => c.Type == "email_verified" && c.Value == "false",
            because: "拿不到邮箱必须显式标 false，让 AccountService 拒绝而不是猜测");
    }

    [Fact]
    public async Task Non_member_scan_should_be_rejected_with_friendly_message()
    {
        using var service = CreateService(new StubHandler(path => path switch
        {
            "/cgi-bin/gettoken" => TokenJson,
            // 企业外部联系人：只有 openid，没有 userid/user_ticket
            _ => """{"errcode":0,"errmsg":"ok","openid":"o-openid"}"""
        }));

        var result = await service.CreatePrincipalAsync("auth-code-1", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Contain("不是企业成员");
    }

    [Fact]
    public async Task WeCom_api_error_should_return_failure_instead_of_throwing()
    {
        using var service = CreateService(new StubHandler(path => path switch
        {
            "/cgi-bin/gettoken" => TokenJson,
            // auth_code 已过期/已用
            _ => """{"errcode":40029,"errmsg":"invalid auth_code"}"""
        }));

        var result = await service.CreatePrincipalAsync("stale-code", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Contain("企业微信");
    }

    [Fact]
    public void State_roundtrip_should_return_original_return_url()
    {
        using var service = CreateService(new StubHandler(_ => TokenJson));

        service.CreateChallengeUrl("/admin/users", "https://auth.example.com/signin-wecom", out var state);

        service.TryValidateState(state, state, out var returnUrl).Should().BeTrue();
        returnUrl.Should().Be("/admin/users");
    }

    [Theory]
    [InlineData(null, "some-state")]     // Cookie 丢失
    [InlineData("some-state", null)]     // 查询串缺失
    [InlineData("some-state", "other")]  // 双提交对不上
    public void State_validation_should_reject_mismatch(string? cookieState, string? queryState)
    {
        using var service = CreateService(new StubHandler(_ => TokenJson));

        service.TryValidateState(cookieState, queryState, out _).Should().BeFalse();
    }

    [Fact]
    public void Tampered_state_should_be_rejected()
    {
        using var service = CreateService(new StubHandler(_ => TokenJson));
        service.CreateChallengeUrl("/admin", "https://auth.example.com/signin-wecom", out var state);

        // 同一个值但尾部拼一个字符：双提交对得上、解保护必炸 —— 防伪造这层必须接住
        var tampered = state + "x";
        service.TryValidateState(tampered, tampered, out _).Should().BeFalse();
    }
}
