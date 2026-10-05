using System.Reflection;
using AuthHub.Domain.Constants;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.OpenApi.Models;
using Scalar.AspNetCore;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace AuthHub.Api.Extensions;

/// <summary>
/// API 文档配置：OpenAPI 文档 JSON 由 Swashbuckle 生成，交互式 UI 由 Scalar 提供。
/// </summary>
/// <remarks>
/// 「生成文档」与「展示文档」在这里刻意拆成两件事：
/// <list type="bullet">
///   <item>生成仍用 Swashbuckle 的 SwaggerGen —— 本项目目标框架是 net8.0，
///         而 ASP.NET Core 内置的 OpenAPI 文档生成器（<c>AddOpenApi</c> / <c>MapOpenApi</c>）
///         要从 .NET 9 才有；</item>
///   <item>UI 换成 Scalar，替代 Swashbuckle 自带的 Swagger UI，对 OAuth2 授权码流程的调试更顺手。</item>
/// </list>
/// 因此项目引用的是 Swashbuckle.AspNetCore.SwaggerGen（只有生成器、不含 UI 静态资源），
/// 而不是完整的 Swashbuckle.AspNetCore。
/// </remarks>
public static class OpenApiExtensions
{
    /// <summary>文档名。它同时是 JSON 路径与 Scalar 路由的最后一段（<c>/scalar/v1</c>）。</summary>
    public const string DocumentName = "v1";

    /// <summary>Swashbuckle 的 <c>UseSwagger</c> 输出文档的路径模板，不带前导斜杠。</summary>
    public const string DocumentRouteTemplate = "openapi/{documentName}.json";

    /// <summary>Scalar 读取文档的路径模板。与 <see cref="DocumentRouteTemplate"/> 必须指向同一位置，仅前导斜杠不同。</summary>
    public const string ScalarDocumentRoutePattern = "/openapi/{documentName}.json";

    /// <summary>文档里 OAuth2 安全方案的名称，Swashbuckle 定义与 Scalar 引用用的都是它。</summary>
    private const string OAuth2SchemeName = "oauth2";

    /// <summary>
    /// 注册 OpenAPI 文档生成，并声明 OAuth2 的授权码与客户端凭证两种认证方案。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>原服务集合，便于链式调用。</returns>
    public static IServiceCollection AddAuthHubOpenApi(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();

        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc(DocumentName, new OpenApiInfo
            {
                Title = "AuthHub —— 统一认证授权中心",
                Version = "v1",
                Description = "基于 OpenIddict 5.x + ASP.NET Core Identity 的 OIDC 服务端。" +
                              "在文档里点 Authenticate 可直接走完整的授权码 + PKCE 流程（已预填示例 public 客户端），" +
                              "或用客户端凭证流程获取 M2M 令牌。"
            });

            options.AddSecurityDefinition(OAuth2SchemeName, BuildOAuth2SecurityScheme());

            // 默认按「需要 api:read」标注接口；具体权限由各控制器上的授权策略决定，
            // 这里只是让文档给出一个合理的默认提示。
            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = OAuth2SchemeName }
                    },
                    new[] { AuthHubConstants.Scopes.ApiRead }
                }
            });

            // 把 XML 注释（含控制器与 DTO 上的说明）写进文档。
            // 依赖 Directory.Build.props 里的 GenerateDocumentationFile=true。
            var xmlPath = Path.Combine(
                AppContext.BaseDirectory,
                $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
            if (File.Exists(xmlPath))
            {
                options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
            }

            // 关键：本行必须在 IncludeXmlComments **之后**。
            // OperationFilter 按注册顺序执行，放在最后才能让 [EndpointSummary] / [EndpointDescription]
            // 覆盖 XML 注释里的长文本，见 EndpointMetadataOperationFilter 的类型注释。
            options.OperationFilter<EndpointMetadataOperationFilter>();
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
        // 1) 文档 JSON —— 由 Swashbuckle 生成。
        //    路径改成 /openapi/v1.json 是为了对齐 Scalar 的默认约定（它默认就在这个位置找文档），
        //    省掉一处需要两边手工同步的配置。
        app.UseSwagger(options => options.RouteTemplate = DocumentRouteTemplate);

        // 2) 交互式 UI —— Scalar，默认挂在 /scalar/{documentName} 即 /scalar/v1。
        app.MapScalarApiReference(options =>
        {
            options
                .WithTitle("AuthHub —— 统一认证授权中心")
                .WithOpenApiRoutePattern(ScalarDocumentRoutePattern)
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
    /// 让 .NET 8 的 <c>[EndpointSummary]</c> / <c>[EndpointDescription]</c> 优先于 XML 注释。
    /// </summary>
    /// <remarks>
    /// 为什么需要它：Swashbuckle 先生成操作（此时把端点元数据里的 summary / description 写进
    /// <see cref="OpenApiOperation"/>），**再**依次执行 <c>IOperationFilter</c>；
    /// 而 <c>IncludeXmlComments</c> 注册的 XmlCommentsOperationFilter 会直接给
    /// <see cref="OpenApiOperation.Summary"/> 赋值。结果是：只要 action 上写了 <c>&lt;summary&gt;</c>，
    /// 特性标注就被静默覆盖，文档里显示的是面向代码维护者的那段长注释。
    ///
    /// 本过滤器注册在 <c>IncludeXmlComments</c> 之后（按注册顺序最后执行），
    /// 只把「确实标了特性」的端点覆盖回特性值；没标特性的端点仍沿用 XML 注释，
    /// 因此两种写法可以共存、逐端点选择。
    /// </remarks>
    private sealed class EndpointMetadataOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
            if (metadata is null)
            {
                return;
            }

            var summary = metadata.OfType<IEndpointSummaryMetadata>().LastOrDefault()?.Summary;
            if (!string.IsNullOrWhiteSpace(summary))
            {
                operation.Summary = summary;
            }

            var description = metadata.OfType<IEndpointDescriptionMetadata>().LastOrDefault()?.Description;
            if (!string.IsNullOrWhiteSpace(description))
            {
                operation.Description = description;
            }
        }
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
