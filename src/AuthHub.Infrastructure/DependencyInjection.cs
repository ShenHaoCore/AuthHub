using AuthHub.Application.Interfaces;
using AuthHub.Domain.Interfaces;
using AuthHub.Infrastructure.Identity;
using AuthHub.Infrastructure.Notifications;
using AuthHub.Infrastructure.Repositories;
using AuthHub.Infrastructure.Security;
using AuthHub.Infrastructure.Services;
using AuthHub.Infrastructure.Time;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace AuthHub.Infrastructure;

/// <summary>
/// Infrastructure 层 DI 注册入口。
/// DbContext / Identity / OpenIddict 的注册放在 Program.cs（应用启动期横切配置），
/// 这里只注册“实现了 Application 层接口”的基础设施组件。
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        // 时间抽象
        services.AddSingleton<IClock, SystemClock>();

        // 仓储
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();

        // 身份相关
        services.AddScoped<IUserClaimsPrincipalFactory<Domain.Entities.ApplicationUser>, ApplicationUserClaimsPrincipalFactory>();
        services.AddSingleton<ITwoFactorTicketProtector, DataProtectionTwoFactorTicketProtector>();

        // 通知通道（默认写日志，接入真实服务时替换）
        services.AddSingleton<IEmailSender, LoggingEmailSender>();
        services.AddSingleton<ISmsSender, LoggingSmsSender>();

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
}
