using System.Net;
using System.Text.RegularExpressions;
using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace AuthHub.IntegrationTests;

/// <summary>
/// 管理后台（Razor Pages，/admin/*）的端到端契约测试。
///
/// 这里盯住四类"改一行就可能悄悄坏掉"的地方：
///
/// 1) **后台页面的认证方案只能是会话 Cookie**。Program.cs 把
///    DefaultChallengeScheme 指向 OpenIddict Validation（为的是 /api 返回 401 而不是 302）。
///    一旦 UI 策略忘了显式挂 Cookie 方案，浏览器访问 /admin 会拿到 401 + WWW-Authenticate，
///    用户看到一片空白而不是登录页 —— 这条断言就是防这个回归的。
/// 2) **页面必须完全自给**：CSP 是 script-src 'self'，任何 CDN 引用都会被拦掉，
///    表现为"样式丢了/按钮点了没反应"。所以在测试里直接禁止外链。
/// 3) **协议页与后台共用同一份设计令牌**（authhub-tokens.css），不许各自内联一份 CSS。
/// 4) **列表页的表单真的能提交**（以 Scope 页为样本走一遍创建 → 展示 → 删除）。
/// </summary>
[Collection(AuthHubFixture.CollectionName)]
public class AdminUiTests
{
    /// <summary>后台的 7 个页面 + 每页上一段只在正确渲染时才会出现的文字。</summary>
    private static readonly (string Path, string Marker)[] AdminPages =
    {
        ("/admin", "仪表盘"),
        ("/admin/users", "新建用户"),
        ("/admin/roles", "权限矩阵"),
        ("/admin/clients", "注册客户端"),
        ("/admin/scopes", "新建 Scope"),
        ("/admin/audit-logs", "快捷范围"),
        ("/admin/profile", "我的账户")
    };

    /// <summary>后台依赖的静态资源（少一个页面就残了，因此单独列出）。</summary>
    private static readonly string[] StaticAssets =
    {
        "/css/authhub-tokens.css",
        "/css/authhub.css",
        "/css/authhub-auth.css",
        "/js/authhub.js",
        "/favicon.svg"
    };

    private const string ThirdPartyUrlPattern = "(?:src|href)=\"(?<url>[^\"]+)\"";

    private readonly AuthHubFixture _fixture;

    public AdminUiTests(AuthHubFixture fixture) => _fixture = fixture;

    // ==================================================================== 未登录

    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/users")]
    [InlineData("/admin/roles")]
    [InlineData("/admin/clients")]
    [InlineData("/admin/scopes")]
    [InlineData("/admin/audit-logs")]
    [InlineData("/admin/profile")]
    public async Task Anonymous_request_to_admin_page_should_redirect_to_login(string path)
    {
        using var session = NewSession();

        var response = await session.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        // Cookie 中间件用 BuildRedirectUri 生成的是绝对地址，因此比较路径部分
        response.Headers.Location!.AbsolutePath.Should().StartWith("/account/login");
    }

    [Fact]
    public async Task Admin_challenge_should_use_session_cookie_not_bearer()
    {
        // 后台策略若没显式只挂 Cookie 方案，就会被默认的 OpenIddict Validation 方案抢先处理，
        // 结果是浏览器拿到 401 + WWW-Authenticate（无法渲染登录页）。
        using var session = NewSession();

        var response = await session.GetAsync("/admin");

        response.StatusCode.Should().NotBe(
            HttpStatusCode.Unauthorized,
            "浏览器页面应当 302 到登录页，而不是抛出 Bearer 挑战");
        response.Headers.WwwAuthenticate.Should().BeEmpty();
    }

    // ==================================================================== 权限

    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/users")]
    [InlineData("/admin/clients")]
    public async Task User_without_admin_permission_should_be_redirected_to_denied(string path)
    {
        // alice 只属于内置的 User 角色，不含任何 authhub:permission
        using var session = NewSession();
        await LoginAsync(session, "alice", "Alice@12345");

        var response = await session.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.AbsolutePath.Should().StartWith("/account/denied");
    }

    [Fact]
    public async Task Profile_page_should_be_reachable_by_any_authenticated_user()
    {
        // 「我的账户」刻意只要求登录（Ui.Authenticated），普通用户也要能改自己的密码与 MFA
        using var session = NewSession();
        await LoginAsync(session, "alice", "Alice@12345");

        var response = await session.GetAsync("/admin/profile");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CookieSession.ReadHtmlAsync(response)).Should().Contain("我的账户");
    }

    [Fact]
    public async Task Sidebar_should_hide_entries_the_user_cannot_access()
    {
        using var session = NewSession();
        await LoginAsync(session, "alice", "Alice@12345");

        var html = await CookieSession.ReadHtmlAsync(await session.GetAsync("/admin/profile"));

        // 普通用户的侧边栏只剩「我的账户」；管理入口既不可见，也不该被误判为可访问
        html.Should().Contain("href=\"/admin/profile\"");
        html.Should().NotContain("href=\"/admin/users\"");
        html.Should().NotContain("href=\"/admin/audit-logs\"");
    }

    // ==================================================================== 管理员

    [Fact]
    public async Task Admin_should_be_able_to_open_every_admin_page()
    {
        using var session = NewSession();
        await LoginAsync(session, "admin", "Admin@12345");

        foreach (var (path, marker) in AdminPages)
        {
            var response = await session.GetAsync(path);
            var html = await CookieSession.ReadHtmlAsync(response);

            response.StatusCode.Should().Be(HttpStatusCode.OK, because: $"{path} 应可访问");
            html.Should().Contain(marker, because: $"{path} 应渲染出「{marker}」");
            html.Should().Contain("统一认证授权中心", because: $"{path} 应使用共享布局");
        }
    }

    [Fact]
    public async Task Admin_pages_should_not_load_any_external_resource()
    {
        using var session = NewSession();
        await LoginAsync(session, "admin", "Admin@12345");

        foreach (var (path, _) in AdminPages)
        {
            var html = await CookieSession.ReadHtmlAsync(await session.GetAsync(path));

            ExternalUrls(html).Should().BeEmpty(
                because: $"CSP 是 script-src 'self'，{path} 引用任何站外资源都会被浏览器拦掉");
        }
    }

    // ==================================================================== 静态资源

    [Theory]
    [InlineData("/css/authhub-tokens.css", "text/css")]
    [InlineData("/css/authhub.css", "text/css")]
    [InlineData("/css/authhub-auth.css", "text/css")]
    [InlineData("/js/authhub.js", "text/javascript")]
    [InlineData("/favicon.svg", "image/svg+xml")]
    public async Task Static_asset_should_be_served(string path, string expectedContentType)
    {
        using var session = NewSession();

        var response = await session.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK, because: $"{path} 必须能被浏览器取到");
        response.Content.Headers.ContentType?.MediaType.Should().Be(expectedContentType);
    }

    [Fact]
    public async Task Protocol_page_and_admin_page_should_share_the_same_design_tokens()
    {
        using var session = NewSession();

        var loginHtml = await CookieSession.ReadHtmlAsync(await session.GetAsync("/account/login"));
        var css = await CookieSession.ReadHtmlAsync(await session.GetAsync("/css/authhub-auth.css"));

        // 协议页改为外链样式表，不再把 CSS 内联进每个响应
        loginHtml.Should().Contain("/css/authhub-auth.css");
        loginHtml.Should().NotContain("<style", because: "样式已抽到 wwwroot，避免重复下发与两处不同步");

        // 协议页样式表的调色板来自共享令牌文件，而不是自己复制一份
        css.Should().Contain("@import url(\"/css/authhub-tokens.css\")");
        css.Should().Contain("var(--ah-primary)");
    }

    // ==================================================================== 表单往返

    [Fact]
    public async Task Audit_page_should_filter_by_date_range_on_every_provider()
    {
        // 回归护栏：SQLite 没有原生 DateTimeOffset，EF Core 能翻译它的排序却翻译不了比较，
        // 于是 `CreatedAt >= from` 会抛 "could not be translated" —— 表现为按时间筛选审计日志时 500。
        // 修复方式是在 DbContext 里对 SQLite 改用 UtcTicks 存储（见 AuthHubDbContext）。
        // 这条用例确保两种提供程序下时间区间筛选都真的能跑。
        using var session = NewSession();
        await LoginAsync(session, "admin", "Admin@12345");

        var response = await session.GetAsync("/admin/audit-logs?from=2020-01-01&to=2030-01-01");
        var html = await CookieSession.ReadHtmlAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK, because: "时间区间筛选不应因提供程序差异而 500");
        html.Should().NotContain("筛选条件有调整", because: "边界合法的日期区间不该被降级");
    }

    [Fact]
    public async Task Scope_page_should_round_trip_a_scope_whose_name_contains_a_colon()
    {
        using var session = NewSession();
        await LoginAsync(session, "admin", "Admin@12345");

        // 冒烟用的名字刻意带 ':' —— OIDC 里这是合法字符（例如 api:read），
        // 但它会破坏 CSS 选择器语法，因此弹窗 id 回退到 getElementById 的契约必须成立
        var name = $"ui-smoke:{Guid.NewGuid():N}"[..17];

        try
        {
            var created = await session.PostFormAsync("/admin/scopes?handler=create", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = await AntiforgeryTokenAsync(session, "/admin/scopes"),
                ["NewName"] = name,
                ["NewDisplayName"] = "后台冒烟",
                ["NewResources"] = "authhub-api"
            });

            created.StatusCode.Should().Be(HttpStatusCode.Found);

            var afterCreate = await CookieSession.ReadHtmlAsync(await session.GetAsync("/admin/scopes"));
            afterCreate.Should().Contain(name);
            afterCreate.Should().Contain(
                $"id=\"dlg-edit-{name}\"",
                because: "标记里会出现含 ':' 的 id，由 authhub.js 的 byIdOrSelector 兜住");
        }
        finally
        {
            // 清理：不给其它用例留脏数据
            var deleted = await session.PostFormAsync("/admin/scopes?handler=delete", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = await AntiforgeryTokenAsync(session, "/admin/scopes"),
                ["TargetName"] = name
            });

            deleted.StatusCode.Should().Be(HttpStatusCode.Found);
        }
    }

    // ==================================================================== 辅助

    private CookieSession NewSession() => new(_fixture.Factory.Server);

    private static async Task LoginAsync(CookieSession session, string userName, string password)
    {
        var token = await AntiforgeryTokenAsync(session, "/account/login");

        var response = await session.PostFormAsync("/account/login", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["username"] = userName,
            ["password"] = password,
            ["returnUrl"] = "/"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Found, because: $"{userName} 的登录应成功并跳转");
        session.CookieNames.Should().Contain(
            name => name.Contains("authhub.session", StringComparison.Ordinal),
            because: "登录成功应下发会话 Cookie");
    }

    /// <summary>取指定页面上的防伪令牌（并顺带让会话拿到对应的防伪 Cookie）。</summary>
    private static async Task<string> AntiforgeryTokenAsync(CookieSession session, string path)
    {
        var html = await CookieSession.ReadHtmlAsync(await session.GetAsync(path));
        var token = AuthHubFixture.ExtractAntiforgeryToken(html);

        token.Should().NotBeNull(because: $"{path} 上应当渲染出 __RequestVerificationToken");
        return token!;
    }

    /// <summary>页面里所有指向站外的 src / href。</summary>
    private static IEnumerable<string> ExternalUrls(string html)
        => Regex.Matches(html, ThirdPartyUrlPattern, RegexOptions.IgnoreCase)
            .Select(match => match.Groups["url"].Value)
            .Where(url =>
                url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("//", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
