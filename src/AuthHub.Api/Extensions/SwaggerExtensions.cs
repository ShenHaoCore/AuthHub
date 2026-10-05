using System.Reflection;
using AuthHub.Domain.Constants;
using Microsoft.OpenApi.Models;

namespace AuthHub.Api.Extensions;

/// <summary>Swagger / OpenAPI 配置，含 OAuth2 授权码与客户端凭证两种调试入口。</summary>
public static class SwaggerExtensions
{
    public static IServiceCollection AddAuthHubSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();

        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "AuthHub —— 统一认证授权中心",
                Version = "v1",
                Description = "基于 OpenIddict 5.x + ASP.NET Core Identity 的 OIDC 服务端。" +
                              "点击右上角 Authorize 可直接在文档里走完整的授权码 + PKCE 流程，或用客户端凭证流程获取 M2M 令牌。"
            });

            var scheme = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.OAuth2,
                Flows = new OpenApiOAuthFlows
                {
                    AuthorizationCode = new OpenApiOAuthFlow
                    {
                        // 注意：这里用相对地址，Swagger UI 会基于当前站点补全，
                        // 因此不管从 https://localhost:5001 还是其他主机访问都能正确跳转。
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

            options.AddSecurityDefinition("oauth2", scheme);

            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "oauth2" }
                    },
                    new[] { AuthHubConstants.Scopes.ApiRead }
                }
            });

            var xmlPath = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
            if (File.Exists(xmlPath))
            {
                options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
            }
        });

        return services;
    }
}
