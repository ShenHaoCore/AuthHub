using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AuthHub.IntegrationTests.Infrastructure;

namespace AuthHub.IntegrationTests;

/// <summary>
/// 临时探针（用完即删）：① 校验失败响应的真实 Content-Type；② 后台各页渲染 HTML 快照。
/// </summary>
[Collection(AuthHubFixture.CollectionName)]
public class ZzProbe
{
    private static readonly string[] AdminPages =
    {
        "/admin", "/admin/users", "/admin/roles", "/admin/clients", "/admin/scopes", "/admin/audit-logs", "/admin/profile"
    };

    private readonly AuthHubFixture _fixture;

    public ZzProbe(AuthHubFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Zz01_validation_failure_response()
    {
        var report = new StringBuilder();
        Directory.CreateDirectory(".tmp/probe");

        // ① 空对象：绑定期就失败（非空引用类型 → 隐式 Required），走 [ApiController] 的 ModelState 短路
        report.AppendLine(await DescribeAsync("empty-object", "{}"));

        // ② 值能绑定但业务规则不过：应命中 ValidationFilter 里的 FluentValidation
        report.AppendLine(await DescribeAsync(
            "fluent-validation",
            """{"userName":"ab","email":"not-an-email","password":"weak"}"""));

        // ③ 同样走 FluentValidation，但换一个带 [Produces] 的控制器确认差异
        report.AppendLine(await DescribeAsync(
            "user-create",
            """{"userName":"ab","email":"not-an-email","password":"weak"}""",
            "/api/users"));

        await File.WriteAllTextAsync(".tmp/probe/validation.txt", report.ToString());
    }

    private async Task<string> DescribeAsync(string label, string json, string path = "/api/account/register")
    {
        var response = await _fixture.Client.PostAsync(
            path,
            new StringContent(json, Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        return $"""
                --- {label} ({path}) ---
                status       = {(int)response.StatusCode}
                content-type = {response.Content.Headers.ContentType}
                body         = {body}

                """;
    }

    [Fact]
    public async Task Zz02_snapshot_admin_pages()
    {
        Directory.CreateDirectory(".tmp/probe/before");

        using var session = new CookieSession(_fixture.Factory.Server);
        await LoginAsync(session);

        foreach (var path in AdminPages)
        {
            var html = await CookieSession.ReadHtmlAsync(await session.GetAsync(path));
            await File.WriteAllTextAsync($".tmp/probe/before/{Slug(path)}.html", Normalize(html));
        }

        // 失败重渲染路径：弹窗带 autoopen="true"
        var token = AuthHubFixture.ExtractAntiforgeryToken(
            await CookieSession.ReadHtmlAsync(await session.GetAsync("/admin/users")));
        var failed = await session.PostFormAsync("/admin/users?handler=create", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token!,
            ["NewUserName"] = string.Empty,
            ["NewEmail"] = string.Empty,
            ["NewPassword"] = string.Empty,
            ["ReturnUrl"] = "/admin/users"
        });
        await File.WriteAllTextAsync(".tmp/probe/before/users-failed.html",
            Normalize(await CookieSession.ReadHtmlAsync(failed)));

        Console.WriteLine("[PROBE] snapshot written to .tmp/probe/before/");
    }

    private static string Slug(string path) => path.Trim('/').Replace('/', '-') is { Length: > 0 } s ? s : "index";

    /// <summary>把行尾空白与连续空白归一，避免格式噪声淹没真实差异。</summary>
    private static string Normalize(string html)
    {
        var collapsed = Regex.Replace(html, "[ \\t]+", " ");
        collapsed = Regex.Replace(collapsed, "(\\r?\\n[ \\t]*){2,}", "\n");
        return collapsed.Trim();
    }

    private static async Task LoginAsync(CookieSession session)
    {
        var token = AuthHubFixture.ExtractAntiforgeryToken(
            await CookieSession.ReadHtmlAsync(await session.GetAsync("/account/login")));

        var response = await session.PostFormAsync("/account/login", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token!,
            ["username"] = "admin",
            ["password"] = "Admin@12345",
            ["returnUrl"] = "/"
        });

        if (response.StatusCode != HttpStatusCode.Found)
        {
            throw new InvalidOperationException($"登录失败：{response.StatusCode}");
        }
    }
}
