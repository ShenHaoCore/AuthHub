using AuthHub.Application.Interfaces;
using AuthHub.Application.Options;
using AuthHub.Domain.Interfaces;
using AuthHub.Infrastructure.Caching;
using AuthHub.Infrastructure.Identity;
using AuthHub.Infrastructure.Notifications;
using AuthHub.Infrastructure.Repositories;
using AuthHub.Infrastructure.Security;
using AuthHub.Infrastructure.Services;
using AuthHub.Infrastructure.Time;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;
using StackExchange.Redis;

namespace AuthHub.Infrastructure;

/// <summary>
/// Infrastructure 层 DI 注册入口。
/// DbContext / Identity / OpenIddict 的注册放在 Program.cs（应用启动期横切配置），
/// 这里只注册"实现了 Application 层接口"的基础设施组件。
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // 时间抽象
        services.AddSingleton<IClock, SystemClock>();

        // 仓储
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();

        // 身份相关
        services.AddScoped<IUserClaimsPrincipalFactory<Domain.Entities.ApplicationUser>, ApplicationUserClaimsPrincipalFactory>();
        services.AddSingleton<ITwoFactorTicketProtector, DataProtectionTwoFactorTicketProtector>();

        // 通知通道：邮件 logging / smtp / ethereal-auto（仅 Development）；短信 logging / http。
        RegisterEmailSender(services, configuration, environment);
        RegisterSmsSender(services, configuration, environment);

        // 管理服务实现（OpenIddict 驱动的部分）
        services.AddScoped<IRegisteredResourceStore, RegisteredResourceStore>();
        services.AddScoped<IResourceAdminService, ResourceAdminService>();
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddScoped<IRoleAdminService, RoleAdminService>();
        services.AddScoped<IRolePermissionAdminService, RolePermissionAdminService>();
        services.AddScoped<IClientAdminService, OpenIddictClientAdminService>();
        services.AddScoped<IScopeAdminService, OpenIddictScopeAdminService>();
        services.AddScoped<ITokenAdminService, OpenIddictTokenAdminService>();
        services.AddScoped<IConsentService, OpenIddictConsentService>();

        // CIMD 动态客户端注册（AuthHub:Cimd，默认关闭）。
        //
        // 拉取器是单例：它持有 SocketsHttpHandler（连接池、ConnectCallback 里的地址策略），
        // 每个请求新建一个等于关掉连接复用，也让"同一域名连续拉取"变成反复 DNS + TLS 握手。
        services.Configure<CimdOptions>(configuration.GetSection(CimdOptions.SectionName));
        services.AddSingleton<ICimdMetadataFetcher, CimdMetadataFetcher>();
        services.AddScoped<ICimdClientRegistrar, CimdClientRegistrar>();

        // Redis 令牌条目缓存（AuthHub:Redis，默认关闭）。
        //
        // 本方法在 Program.cs 里排在 AddAuthHubOpenIddict 之后执行，而装饰器必须注册在
        // OpenIddict 的 EF Core 存储之后（MS DI 单解析取"最后注册者"），顺序依赖成立。
        RegisterRedisTokenCache(services, configuration);

        return services;
    }

    private static void RegisterRedisTokenCache(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RedisOptions>(configuration.GetSection(RedisOptions.SectionName));

        if (!configuration.GetValue($"{RedisOptions.SectionName}:Enabled", false))
        {
            return;
        }

        // 连接串没配不在这里报错：默认值（本机回环） + abortConnect=false 意味着
        // "Redis 不存在"只表现为缓存全降级（直查库），启动与认证都不受影响。
        // 真正的配置错误（比如指向了内网地址）由运维在部署清单里把关。
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<RedisOptions>>().CurrentValue;
            return ConnectionMultiplexer.Connect(options.ConnectionString);
        });

        // 装饰器包住"当前注册的那个令牌存储"（UseEntityFrameworkCore 注册的 EF Core 实现）。
        // 捕获既有描述符的实现类型而不是硬编码 EF 存储的泛型形状，OpenIddict 升级时这里不用动。
        var innerStore = services.FirstOrDefault(descriptor =>
                descriptor.ServiceType == typeof(IOpenIddictTokenStore<OpenIddictEntityFrameworkCoreToken>))
            ?? throw new InvalidOperationException(
                "Redis 令牌缓存要求先注册 OpenIddict 的令牌存储（AddAuthHubOpenIddict 必须先于 AddInfrastructure 执行，" +
                "或确认 UseEntityFrameworkCore() 未被移除）。");

        services.Add(ServiceDescriptor.Describe(
            typeof(IOpenIddictTokenStore<OpenIddictEntityFrameworkCoreToken>),
            sp => new RedisCachedTokenStore(
                (IOpenIddictTokenStore<OpenIddictEntityFrameworkCoreToken>)ActivatorUtilities.CreateInstance(
                    sp, innerStore.ImplementationType!),
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<IOpenIddictEntityFrameworkCoreContext>(),
                sp.GetRequiredService<IOptionsMonitor<RedisOptions>>(),
                sp.GetRequiredService<ILogger<RedisCachedTokenStore>>()),
            ServiceLifetime.Scoped));
    }

    private static void RegisterEmailSender(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var emailProvider = configuration.GetValue<string>($"{EmailOptions.SectionName}:Provider") ?? "logging";
        var wantsEthereal = emailProvider.Equals("ethereal-auto", StringComparison.OrdinalIgnoreCase)
                            || emailProvider.Equals("ethereal", StringComparison.OrdinalIgnoreCase);

        if (wantsEthereal && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "AuthHub:Email:Provider=ethereal-auto（或 ethereal）仅允许在 Development 环境使用，" +
                "避免把真实用户邮件发到公共测试服务。请改为 logging 或 smtp。");
        }

        if (emailProvider.Equals("smtp", StringComparison.OrdinalIgnoreCase))
        {
            services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }
        else if (wantsEthereal)
        {
            services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
            services.AddSingleton<IEmailSender, EtherealEmailSender>();
        }
        else
        {
            services.AddSingleton<IEmailSender, LoggingEmailSender>();
        }
    }

    private static void RegisterSmsSender(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var smsProvider = configuration.GetValue<string>($"{SmsOptions.SectionName}:Provider") ?? "logging";
        if (smsProvider.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            services.Configure<SmsOptions>(configuration.GetSection(SmsOptions.SectionName));
            var timeoutSeconds = configuration.GetValue($"{SmsOptions.SectionName}:TimeoutSeconds", 30);
            services.AddHttpClient(HttpSmsSender.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 120));
            });
            services.AddSingleton<ISmsSender, HttpSmsSender>();
            return;
        }

        // Development 默认走内存收件箱（可打开 /dev/sms-inbox）；其它环境仍只写日志
        if (environment.IsDevelopment())
        {
            services.AddSingleton<SmsDevInbox>();
            services.AddSingleton<ISmsSender, DevInboxSmsSender>();
            return;
        }

        services.AddSingleton<ISmsSender, LoggingSmsSender>();
    }
}
