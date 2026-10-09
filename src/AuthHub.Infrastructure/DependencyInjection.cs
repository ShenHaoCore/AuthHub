using AuthHub.Application.Interfaces;
using AuthHub.Domain.Interfaces;
using AuthHub.Infrastructure.Identity;
using AuthHub.Infrastructure.Notifications;
using AuthHub.Infrastructure.Repositories;
using AuthHub.Infrastructure.Security;
using AuthHub.Infrastructure.Services;
using AuthHub.Infrastructure.Time;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddScoped<IRoleAdminService, RoleAdminService>();
        services.AddScoped<IRolePermissionAdminService, RolePermissionAdminService>();
        services.AddScoped<IClientAdminService, OpenIddictClientAdminService>();
        services.AddScoped<IScopeAdminService, OpenIddictScopeAdminService>();
        services.AddScoped<ITokenAdminService, OpenIddictTokenAdminService>();
        services.AddScoped<IConsentService, OpenIddictConsentService>();

        return services;
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
