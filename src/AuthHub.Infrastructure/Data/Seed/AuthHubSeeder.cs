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
/// 开发环境种子数据：内置角色、Scope、演示客户端、演示账号。
///
/// 设计原则：
/// - 幂等：每一项都先查后建，重复启动不会产生脏数据；
/// - 密码/密钥全部可被配置覆盖（AuthHub:Seed:*），避免把生产口令写死在代码里；
/// - 只在 Development 调用（见 Program.cs），生产环境应通过管理 API 创建客户端。
/// </summary>
public static class AuthHubSeeder
{
    public static async Task SeedAsync(
        IServiceProvider services,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("AuthHub.Seeder");
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
