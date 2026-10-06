using System.Reflection;
using AuthHub.Application.Interfaces;
using AuthHub.Application.Services;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace AuthHub.Application;

/// <summary>Application 层 DI 注册入口。</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();

        // 注册程序集内全部 FluentValidation 验证器
        services.AddValidatorsFromAssembly(assembly);

        // 业务流程服务（只依赖 Identity 抽象与仓储抽象）
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IAuditLogService, AuditLogService>();

        // MFA 的下发通道。AccountService 只认 ITwoFactorChannel 集合，
        // 加通道 = 加一行注册，业务类不动（通道名必须与 Identity 的 provider 名一致）。
        services.AddScoped<ITwoFactorChannel, EmailTwoFactorChannel>();
        services.AddScoped<ITwoFactorChannel, PhoneTwoFactorChannel>();

        return services;
    }
}
