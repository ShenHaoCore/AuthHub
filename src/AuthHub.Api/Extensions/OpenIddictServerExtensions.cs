using AuthHub.Domain.Constants;
using AuthHub.Infrastructure.Data;

namespace AuthHub.Api.Extensions;

/// <summary>
/// OpenIddict 服务端与本地令牌校验的注册。
///
/// 分三块：核心（客户端 / 授权 / 令牌 / Scope 存到 EF Core）、服务端（各类端点与令牌策略）、
/// 校验（本进程内自校验，同一实例既签发又校验）。
/// </summary>
internal static class OpenIddictServerExtensions
{
    /// <summary>
    /// 注册 OpenIddict，并返回签名 / 加密密钥的形态描述（供启动日志展示）。
    ///
    /// 返回值来自 <see cref="OpenIddictKeySetup.ConfigureKeys"/> —— 它的配置委托是**同步执行**的，
    /// 因此到这里时描述已经回填完毕。
    /// </summary>
    public static string AddAuthHubOpenIddict(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var requireHttps = configuration.GetValue("AuthHub:Security:RequireHttps", true);
        var enablePasswordFlow = configuration.GetValue("AuthHub:Features:EnablePasswordFlow", false);
        var keyDescription = "未初始化";

        services.AddOpenIddict()

            // 5.1 核心：把客户端 / 授权 / 令牌 / Scope 存到 EF Core
            .AddCore(options =>
            {
                options.UseEntityFrameworkCore()
                       .UseDbContext<AuthHubDbContext>();
            })

            // 5.2 服务端
            .AddServer(options =>
            {
                // 端点
                options.SetAuthorizationEndpointUris("/connect/authorize")
                       .SetTokenEndpointUris("/connect/token")
                       .SetLogoutEndpointUris("/connect/logout")
                       .SetIntrospectionEndpointUris("/connect/introspect")
                       .SetRevocationEndpointUris("/connect/revoke")
                       .SetUserinfoEndpointUris("/connect/userinfo");

                // 授权类型
                options.AllowAuthorizationCodeFlow()
                       .AllowClientCredentialsFlow()
                       .AllowRefreshTokenFlow();

                if (enablePasswordFlow)
                {
                    // 资源所有者密码流程仅用于内部 / 迁移场景，生产默认关闭
                    options.AllowPasswordFlow();
                }

                // 授权码流程强制 PKCE（OAuth 2.1 要求，防授权码被拦截后直接兑换）
                //
                // 注意：OpenIddict 5.x 没有 builder API 能限制 code_challenge_method，
                // 因此发现文档里会同时声明 plain 与 S256。plain（code_challenge == code_verifier）
                // 在 OAuth 2.1 里已被弃用——它把校验值明文放在前端渠道，攻击者只要读到授权请求
                // 就拿到了 verifier。本项目所有示例客户端都用 S256；要彻底禁掉 plain 需要
                // 自定义 ValidateAuthorizationRequestContext 事件处理器并替换发现文档处理器，
                // 见 README「待办与扩展方向」。
                options.RequireProofKeyForCodeExchange();

                // 访问令牌只签名、不加密。
                //
                // OpenIddict 默认会用服务端加密证书把 Access Token 加密成 JWE，这适合“只有本服务
                // 能读懂令牌”的场景；但本项目的定位是统一认证中心，下游微服务需要凭 jwks_uri
                // 在本地离线校验令牌（见第十章“分布式令牌验证 JWKS”），因此必须产出可被第三方
                // 读取的 JWS。若某些部署希望隐藏令牌内容，请移除这一行并改用 introspection。
                options.DisableAccessTokenEncryption();

                // 令牌生命周期
                options.SetAccessTokenLifetime(TimeSpan.FromHours(1));
                options.SetIdentityTokenLifetime(TimeSpan.FromMinutes(30));
                options.SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(5));
                options.SetRefreshTokenLifetime(TimeSpan.FromDays(30));

                // 刷新令牌旋转：旧令牌立即失效（宽限期 0），被盗后也只用得了一次
                options.SetRefreshTokenReuseLeeway(TimeSpan.Zero);

                // 自定义声明与 Scope 必须注册，否则会被 OpenIddict 过滤掉
                options.RegisterClaims(
                    AuthHubConstants.ClaimTypes.Permission,
                    AuthHubConstants.ClaimTypes.DisplayName);

                options.RegisterScopes(
                    AuthHubConstants.Scopes.ApiRead,
                    AuthHubConstants.Scopes.ApiWrite,
                    AuthHubConstants.Scopes.Admin);

                // 显式指定 issuer，保证令牌 iss 与发现文档在反向代理后依然正确
                var issuer = configuration["AuthHub:Issuer"];
                if (!string.IsNullOrWhiteSpace(issuer))
                {
                    options.SetIssuer(issuer);
                }

                // 签名 / 加密密钥（生产：持久化证书；开发：开发证书；CI：临时密钥）
                keyDescription = options.ConfigureKeys(configuration, environment);

                var aspNetCore = options.UseAspNetCore()
                       .EnableAuthorizationEndpointPassthrough()
                       .EnableTokenEndpointPassthrough()
                       .EnableLogoutEndpointPassthrough()
                       .EnableUserinfoEndpointPassthrough()
                       .EnableStatusCodePagesIntegration();

                if (!requireHttps)
                {
                    // 本地 http 调试 / 集成测试：关闭“端点必须走 TLS”的强制要求
                    aspNetCore.DisableTransportSecurityRequirement();
                }
            })

            // 5.3 校验：本进程内自校验（同一实例既签发又校验）
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();

                // 每次校验都检查令牌是否已被撤销：
                // 这让“管理员强制下线”对尚未过期的 JWT 也立即生效（即令牌黑名单）
                options.EnableTokenEntryValidation();
            });

        return keyDescription;
    }
}
