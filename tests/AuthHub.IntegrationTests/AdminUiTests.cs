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

    // ==================================================================== 弹窗自动开合

    /// <summary>
    /// 默认进入页面时，任何弹窗都不允许带"自动打开"标记，且标记的取值只能是显式下写字面量。
    ///
    /// 这是一条防回归断言，起因是一个真实故障：服务端曾写成
    /// <c>data-dialog-autoopen="@Model.AutoOpenCreate"</c>，Razor 把 bool 插值渲染成
    /// <c>"True"</c>/<c>"False"</c> 字面量 —— <b>属性照样存在</b>，于是前端按值判断失效，
    /// 每个弹窗都被 showModal()：表现就是用户看到的"点菜单进入任一页面，全部弹窗一起弹出来"。
    ///
    /// 因此约定两条，缺一不可：值必须是 <c>"true"</c>/<c>"false"</c> 小写；
    /// 默认状态下必须全是 <c>"false"</c>。
    /// </summary>
    [Theory]
    [InlineData("/admin/users")]
    [InlineData("/admin/roles")]
    [InlineData("/admin/clients")]
    [InlineData("/admin/scopes")]
    public async Task Admin_pages_should_not_auto_open_any_dialog_by_default(string path)
    {
        using var session = NewSession();
        await LoginAsync(session, "admin", "Admin@12345");

        var html = await CookieSession.ReadHtmlAsync(await session.GetAsync(path));

        var flags = Regex.Matches(html, "data-dialog-autoopen=\"([^\"]*)\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        flags.Should().NotContain("true", because: $"{path} 默认不该有任何弹窗自动打开");
        flags.Should().OnlyContain(flag => flag == "false",
            because: "取值必须是显式小写字面量；写成 @bool 会渲染成 \"True\"/\"False\"，前端契约随即失效");
    }

    /// <summary>
    /// 上面那条的反面：提交失败时必须把出问题的那个弹窗**真的**重新打开，
    /// 否则用户刚填的内容就白填了（PRG 的失败分支就是靠这个特性保住输入的）。
    /// </summary>
    [Fact]
    public async Task Failed_validation_should_auto_open_the_offending_dialog()
    {
        using var session = NewSession();
        await LoginAsync(session, "admin", "Admin@12345");

        var token = await AntiforgeryTokenAsync(session, "/admin/users");

        // 用户名与邮箱留空 —— 服务端判校验失败，原地重渲染（不 302）
        var response = await session.PostFormAsync("/admin/users?handler=create", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["NewUserName"] = string.Empty,
            ["NewEmail"] = string.Empty,
            ["NewPassword"] = string.Empty,
            ["ReturnUrl"] = "/admin/users"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, because: "校验失败应原地重渲染列表页，而不是重定向");

        var html = await CookieSession.ReadHtmlAsync(response);

        html.Should().Contain("data-dialog-autoopen=\"true\"",
            because: "新建弹窗必须自动重新打开，用户填过的值才不会丢");

        // @Model.FormError 会经 HTML 编码器输出（非 ASCII 会被转成 &#x....;），所以要解码后再比对文字
        WebUtility.HtmlDecode(html).Should().Contain("用户名与邮箱为必填项",
            because: "失败原因要显示在重新打开的弹窗里");
    }

    // ==================================================================== 视图组件契约

    /// <summary>
    /// 弹窗外壳与标题栏现在由 <c>&lt;ah-dialog&gt;</c>（见 TagHelpers/AdminTagHelpers.cs）统一生成，
    /// 标题栏的 <c>&lt;h2 id&gt;</c> 与 <c>aria-labelledby</c> 都取自同一个 <c>{id}-title</c> 表达式。
    ///
    /// 这条用例盯住三件"生成器一改就静默坏掉"的事：
    ///
    /// 1. **无障碍名称不能丢**。两者一旦脱钩，弹窗就没有无障碍名称，读屏只会念"对话框"。
    ///    历史上这里差点出问题：某个弹窗的 id 是 <c>dlg-perm-{roleName}</c>，
    ///    而手写的标题 id 是老格式 <c>dlg-perm-title-{roleName}</c>，与统一的 <c>{id}-title</c> 规则对不上。
    /// 2. **关闭按钮必须是 <c>type="button"</c>**。底部的"取消"按钮位于 <c>&lt;form&gt;</c> 内部，
    ///    默认类型是 <c>submit</c> —— 少了这个属性，点"取消"会提交表单（并触发校验失败）。
    /// 3. 每个弹窗都要有标题栏，且外面套着 <c>ah-modal</c>（否则 authhub.css 的模态框样式不生效）。
    /// </summary>
    [Theory]
    [InlineData("/admin/users")]
    [InlineData("/admin/roles")]
    [InlineData("/admin/clients")]
    [InlineData("/admin/scopes")]
    public async Task Every_dialog_should_be_labelled_by_its_own_heading(string path)
    {
        using var session = NewSession();
        await LoginAsync(session, "admin", "Admin@12345");

        var html = await CookieSession.ReadHtmlAsync(await session.GetAsync(path));
        var dialogs = Regex.Matches(html, @"<dialog\b[^>]*>.*?</dialog>", RegexOptions.Singleline);

        dialogs.Should().NotBeEmpty(because: $"{path} 上应当有弹窗，否则这条断言是空跑");

        foreach (Match dialog in dialogs)
        {
            var openTag = dialog.Value[..(dialog.Value.IndexOf('>') + 1)];

            openTag.Should().Contain("class=\"ah-modal",
                because: "少了 ah-modal 类，authhub.css 的模态框样式不会生效");

            var label = Regex.Match(openTag, "aria-labelledby=\"([^\"]+)\"");
            label.Success.Should().BeTrue(because: $"{openTag} 缺少 aria-labelledby，弹窗会没有无障碍名称");

            var labelled = label.Groups[1].Value;
            Regex.Matches(dialog.Value, $"<h2 id=\"{Regex.Escape(labelled)}\"")
                .Should().HaveCount(1,
                    because: $"aria-labelledby=\"{labelled}\" 必须正好命中一个标题元素，"
                             + "标题 id 与 aria-labelledby 由同一个 {id}-title 表达式生成，脱钩就是无障碍回归");

            dialog.Value.Should().Contain("<div class=\"ah-modal-head\">",
                because: "标题栏由 ah-dialog 产出，缺了就没有标题与关闭按钮");

            // 所有带 data-dialog-close 的按钮都必须是 type="button"（含表单内的"取消"）
            var dismissers = Regex.Matches(dialog.Value, @"<button[^>]*data-dialog-close[^>]*>");
            dismissers.Should().NotBeEmpty(because: "每个弹窗都要有关闭入口");
            foreach (Match button in dismissers)
            {
                button.Value.Should().Contain("type=\"button\"",
                    because: $"「取消」默认是 submit，少了 type=\"button\" 会提交表单：{button.Value}");
            }
        }
    }

    /// <summary>
    /// 全站 68 处图标现在统一由 <c>&lt;ah-icon&gt;</c> 生成，画法只此一份。
    ///
    /// 断言的是"每一个 svg 都长得一样"：装饰性图标必须 <c>aria-hidden</c>，
    /// 描边参数必须齐全（缺 <c>stroke</c> 图标会变成黑色实心块，缺 <c>viewBox</c> 会被裁掉）。
    /// 这条用例等于把"别在视图里手写 &lt;svg&gt;"的约定钉死 —— 手写的那份必然少一两项。
    /// </summary>
    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/users")]
    [InlineData("/admin/roles")]
    [InlineData("/admin/clients")]
    [InlineData("/admin/scopes")]
    [InlineData("/admin/audit-logs")]
    [InlineData("/admin/profile")]
    public async Task Every_icon_should_follow_the_same_svg_contract(string path)
    {
        using var session = NewSession();
        await LoginAsync(session, "admin", "Admin@12345");

        var html = await CookieSession.ReadHtmlAsync(await session.GetAsync(path));
        var icons = Regex.Matches(html, @"<svg\b[^>]*>");

        icons.Should().NotBeEmpty(because: $"{path} 应当有图标，否则这条断言是空跑");

        foreach (Match icon in icons)
        {
            icon.Value.Should().Contain("class=\"ah-icon\"");
            icon.Value.Should().Contain("aria-hidden=\"true\"",
                because: "图标是纯装饰，含义由按钮的 aria-label 或可见文字承担");
            icon.Value.Should().Contain("stroke=\"currentColor\"",
                because: "不跟随文字颜色的图标在深色/悬停态下会突然变黑");
            icon.Value.Should().Contain("stroke-width=\"2\"");
            icon.Value.Should().Contain("viewBox=\"0 0 24 24\"");
            icon.Value.Should().Contain("fill=\"none\"");
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

    /// <summary>
    /// 静态资源必须显式要求重新验证。
    ///
    /// 响应里没有 Cache-Control 时，浏览器按启发式规则自己决定缓存期（RFC 9111 建议取
    /// 距 Last-Modified 的 10%）。于是改了 authhub.js 之后按 F5 也可能拿不到新文件 ——
    /// 用户看到的是"修复没生效"，非得 Ctrl+F5 才行。这条用例把它钉住。
    /// </summary>
    [Fact]
    public async Task Static_assets_should_require_revalidation()
    {
        using var session = NewSession();

        var response = await session.GetAsync("/js/authhub.js");

        response.Headers.CacheControl?.NoCache.Should().BeTrue(
            because: "缺了它，改完的 JS 会被浏览器缓存挡住，用户按 F5 也看不到");
    }

    /// <summary>
    /// 页面引用静态资源时要带内容指纹（<c>asp-append-version</c> 追加的 <c>?v=</c>）。
    ///
    /// 与上一条互补：no-cache 让浏览器每次回来验证，指纹则把"内容变了"直接体现到 URL 上 ——
    /// 连验证都不必，旧副本不可能被复用。
    /// </summary>
    [Fact]
    public async Task Static_assets_should_be_referenced_with_a_content_fingerprint()
    {
        using var session = NewSession();
        await LoginAsync(session, "admin", "Admin@12345");

        var html = await CookieSession.ReadHtmlAsync(await session.GetAsync("/admin/profile"));

        html.Should().Contain("src=\"/js/authhub.js?v=",
            because: "脚本 URL 必须随内容变化，否则浏览器会继续用缓存里的旧版本");
        html.Should().Contain("href=\"/css/authhub.css?v=", because: "样式表同理");
        html.Should().Contain("href=\"/favicon.svg?v=", because: "图标同理");
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

    // ==================================================================== 页内锚点

    /// <summary>
    /// 右上角「修改密码」是指向 <c>/admin/profile#password</c> 的**同文档锚点**。
    /// 用户已经停在「我的账户」页时点它，浏览器只改 hash、不会重新加载页面，
    /// authhub.js 的初始化也就不会重跑 —— 少了 hashchange 监听，表现就是「点了没反应」。
    ///
    /// 这条断言检查三样必须同时成立的东西：链接指向一个真实存在的标签，且脚本会响应 hash 变化。
    /// 切换效果本身在真实渲染的页面上用 jsdom 验证过（前端刻意零 npm、零构建，
    /// 仓库里没有 JS 测试运行器，因此这里只能做静态检查）。
    /// </summary>
    [Fact]
    public async Task In_page_anchor_should_switch_tabs_through_a_hashchange_listener()
    {
        using var session = NewSession();
        await LoginAsync(session, "admin", "Admin@12345");

        var html = await CookieSession.ReadHtmlAsync(await session.GetAsync("/admin/profile"));
        var script = await CookieSession.ReadHtmlAsync(await session.GetAsync("/js/authhub.js"));

        html.Should().Contain("href=\"/admin/profile#password\"",
            because: "右上角菜单里的「修改密码」跳的是本页的密码标签");
        html.Should().Contain("data-tab=\"password\"",
            because: "上面那个 hash 必须对应页面上真实存在的标签，否则锚点无处可去");
        script.Should().Contain("hashchange",
            because: "已经在本页时点该链接不会重新加载页面，只能靠 hashchange 监听接管");
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
    public async Task Audit_page_should_use_text_date_inputs_with_our_own_format_placeholder()
    {
        // 日期筛选框曾用 <input type="date">，其占位文字（年/月/日、yyyy/mm/dd、yyyy/mm/日…）
        // 由浏览器按自身语言环境绘制，HTML/CSS 控制不了，不同浏览器里长相不一致 ——
        // 用户看到的 "yyyy/mm/日" 就是这么来的。现在改为普通文本框（placeholder 归我们写）
        // + 点击弹原生日历（authhub.js 的 data-date-picker）。这条测试把"不再用原生控件"钉住。
        using var session = NewSession();
        await LoginAsync(session, "admin", "Admin@12345");

        var html = await CookieSession.ReadHtmlAsync(await session.GetAsync("/admin/audit-logs"));

        html.Should().NotContain("type=\"date\"", because: "原生日历控件的占位文字由浏览器语言环境决定，页面无法统一");
        html.Should().Contain("placeholder=\"yyyy-MM-dd\"", because: "格式提示必须由页面自己写死，所有浏览器显示一致");
    }

    [Fact]
    public async Task Audit_page_should_accept_slash_and_short_dates()
    {
        // 输入框改成文本框后，管理员键盘手输最常见的就是省零 + 斜杠写法；
        // 后端解析必须收下（原生日历回填的 yyyy-MM-dd 也依旧有效）。
        using var session = NewSession();
        await LoginAsync(session, "admin", "Admin@12345");

        var response = await session.GetAsync("/admin/audit-logs?from=2020/1/1&to=2030/12/31");
        var html = await CookieSession.ReadHtmlAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().NotContain("筛选条件有调整", because: "yyyy/M/d 是文本框下最常见的手输写法，不该被当成错误");

        // 反向：真正不合法的输入仍然要给提示，而不是静默忽略
        var bad = await session.GetAsync("/admin/audit-logs?from=不是日期");
        var badHtml = await CookieSession.ReadHtmlAsync(bad);
        badHtml.Should().Contain("筛选条件有调整", because: "非法输入必须有可见提示");
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

    // ================================================================ 入口与回跳

    /// <summary>
    /// 根路径必须把人送到后台入口，而不是 404。
    /// 这是任何人第一次访问站点时最自然的入口；没有这条跳转时，
    /// 打开 <c>https://host:port/</c> 只会看到 404，无从判断该往哪走。
    /// </summary>
    [Fact]
    public async Task Root_path_should_lead_to_the_admin_entry()
    {
        using var session = NewSession();

        var response = await session.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.OriginalString.Should().Be("/admin");
    }

    /// <summary>
    /// 登录成功后必须回到用户原本要去的页面（returnUrl）。
    ///
    /// 这不是可有可无的礼貌：<c>/admin</c> 与 <c>/connect/authorize</c> 都会把未登录用户
    /// 302 到 <c>/account/login?ReturnUrl=...</c>，回跳一旦失效，用户登录完就落到 <c>/</c> ——
    /// 那条路以前是 404，看起来像"登录之后站点坏了"。
    /// </summary>
    [Fact]
    public async Task Login_should_redirect_back_to_the_requested_returnUrl()
    {
        using var session = NewSession();

        var token = await AntiforgeryTokenAsync(session, "/account/login");

        var response = await session.PostFormAsync("/account/login", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["username"] = "admin",
            ["password"] = "Admin@12345",
            ["returnUrl"] = "/admin/users"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.OriginalString.Should().Be("/admin/users",
            because: "登录后应当回到用户原本要访问的页面，而不是落到没有路由的根路径");
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
