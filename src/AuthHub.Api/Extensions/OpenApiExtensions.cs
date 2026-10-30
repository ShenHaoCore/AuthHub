using System.Reflection;
using System.Xml.Linq;
using AuthHub.Domain.Constants;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
// Microsoft.OpenApi 2.x（随 .NET 10 的 Microsoft.AspNetCore.OpenApi 引入）把 Models 命名空间并入了根命名空间。
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace AuthHub.Api.Extensions;

/// <summary>
/// API 文档配置：OpenAPI 文档 JSON 由框架内置的生成器产出，交互式 UI 由 Scalar 提供。
/// </summary>
/// <remarks>
/// - 生成侧自 .NET 9 是框架内置的 <c>AddOpenApi</c>/<c>MapOpenApi</c>，原生读 <c>[Tags]</c>/<c>[EndpointSummary]</c>/
///   <c>[EndpointDescription]</c>；展示侧 Scalar 只认一个 URL。两边路由收敛在 <see cref="DocumentRoutePattern"/> 一个常量上。
/// - 从 Swashbuckle 迁来的两处显式处理（不要当成 bug）：① 内置生成器只认 ApiExplorer 可见端点，而 MVC 默认只交出
///   <c>[ApiController]</c> 控制器 —— 见 <see cref="AddAuthHubOpenApi"/> 里的可见性约定；② 分组描述仍由
///   <see cref="ApplyTagDescriptions"/> 自己补。
/// - 收录范围有意只含面向调用方的端点（<c>/api/*</c> 与 <c>/connect/*</c>）；浏览器页控制器在各自声明处显式关闭 ——
///   登录/提示页依赖会话与防伪令牌，进文档既不可发起也只是噪音。
/// </remarks>
public static class OpenApiExtensions
{
    /// <summary>文档名。它同时是 JSON 路径与 Scalar 路由的最后一段（<c>/scalar/v1</c>）。</summary>
    public const string DocumentName = "v1";

    /// <summary>
    /// 文档路径模板：生成器出 JSON 与 Scalar 读取共用；<c>{documentName}</c> 由 <see cref="DocumentName"/> 替换。
    /// </summary>
    public const string DocumentRoutePattern = "/openapi/{documentName}.json";

    /// <summary>文档里 OAuth2 安全方案的名称，文档定义与 Scalar 引用用的都是它。</summary>
    private const string OAuth2SchemeName = "oauth2";

    /// <summary>
    /// 注册 OpenAPI 文档生成，并声明文档元数据与 OAuth2 的授权码 / 客户端凭证两种认证方案。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>原服务集合，便于链式调用。</returns>
    public static IServiceCollection AddAuthHubOpenApi(this IServiceCollection services)
    {
        // 内置生成器读 ApiExplorer 的 ApiDescription 集合；这一步把控制器端点注册进去（Minimal API 自动可见）。
        services.AddEndpointsApiExplorer();

        // ⚠ 不是可选项，去掉会让文档静默少 14 个端点：MVC 默认只把 [ApiController] 控制器交给 ApiExplorer，
        // 而 AccountController / AuthorizationController 故意不加（要返回 HTML 与 302）。
        // Swashbuckle 曾用内置约定替我们隐式点亮全部控制器，换成内置生成器后得自己显式声明。
        services.Configure<MvcOptions>(options => options.Conventions.Add(new ApiVisibilityConvention()));

        services.AddOpenApi(DocumentName, options =>
        {
            // 文档骨架（标题/版本/OAuth2 方案）在生成完之后统一补上，
            // 不需要逐个控制器重复声明。
            options.AddDocumentTransformer(ApplyDocumentMetadata);

            // 分组描述：内置生成器不会从 XML 注释里取，只能自己补。
            options.AddDocumentTransformer(ApplyTagDescriptions);
        });

        return services;
    }

    /// <summary>
    /// 映射 OpenAPI 文档 JSON 与 Scalar 交互式 UI。
    /// </summary>
    /// <param name="app">Web 应用。</param>
    /// <returns>原应用，便于链式调用。</returns>
    public static WebApplication MapAuthHubApiDocs(this WebApplication app)
    {
        // 1) 文档 JSON —— 框架内置生成器。路径用两边共用的那个常量。
        app.MapOpenApi(DocumentRoutePattern);

        // 2) 交互式 UI —— Scalar，默认挂在 /scalar/{documentName} 即 /scalar/v1。
        app.MapScalarApiReference(options =>
        {
            options
                .WithTitle("AuthHub —— 统一认证授权中心")
                .WithOpenApiRoutePattern(DocumentRoutePattern)
                .AddPreferredSecuritySchemes([OAuth2SchemeName])
                // 关闭匿名使用统计：这个服务常部署在内网，不应有任何出网请求。
                .DisableTelemetry()
                // Scalar 默认从 fonts.scalar.com 拉 Inter，与本站 CSP font-src 'self' 冲突且内网会白等 DNS 超时；
                // 禁用后回退系统字体栈，视觉差异可忽略。
                .DisableDefaultFonts()
                .AddOAuth2Authentication(OAuth2SchemeName, oauth =>
                {
                    // 只预填 public 客户端的 clientId；机密客户端的密钥不进代码，在 UI 认证面板手工填写（生产由环境变量注入）。
                    string[] userScopes =
                    [
                        AuthHubConstants.Scopes.OpenId,
                        AuthHubConstants.Scopes.Profile,
                        AuthHubConstants.Scopes.Email,
                        AuthHubConstants.Scopes.Roles,
                        AuthHubConstants.Scopes.ApiRead,
                        AuthHubConstants.Scopes.ApiWrite
                    ];

                    string[] machineScopes =
                    [
                        AuthHubConstants.Scopes.ApiRead,
                        AuthHubConstants.Scopes.ApiWrite
                    ];

                    oauth.WithDefaultScopes(userScopes);

                    oauth.WithFlows(flows =>
                    {
                        // spa-client 是 public 客户端，无需密钥；PKCE 只走 S256 —— plain 等于把校验值明文放在前端渠道。
                        flows.WithAuthorizationCode(flow => flow
                            .WithClientId(AuthHubConstants.SeedClients.Spa)
                            .WithAuthorizationUrl("/connect/authorize")
                            .WithTokenUrl("/connect/token")
                            .WithPkce(Pkce.Sha256)
                            .WithSelectedScopes(userScopes));

                        // 客户端凭证：给 M2M 用，clientId 填 m2m-service，密钥在 UI 里手工输入。
                        flows.WithClientCredentials(flow => flow
                            .WithClientId(AuthHubConstants.SeedClients.M2M)
                            .WithTokenUrl("/connect/token")
                            .WithSelectedScopes(machineScopes));
                    });
                });
        });

        return app;
    }

    /// <summary>
    /// 补上文档级的元数据：标题、OAuth2 安全方案，以及「默认需要 api:read」的安全要求。
    /// </summary>
    /// <param name="document">生成好的文档，在此就地修改。</param>
    /// <param name="context">转换器上下文（这里用不到，签名要求）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static Task ApplyDocumentMetadata(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "AuthHub —— 统一认证授权中心",
            Version = "v1",
            Description = "基于 OpenIddict 7.x + ASP.NET Core Identity 的 OIDC 服务端。" +
                          "在文档里点 Authenticate 可直接走完整的授权码 + PKCE 流程（已预填示例 public 客户端），" +
                          "或用客户端凭证流程获取 M2M 令牌。"
        };

        document.Components ??= new OpenApiComponents();
        // Microsoft.OpenApi 2.x 里 SecuritySchemes 在反序列化/新建后可能是 null，
        // 即使属性标注为非空 —— 直接索引赋值会 NRE，必须先兜底。
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[OAuth2SchemeName] = BuildOAuth2SecurityScheme();

        // 默认按「需要 api:read」标注接口；具体权限由各控制器上的授权策略决定，
        // 这里只是让文档给出一个合理的默认提示。
        // 2.x 起安全要求挂在 document.Security 上，且「指向某个安全方案」用独立的引用类型
        // OpenApiSecuritySchemeReference 表示（OpenApiSecurityScheme.Reference 已移除）。
        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(OAuth2SchemeName, document)] =
                [AuthHubConstants.Scopes.ApiRead]
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// 给分组（tag）补上描述，取自控制器类上的 XML <c>&lt;summary&gt;</c>。
    /// </summary>
    /// <remarks>
    /// 框架内置生成器不读 XML 注释（读 .xml 的源码生成器 .NET 10 才随包提供），分组描述会丢，这里自己补。
    /// 影响面（升 TFM 时实测）：端点摘要/描述来自 <c>[EndpointSummary]</c>/<c>[EndpointDescription]</c>，不受影响；
    /// DTO 属性/参数说明本来就跨程序集拿不到，无需补。
    /// </remarks>
    private static Task ApplyTagDescriptions(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        var documentation = XmlDocumentation.Value;

        // 分组名来自 [Tags("中文分组")]，要找到它对应的控制器类型才能查 XML 注释，
        // 于是从 ApiExplorer 的描述里反查「分组名 → 控制器」。
        var controllerByTag = context.DescriptionGroups
            .SelectMany(group => group.Items)
            .Where(description => description.ActionDescriptor is ControllerActionDescriptor)
            .SelectMany(description => description.ActionDescriptor.EndpointMetadata
                .OfType<ITagsMetadata>()
                .SelectMany(metadata => metadata.Tags)
                .Select(tag => (Tag: tag, Controller: (ControllerActionDescriptor)description.ActionDescriptor)))
            .GroupBy(x => x.Tag, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Controller.ControllerTypeInfo, StringComparer.Ordinal);

        // Microsoft.OpenApi 2.x 的 Tags 在不同引用程序集下可空性标注不一致，
        // 显式判空：既避免 CS8602（CI 上是警告即失败），也不改变行为。
        if (document.Tags is null)
        {
            return Task.CompletedTask;
        }

        foreach (var tag in document.Tags)
        {
            if (!string.IsNullOrEmpty(tag.Description) ||
                !controllerByTag.TryGetValue(tag.Name ?? string.Empty, out var controller))
            {
                continue;
            }

            if (documentation.TryGetValue($"T:{controller.FullName}", out var summary))
            {
                tag.Description = summary;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 程序集同名 .xml 文件里的成员注释，键是 XML 文档成员 ID（形如 <c>T:命名空间.类型</c>）。
    /// </summary>
    /// <remarks>
    /// 只加载一次：文档是首次请求时才生成的，之后按文档名缓存，这里再懒加载一次即可。
    /// 文件不存在（未开 &lt;GenerateDocumentationFile&gt;）时退化为空表，不抛异常 ——
    /// 文档里少几段说明，不该让整个服务起不来。
    /// </remarks>
    private static readonly Lazy<IReadOnlyDictionary<string, string>> XmlDocumentation =
        new(LoadXmlDocumentation, LazyThreadSafetyMode.ExecutionAndPublication);

    private static IReadOnlyDictionary<string, string> LoadXmlDocumentation()
    {
        var xmlPath = Path.Combine(
            AppContext.BaseDirectory,
            $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");

        if (!File.Exists(xmlPath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return XDocument.Load(xmlPath)
            .Descendants("member")
            .Select(member => (
                Id: member.Attribute("name")?.Value,
                Text: CollapseWhitespace(member.Element("summary"))))
            .Where(x => !string.IsNullOrEmpty(x.Id) && !string.IsNullOrEmpty(x.Text))
            .ToDictionary(x => x.Id!, x => x.Text!, StringComparer.Ordinal);
    }

    /// <summary>
    /// 把 XML 注释里的多行缩进压成单行空格，顺带丢掉 <c>&lt;see cref&gt;</c> 之类行内标签本身。
    /// </summary>
    private static string? CollapseWhitespace(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        var text = string.Join(
            " ",
            element.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// 构造 OAuth2 安全方案：同时声明授权码与客户端凭证两条流程，供文档与 Scalar 读取。
    /// </summary>
    private static OpenApiSecurityScheme BuildOAuth2SecurityScheme() => new()
    {
        Type = SecuritySchemeType.OAuth2,
        Flows = new OpenApiOAuthFlows
        {
            AuthorizationCode = new OpenApiOAuthFlow
            {
                // 用相对地址：UI 会基于当前站点补全，
                // 因此不管从 https://localhost:5001 还是别的主机名访问都能正确跳转。
                AuthorizationUrl = new Uri("/connect/authorize", UriKind.Relative),
                TokenUrl = new Uri("/connect/token", UriKind.Relative),
                Scopes = new Dictionary<string, string>
                {
                    [AuthHubConstants.Scopes.Profile] = "用户档案",
                    [AuthHubConstants.Scopes.Email] = "邮箱",
                    [AuthHubConstants.Scopes.Roles] = "角色",
                    [AuthHubConstants.Scopes.ApiRead] = "API 只读",
                    [AuthHubConstants.Scopes.ApiWrite] = "API 读写"
                }
            },
            ClientCredentials = new OpenApiOAuthFlow
            {
                TokenUrl = new Uri("/connect/token", UriKind.Relative),
                Scopes = new Dictionary<string, string>
                {
                    [AuthHubConstants.Scopes.ApiRead] = "API 只读",
                    [AuthHubConstants.Scopes.ApiWrite] = "API 读写"
                }
            }
        }
    };
}
