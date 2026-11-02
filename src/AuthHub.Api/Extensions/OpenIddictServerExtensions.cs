using AuthHub.Api.OpenIddict;
using AuthHub.Domain.Constants;
using AuthHub.Infrastructure.Data;
using OpenIddict.Server;

namespace AuthHub.Api.Extensions;

/// <summary>
/// OpenIddict 服务端与本地令牌校验的注册：核心（EF Core 存储）、服务端（端点与令牌策略）、
/// 校验（本进程内自校验，同一实例既签发又校验）。
/// </summary>
internal static class OpenIddictServerExtensions
{
    /// <summary>注册 OpenIddict，并返回签名 / 加密密钥的形态描述（供启动日志展示）。</summary>
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
                options.SetAuthorizationEndpointUris("/connect/authorize")
                       .SetTokenEndpointUris("/connect/token")
                       .SetEndSessionEndpointUris("/connect/logout")
                       .SetIntrospectionEndpointUris("/connect/introspect")
                       .SetRevocationEndpointUris("/connect/revoke")
                       .SetUserInfoEndpointUris("/connect/userinfo");

                options.AllowAuthorizationCodeFlow()
                       .AllowClientCredentialsFlow()
                       .AllowRefreshTokenFlow();

                if (enablePasswordFlow)
                {
                    // 资源所有者密码流程仅用于内部 / 迁移场景，生产默认关闭
                    options.AllowPasswordFlow();
                }

                // 授权码流程强制 PKCE（OAuth 2.1 要求，防授权码被拦截后直接兑换），且只接受 S256
                // （plain 已被弃用：校验值明文放在前端渠道，读到一次授权请求就拿到 verifier）。
                // CodeChallengeMethods 同时驱动授权请求校验与发现文档的 code_challenge_methods_supported；
                // 7.7.2 没有 SetCodeChallengeMethods，只能经 builder.Configure 改。
                options.RequireProofKeyForCodeExchange();

                options.Configure(server =>
                {
                    server.CodeChallengeMethods.Clear();
                    server.CodeChallengeMethods.Add("S256");
                });

                // 访问令牌只签名、不加密：统一认证中心的下游微服务要凭 jwks_uri 本地离线校验令牌，
                // 必须产出可被第三方读取的 JWS。若部署想隐藏令牌内容，移除这行并改用 introspection。
                options.DisableAccessTokenEncryption();

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

                // 资源标识（RFC 8707 Resource Indicators）：resource 参数会写进令牌 aud，
                // 但只认白名单里的值，未登记的一律 invalid_target（防 confused deputy，MCP 授权规范列为 MUST）。
                //
                // 白名单已库表化（RegisteredResources 表）：内置 ValidateResources handler 对照的是
                // OpenIddictServerOptions.Resources（启动快照，7.7.2 无运行时增删接口），故精确移除，
                // 换成 RegisteredResourceValidationHandlers 的读库实现 —— 管理 API 增删后下一请求即生效。
                // 默认拒绝的红线不变：表为空时任何 resource 参数都被 invalid_target 拒绝。
                //
                // authhub-api 不在白名单：RFC 8707 要求绝对 URI，登记时逐个 new Uri() 会直接抛；
                // 它作为 aud 仍正常工作 —— 由 Scope 的 Resources 带出（AuthHubSeeder），走另一条路径。
                // AuthHub:Resources 配置仍是启动期种子来源与 fail-fast 校验点（见 ResourceSeeder / AuthHubResourceRegistry）。
                options.RemoveEventHandler(OpenIddictServerHandlers.Exchange.ValidateResources.Descriptor);
                options.RemoveEventHandler(OpenIddictServerHandlers.Authentication.ValidateResources.Descriptor);

                foreach (var descriptor in RegisteredResourceValidationHandlers.Descriptors)
                {
                    options.AddEventHandler(descriptor);
                }

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
                       .EnableEndSessionEndpointPassthrough()
                       .EnableUserInfoEndpointPassthrough()
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
