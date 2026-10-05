using System.Reflection;
using System.Xml.Linq;
using AuthHub.Domain.Constants;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi.Models;
using Scalar.AspNetCore;

namespace AuthHub.Api.Extensions;

/// <summary>
/// API 文档配置：OpenAPI 文档 JSON 由框架内置的生成器产出，交互式 UI 由 Scalar 提供。
/// </summary>
/// <remarks>
/// 「生成文档」这一侧自 .NET 9 起不再需要第三方库：<c>AddOpenApi</c> / <c>MapOpenApi</c>
/// 随 <c>Microsoft.AspNetCore.OpenApi</c> 提供，且原生读取 <c>[Tags]</c> /
/// <c>[EndpointSummary]</c> / <c>[EndpointDescription]</c>（.NET 8 就有的端点元数据接口）。
/// 这就是把目标框架升到 net9.0 之后可以删掉 Swashbuckle 的原因。
///
/// 「展示文档」仍是 Scalar，它只认一个 URL，与生成器无关。
/// 两个约定收敛在 <see cref="DocumentRoutePattern"/> 一个常量上，不需要两边手工同步。
///
/// 从 Swashbuckle 换过来时有两处**行为差异**，都在本文件里显式处理了，不要当成 bug：
/// <list type="number">
///   <item>ApiExplorer 的可见性：内置生成器只认 ApiExplorer 里「可见」的端点，
///         而 MVC 默认只让 <c>[ApiController]</c> 控制器可见 —— 见
///         <see cref="AddAuthHubOpenApi"/> 里那段约定；</item>
///   <item>XML 注释：内置生成器不读它（.NET 10 才有），分组描述由
///         <see cref="ApplyTagDescriptions"/> 自己补，其余内容本来就不依赖它。</item>
/// </list>
///
/// 收录范围（有意为之，别当成漏了）：文档只收**面向调用方**的端点 ——
/// 各 <c>/api/*</c> 业务接口、OIDC 协议端点 <c>/connect/*</c>。
/// 产出浏览器页面的控制器与站点跳转不进来，它们各自在声明处显式关掉
/// （<c>AccountController</c> 用 <c>[ApiExplorerSettings(IgnoreApi = true)]</c>、
/// 根路径用 <c>ExcludeFromDescription()</c>）。原因是文档的读者是 API 调用方，
/// 而登录页 / 提示页需要会话与防伪令牌，既不能在 Scalar 里直接发起，
/// 出现在侧边栏里也只是噪音。
/// </remarks>
public static class OpenApiExtensions
{
    /// <summary>文档名。它同时是 JSON 路径与 Scalar 路由的最后一段（<c>/scalar/v1</c>）。</summary>
    public const string DocumentName = "v1";

    /// <summary>
    /// 文档路径模板：既是内置生成器对外提供 JSON 的地址，也是 Scalar 去读取的地址。
    /// <c>{documentName}</c> 是内置生成器的占位符，会被 <see cref="DocumentName"/> 替换。
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
        // 控制器也要出现在文档里：内置生成器读的是 ApiExplorer 的 ApiDescription 集合，
        // 这一步负责把控制器端点注册进去（Minimal API 端点则自动可见）。
        services.AddEndpointsApiExplorer();

        // ⚠ 这一行不是可选项，去掉会让文档静默少 14 个端点。
        //
        // MVC 默认只把标注了 [ApiController] 的控制器交给 ApiExplorer（见
        // ControllerActionDescriptorBuilder：IsVisible 三级都取不到值时判 false）。
        // 本项目有两个控制器**故意不加** [ApiController] —— AccountController 与
        // AuthorizationController 要返回 HTML 页面和 302，加了会改变运行时行为。
        // 于是 /account/*、/connect/*（含 OIDC 的 token / authorize / userinfo / logout）
        // 全部落在文档之外。
        //
        // 以前用 Swashbuckle 时看不到这个坑：它在 AddSwaggerGen 里加了同样作用的约定
        // （源码原话 "Add Mvc convention to ensure ApiExplorer is enabled for all actions"），
        // 替我们隐式点亮了所有控制器。换成框架内置生成器后，这一层得自己显式声明。
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
                // 关闭默认字体：Scalar 默认从 fonts.scalar.com 拉取 Inter 字体，
                // 而本站 CSP 是 font-src 'self'，这些请求必然被拦 —— 内网环境还会
                // 白白等待 DNS 解析超时。禁用后回退到系统字体栈，视觉差异可忽略。
                .DisableDefaultFonts()
                .AddOAuth2Authentication(OAuth2SchemeName, oauth =>
                {
                    // 交互式授权只预填 public 客户端的 clientId。
                    // 机密客户端（web-client / m2m-service）的密钥不写进代码，
                    // 需要在 UI 的认证面板里手工填写 —— 这些是开发用种子密钥，
                    // 生产环境必须由环境变量注入，见 README。
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
                        // 授权码 + PKCE：spa-client 是 public 客户端，没有密钥，因此不需要 WithClientSecret。
                        // PKCE 显式指定 S256 —— 服务端两个方案都声明支持，
                        // 但 plain 等于把校验值明文放在前端渠道，这里只走 S256。
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
            Description = "基于 OpenIddict 5.x + ASP.NET Core Identity 的 OIDC 服务端。" +
                          "在文档里点 Authenticate 可直接走完整的授权码 + PKCE 流程（已预填示例 public 客户端），" +
                          "或用客户端凭证流程获取 M2M 令牌。"
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes[OAuth2SchemeName] = BuildOAuth2SecurityScheme();

        // 默认按「需要 api:read」标注接口；具体权限由各控制器上的授权策略决定，
        // 这里只是让文档给出一个合理的默认提示。
        document.SecurityRequirements ??= [];
        document.SecurityRequirements.Add(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = OAuth2SchemeName }
                },
                new[] { AuthHubConstants.Scopes.ApiRead }
            }
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// 给分组（tag）补上描述，取自控制器类上的 XML <c>&lt;summary&gt;</c>。
    /// </summary>
    /// <remarks>
    /// 为什么需要这一步：XML 注释（含控制器摘要、DTO 说明、<c>&lt;param&gt;</c>）是 Swashbuckle
    /// 在运行时读 .xml 文件解析出来的；框架内置的生成器**不读它** —— 源码生成器版本要到 .NET 10
    /// 才随包提供（<c>Microsoft.AspNetCore.OpenApi/10.x</c> 的 <c>analyzers/</c> 目录里才有），
    /// 9.0.x 包里既没有那个生成器，dll 里也搜不到任何 XmlComment 相关符号。
    ///
    /// 影响面（升 TFM 时实测过，不是猜的）：
    /// <list type="bullet">
    ///   <item>端点摘要 / 描述：不受影响 —— 它们来自 <c>[EndpointSummary]</c> /
    ///         <c>[EndpointDescription]</c>，是内置生成器原生支持的端点元数据；</item>
    ///   <item>分组描述：会丢，就是这里补的东西；</item>
    ///   <item>DTO 属性说明、参数说明：本来就没有（DTO 的 XML 在另一个程序集里，
    ///         Swashbuckle 当时只加载了 Api 程序集自己的 .xml），无需补。</item>
    /// </list>
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

        foreach (var tag in document.Tags)
        {
            if (!string.IsNullOrEmpty(tag.Description) || !controllerByTag.TryGetValue(tag.Name, out var controller))
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
