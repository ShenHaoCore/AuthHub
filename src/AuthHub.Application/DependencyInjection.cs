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

        // DTO / Entity 映射
        services.AddAutoMapper(assembly);

        // 注册程序集内全部 FluentValidation 验证器
        services.AddValidatorsFromAssembly(assembly);

        // 业务流程服务（只依赖 Identity 抽象与仓储抽象）
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IAuditLogService, AuditLogService>();

        return services;
    }
}
