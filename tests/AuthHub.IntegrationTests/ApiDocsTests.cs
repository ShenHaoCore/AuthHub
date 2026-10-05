using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using System.Net;
using System.Text.Json;

namespace AuthHub.IntegrationTests;

/// <summary>
/// API 文档（OpenAPI JSON + Scalar UI）的集成测试。
///
/// 文档默认关闭（<c>AuthHub:Features:EnableApiDocs</c> 为 false），
/// 因此这里单独构造一个打开它的宿主。
///
/// 重点守住两件容易在升级时被悄悄破坏的事：
/// <list type="number">
///   <item>Swashbuckle 输出文档的路径与 Scalar 读取文档的路径必须一致，
///         任一侧被改动都会让 UI 打开后取不到文档；</item>
///   <item>Scalar 的前端资源必须由本地提供 —— 一旦退回 CDN，离线/内网环境会白屏，
///         本站的 CSP（<c>script-src 'self'</c>）也会直接拦掉脚本。</item>
/// </list>
/// </summary>
public class ApiDocsTests
{
    private static AuthHubWebApplicationFactory CreateApiDocsFactory() =>
        new(new Dictionary<string, string>
        {
            ["AuthHub__Features__EnableApiDocs"] = "true"
        });

    [Fact]
    public async Task OpenApi_document_should_be_served_at_the_path_scalar_reads()
    {
        using var factory = CreateApiDocsFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        // 文档本身是合法的 OpenAPI
        root.GetProperty("openapi").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("info").GetProperty("title").GetString().Should().Contain("AuthHub");

        // OAuth2 安全方案必须存在：Scalar 的交互式授权就是基于它渲染的
        var scheme = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("oauth2");
        scheme.GetProperty("type").GetString().Should().Be("oauth2");

        var flows = scheme.GetProperty("flows");
        flows.TryGetProperty("authorizationCode", out _).Should().BeTrue(because: "授权码流程是主流程");
        flows.TryGetProperty("clientCredentials", out _).Should().BeTrue(because: "M2M 调试依赖它");
    }

    [Fact]
    public async Task Scalar_ui_should_serve_assets_locally_without_any_cdn()
    {
        using var factory = CreateApiDocsFactory();
        using var client = factory.CreateClient();

        var page = await client.GetAsync("/scalar/v1");

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Content.Headers.ContentType!.MediaType.Should().Be("text/html");

        var html = await page.Content.ReadAsStringAsync();
        html.Should().Contain("scalar.aspnetcore.js", because: "页面由引导脚本驱动");

        // 关键的部署约束：不得依赖外部 CDN。
        // 内网/离线环境取不到，且会被本站 CSP 拦下。
        html.Should().NotContain("cdn.jsdelivr.net");
        html.Should().NotContain("unpkg.com");

        // 引导脚本对应的前端 bundle 必须能取到（内嵌在程序集里，由本地路由提供）。
        // 用 ResponseHeadersRead，避免为了断言状态码把约 3.7 MB 的脚本读进内存。
        using var bundleRequest = new HttpRequestMessage(HttpMethod.Get, "/scalar/scalar.js");
        using var bundle = await client.SendAsync(bundleRequest, HttpCompletionOption.ResponseHeadersRead);

        bundle.StatusCode.Should().Be(HttpStatusCode.OK);
        bundle.Content.Headers.ContentType!.MediaType.Should().Be("text/javascript");
    }

    [Fact]
    public async Task Legacy_swagger_paths_should_no_longer_be_served()
    {
        using var factory = CreateApiDocsFactory();
        using var client = factory.CreateClient();

        // 换成 Scalar 后，Swagger UI 与旧文档路径都应消失
        (await client.GetAsync("/swagger/index.html")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/swagger/v1/swagger.json")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Api_docs_should_stay_off_when_not_enabled()
    {
        // 生产默认关闭：即使有端点映射，未开启时也不应对外暴露
        using var factory = new AuthHubWebApplicationFactory();
        using var client = factory.CreateClient();

        (await client.GetAsync("/scalar/v1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/openapi/v1.json")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ CSP

    /// <summary>
    /// Scalar 的页面模板里有一段**内联**的 <c>&lt;script type="module"&gt;</c> 初始化脚本
    /// （承载文档源与 OAuth2 预填参数），本站的基准 CSP（<c>script-src 'self'</c>）会拦掉它，
    /// 症状是页面 200、资源全通、但浏览器里一片空白 —— 纯接口断言根本发现不了。
    /// 因此文档 UI 路径的 CSP 必须放行内联脚本（.NET 集成拿不到 nonce，见中间件注释）。
    /// </summary>
    [Fact]
    public async Task Scalar_page_csp_should_allow_inline_scripts()
    {
        using var factory = CreateApiDocsFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/scalar/v1");
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        var scriptSrc = GetDirective(csp, "script-src");

        scriptSrc.Should().NotBeNull(because: "只有显式声明 script-src 才谈得上放行内联脚本");
        scriptSrc.Should().Contain("'unsafe-inline'");
    }

    /// <summary>
    /// 上面那条放宽的护栏：放行内联脚本只允许发生在 <c>/scalar</c> 之下。
    /// 登录页与 API 响应必须维持基准策略 —— 防止有人"修不好就全局放宽"。
    /// 注意 style-src 一直含 <c>'unsafe-inline'</c>（行内样式），因此必须按
    /// <c>script-src</c> 指令段断言，不能对整条策略做字符串包含判断。
    /// </summary>
    [Fact]
    public async Task Strict_csp_should_stay_in_effect_outside_the_docs_ui()
    {
        using var factory = CreateApiDocsFactory();
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/account/login", "/health", "/openapi/v1.json" })
        {
            var response = await client.GetAsync(path);

            response.Headers.TryGetValues("Content-Security-Policy", out var values).Should()
                .BeTrue(because: $"基准策略必须覆盖 {path}");
            var scriptSrc = GetDirective(values!.Single(), "script-src");

            scriptSrc.Should().NotBeNull(because: $"{path} 应有显式的 script-src");
            scriptSrc.Should().NotContain("'unsafe-inline'",
                because: $"{path} 不属于文档 UI，内联脚本必须保持被拦");
        }
    }

    /// <summary>从 CSP 字符串里取出某条指令的原文（如 <c>script-src 'self'</c>），找不到返回 null。</summary>
    private static string? GetDirective(string csp, string directiveName) =>
        csp.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(d => d.StartsWith(directiveName, StringComparison.Ordinal));
}
