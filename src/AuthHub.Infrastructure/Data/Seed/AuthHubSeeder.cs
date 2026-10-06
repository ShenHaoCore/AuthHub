using AuthHub.Domain.Constants;
using AuthHub.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using Oidc = OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthHub.Infrastructure.Data.Seed;

/// <summary>
/// 种子数据：内置角色、Scope、演示客户端、演示账号。
///
/// 设计原则：
/// - 幂等：每一项都先查后建，重复启动不会产生脏数据；
/// - 密码/密钥全部可被配置覆盖（<c>AuthHub:Seed:*</c>），避免把生产口令写死在代码里；
/// - **由配置开关控制，而不是按环境硬编码**：<c>AuthHub:Seeding:Enabled</c> 决定要不要跑；
///   生产环境还需再显式设置 <c>AuthHub:Seeding:AllowInProduction=true</c>，
///   否则即使 Enabled=true 也会被跳过并打警告 —— 理由见 <see cref="Decide"/>。
/// </summary>
public static class AuthHubSeeder
{
    /// <summary>
    /// 播种是否放行的判定结果。<see cref="SkipReason"/> 在放行时为 <c>null</c>。
    /// </summary>
    /// <param name="Allowed">是否允许播种。</param>
    /// <param name="SkipReason">被跳过时给日志的说明。</param>
    public readonly record struct SeedDecision(bool Allowed, string? SkipReason);

    /// <summary>
    /// 判定当前配置是否允许播种。**刻意做成纯函数**：它是"防误开"的安全护栏，
    /// 决策逻辑必须能脱离数据库与宿主被直接测试。
    ///
    /// <para><b>为什么需要生产期二次确认</b>：种子数据里有演示账号
    /// （<c>alice</c>，密码写在源码常量里）与演示客户端密钥，它们的存在是为了让人
    /// clone 下来就能登录试用。一旦在生产上跑起来，等于留下几个公开密码的可用账号，
    /// 而症状（多了两个用户）非常容易被忽略。因此这里选择**默认拒绝**：
    /// 生产环境即使 <c>Seeding:Enabled=true</c> 也要再设一次
    /// <c>Seeding:AllowInProduction=true</c> 才会执行，把"误开"与"确实要初始化"区分开。</para>
    /// </summary>
    public static SeedDecision Decide(IConfiguration configuration, bool isProduction)
    {
        var allowInProduction = configuration.GetValue("AuthHub:Seeding:AllowInProduction", false);

        if (!isProduction || allowInProduction)
        {
            return new SeedDecision(true, null);
        }

        return new SeedDecision(false,
            "已请求播种（AuthHub:Seeding:Enabled=true），但当前是生产环境且未显式设置 " +
            "AuthHub:Seeding:AllowInProduction=true —— 已跳过。这是一道防误开护栏：种子数据包含演示账号 " +
            $"{AuthHubConstants.SeedUsers.AdminUserName} / {AuthHubConstants.SeedUsers.DemoUserName} " +
            "与写在源码里的演示客户端密钥，在生产上出现等同留下几个公开口令的可用账号。" +
            "若确实需要初始化（例如建第一个管理员），请显式设置 AuthHub:Seeding:AllowInProduction=true，" +
            "并在初始化完成后立即改回 false。");
    }

    public static async Task SeedAsync(
        IServiceProvider services,
        IConfiguration configuration,
        bool isProduction,
        CancellationToken cancellationToken = default)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("AuthHub.Seeder");

        var decision = Decide(configuration, isProduction);
        if (!decision.Allowed)
        {
            logger.LogWarning("{Reason}", decision.SkipReason);
            return;
        }

        var section = configuration.GetSection("AuthHub:Seed");

        // 配置里常把占位项写成空字符串，因此这里按“空白即回退”处理，而不是 ?? 判空
        var adminPassword = ValueOrDefault(section["AdminPassword"], AuthHubConstants.SeedUsers.AdminPassword);
        var demoPassword = ValueOrDefault(section["DemoPassword"], AuthHubConstants.SeedUsers.DemoPassword);
        var webClientSecret = ValueOrDefault(section["WebClientSecret"], AuthHubConstants.SeedClients.WebSecret);
        var m2mClientSecret = ValueOrDefault(section["M2mClientSecret"], AuthHubConstants.SeedClients.M2MSecret);

        await SeedRolesAsync(services, logger, cancellationToken);
        await SeedScopesAsync(services, logger, cancellationToken);
        await SeedClientsAsync(services, logger, webClientSecret, m2mClientSecret, cancellationToken);
        await SeedUsersAsync(services, logger, adminPassword, demoPassword, cancellationToken);

        logger.LogInformation("种子数据初始化完成。");
    }

    // ------------------------------------------------------------------ 角色

    private static string ValueOrDefault(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static async Task SeedRolesAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        var roleManager = services.GetRequiredService<RoleManager<ApplicationRole>>();

        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AuthHubConstants.Roles.Administrator] = "系统管理员：拥有全部管理权限",
            [AuthHubConstants.Roles.UserManager] = "用户管理员：管理用户与强制下线",
            [AuthHubConstants.Roles.Auditor] = "审计员：只读访问审计日志",
            [AuthHubConstants.Roles.User] = "普通用户：仅能登录与使用已授权应用"
        };

        foreach (var roleName in AuthHubConstants.Roles.All)
        {
            if (await roleManager.FindByNameAsync(roleName) is not null)
            {
                continue;
            }

            var result = await roleManager.CreateAsync(new ApplicationRole(roleName)
            {
                Description = descriptions.GetValueOrDefault(roleName),
                IsSystemRole = true
            });

            if (result.Succeeded)
            {
                logger.LogInformation("已创建角色 {Role}", roleName);
            }
            else
            {
                logger.LogWarning("创建角色 {Role} 失败：{Errors}", roleName,
                    string.Join("; ", result.Errors.Select(e => e.Description)));
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    // ------------------------------------------------------------------ Scope

    private static async Task SeedScopesAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        var scopeManager = services.GetRequiredService<IOpenIddictScopeManager>();

        var definitions = new (string Name, string DisplayName, string Description, string[] Resources)[]
        {
            (AuthHubConstants.Scopes.Profile, "用户档案", "读取你的基础档案信息（姓名、显示名）", Array.Empty<string>()),
            (AuthHubConstants.Scopes.Email, "邮箱", "读取你的邮箱地址", Array.Empty<string>()),
            (AuthHubConstants.Scopes.Roles, "角色", "读取你的角色信息", Array.Empty<string>()),
            (AuthHubConstants.Scopes.ApiRead, "API 只读", "以只读方式访问 AuthHub 业务 API", new[] { AuthHubConstants.Resources.Api }),
            (AuthHubConstants.Scopes.ApiWrite, "API 读写", "以读写方式访问 AuthHub 业务 API", new[] { AuthHubConstants.Resources.Api }),
            (AuthHubConstants.Scopes.Admin, "AuthHub 管理", "访问 AuthHub 管理接口", new[] { AuthHubConstants.Resources.Api })
        };

        foreach (var (name, displayName, description, resources) in definitions)
        {
            if (await scopeManager.FindByNameAsync(name, cancellationToken) is not null)
            {
                continue;
            }

            var descriptor = new OpenIddictScopeDescriptor
            {
                Name = name,
                DisplayName = displayName,
                Description = description
            };

            foreach (var resource in resources)
            {
                descriptor.Resources.Add(resource);
            }

            await scopeManager.CreateAsync(descriptor, cancellationToken);
            logger.LogInformation("已创建 Scope {Scope}", name);
        }
    }

    // ------------------------------------------------------------------ 客户端

    private static async Task SeedClientsAsync(
        IServiceProvider services,
        ILogger logger,
        string webClientSecret,
        string m2mClientSecret,
        CancellationToken cancellationToken)
    {
        var applicationManager = services.GetRequiredService<IOpenIddictApplicationManager>();

        // 1) SPA：public 客户端，不持有密钥，安全边界完全依赖 PKCE
        await CreateClientIfMissingAsync(
            applicationManager,
            logger,
            new OpenIddictApplicationDescriptor
            {
                ClientId = AuthHubConstants.SeedClients.Spa,
                DisplayName = "SPA 前端应用（示例）",
                ClientType = Oidc.ClientTypes.Public,
                ApplicationType = Oidc.ApplicationTypes.Native,
                ConsentType = Oidc.ConsentTypes.Explicit,
                RedirectUris = { new Uri("https://localhost:3000/callback") },
                PostLogoutRedirectUris = { new Uri("https://localhost:3000/") },
                Permissions =
                {
                    Oidc.Permissions.Endpoints.Authorization,
                    Oidc.Permissions.Endpoints.Token,
                    Oidc.Permissions.Endpoints.Logout,
                    Oidc.Permissions.GrantTypes.AuthorizationCode,
                    Oidc.Permissions.GrantTypes.RefreshToken,
                    Oidc.Permissions.ResponseTypes.Code,
                    Oidc.Permissions.Prefixes.Scope + AuthHubConstants.Scopes.OpenId,
                    Oidc.Permissions.Prefixes.Scope + AuthHubConstants.Scopes.Profile,
                    Oidc.Permissions.Prefixes.Scope + AuthHubConstants.Scopes.Email,
                    Oidc.Permissions.Prefixes.Scope + AuthHubConstants.Scopes.Roles,
                    Oidc.Permissions.Prefixes.Scope + AuthHubConstants.Scopes.OfflineAccess,
                    Oidc.Permissions.Prefixes.Scope + AuthHubConstants.Scopes.ApiRead
                },
                Requirements = { Oidc.Requirements.Features.ProofKeyForCodeExchange }
            },
            cancellationToken);

        // 2) 服务端 Web 应用：confidential，持密钥
        await CreateClientIfMissingAsync(
            applicationManager,
            logger,
            new OpenIddictApplicationDescriptor
            {
                ClientId = AuthHubConstants.SeedClients.Web,
                DisplayName = "服务端 Web 应用（示例）",
                ClientType = Oidc.ClientTypes.Confidential,
                ApplicationType = Oidc.ApplicationTypes.Web,
                ConsentType = Oidc.ConsentTypes.Implicit,
                ClientSecret = webClientSecret,
                RedirectUris = { new Uri("https://localhost:5002/signin-oidc") },
                PostLogoutRedirectUris = { new Uri("https://localhost:5002/signout-callback-oidc") },
                Permissions =
                {
                    Oidc.Permissions.Endpoints.Authorization,
                    Oidc.Permissions.Endpoints.Token,
                    Oidc.Permissions.Endpoints.Logout,
                    Oidc.Permissions.GrantTypes.AuthorizationCode,
                    Oidc.Permissions.GrantTypes.RefreshToken,
                    Oidc.Permissions.ResponseTypes.Code,
                    Oidc.Permissions.Prefixes.Scope + AuthHubConstants.Scopes.OpenId,
                    Oidc.Permissions.Prefixes.Scope + AuthHubConstants.Scopes.Profile,
                    Oidc.Permissions.Prefixes.Scope + AuthHubConstants.Scopes.Email,
                    Oidc.Permissions.Prefixes.Scope + AuthHubConstants.Scopes.Roles,
                    Oidc.Permissions.Prefixes.Scope + AuthHubConstants.Scopes.OfflineAccess,
                    Oidc.Permissions.Prefixes.Scope + AuthHubConstants.Scopes.ApiRead,
                    Oidc.Permissions.Prefixes.Scope + AuthHubConstants.Scopes.ApiWrite
                },
                Requirements = { Oidc.Requirements.Features.ProofKeyForCodeExchange }
            },
            cancellationToken);

        // 3) 机器对机器：仅客户端凭证流程
        await CreateClientIfMissingAsync(
            applicationManager,
            logger,
            new OpenIddictApplicationDescriptor
            {
                ClientId = AuthHubConstants.SeedClients.M2M,
                DisplayName = "后端服务（M2M，示例）",
                ClientType = Oidc.ClientTypes.Confidential,
                ConsentType = Oidc.ConsentTypes.Implicit,
                ClientSecret = m2mClientSecret,
                Permissions =
                {
                    Oidc.Permissions.Endpoints.Token,
                    Oidc.Permissions.GrantTypes.ClientCredentials,
                    Oidc.Permissions.Prefixes.Scope + AuthHubConstants.Scopes.ApiRead,
                    Oidc.Permissions.Prefixes.Scope + AuthHubConstants.Scopes.ApiWrite
                }
            },
            cancellationToken);
    }

    private static async Task CreateClientIfMissingAsync(
        IOpenIddictApplicationManager applicationManager,
        ILogger logger,
        OpenIddictApplicationDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (await applicationManager.FindByClientIdAsync(descriptor.ClientId!, cancellationToken) is not null)
        {
            return;
        }

        await applicationManager.CreateAsync(descriptor, cancellationToken);

        logger.LogInformation(
            "已创建示例客户端 {ClientId}（{ClientType}）",
            descriptor.ClientId,
            descriptor.ClientType ?? "unspecified");
    }

    // ------------------------------------------------------------------ 用户

    private static async Task SeedUsersAsync(
        IServiceProvider services,
        ILogger logger,
        string adminPassword,
        string demoPassword,
        CancellationToken cancellationToken)
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

        await CreateUserIfMissingAsync(
            userManager,
            logger,
            AuthHubConstants.SeedUsers.AdminUserName,
            AuthHubConstants.SeedUsers.AdminEmail,
            "系统管理员",
            adminPassword,
            new[] { AuthHubConstants.Roles.Administrator },
            cancellationToken);

        await CreateUserIfMissingAsync(
            userManager,
            logger,
            AuthHubConstants.SeedUsers.DemoUserName,
            AuthHubConstants.SeedUsers.DemoEmail,
            "演示用户 Alice",
            demoPassword,
            new[] { AuthHubConstants.Roles.User },
            cancellationToken);
    }

    private static async Task CreateUserIfMissingAsync(
        UserManager<ApplicationUser> userManager,
        ILogger logger,
        string userName,
        string email,
        string displayName,
        string password,
        string[] roles,
        CancellationToken cancellationToken)
    {
        if (await userManager.FindByNameAsync(userName) is not null)
        {
            return;
        }

        var user = new ApplicationUser
        {
            UserName = userName,
            Email = email,
            DisplayName = displayName,
            EmailConfirmed = true,
            IsActive = true
        };

        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            logger.LogWarning("创建种子用户 {UserName} 失败：{Errors}", userName,
                string.Join("; ", result.Errors.Select(e => e.Description)));
            return;
        }

        if (roles.Length > 0)
        {
            await userManager.AddToRolesAsync(user, roles);
        }

        logger.LogInformation(
            "已创建种子用户 {UserName} / 角色 {Roles}（开发环境初始密码见配置 AuthHub:Seed:*，请在生产前修改）",
            userName,
            string.Join(",", roles));
    }
}
