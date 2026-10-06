using AuthHub.Application.Interfaces;
using AuthHub.Application.Options;
using AuthHub.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace AuthHub.Api.Extensions;

/// <summary>
/// 「角色 → 权限」归属的装配。
///
/// 这是授权分层里**唯一允许运行时改动**的一层：
///   能力目录（<c>AuthHubConstants.Permissions</c>）与策略名（<c>AuthHubConstants.Policies</c>）
///     → 编译期常量，端点上那道 [Authorize] 是程序集的一部分；
///   角色归属 → 配置基线 + 数据库覆盖（后台界面可直接勾选），改完即时生效，不必重启更不必发版；
///   用户与角色的绑定 → 数据库，后台界面可直接改。
///
/// 配置的绑定语义是"覆盖"而非"替换"：配置里出现过的角色覆盖出厂默认，没出现过的沿用默认。
/// 于是老部署的 appsettings 里没有这一段时，行为与升级前逐字一致。
/// 数据库那一层再由 <see cref="IRolePermissionAdminService"/> 写入，优先级高于配置。
/// </summary>
internal static class RolePermissionExtensions
{
    public static IServiceCollection AddAuthHubRolePermissions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RolePermissionOptions>(options =>
        {
            foreach (var roleSection in configuration.GetSection(RolePermissionOptions.SectionName).GetChildren())
            {
                // "X": [] 是有效写法：显式收回该角色的全部权限
                options.Roles[roleSection.Key] = roleSection.Get<string[]>() ?? Array.Empty<string>();
            }
        });

        // 校验器挂在 IOptions 的校验链上：IOptions<RolePermissionOptions>.Value 首次求值即执行。
        // 首次求值由启动期自检强制触发（见 WebApplicationExtensions.ValidateAuthHubStartupConfiguration），
        // 因此配置写错时进程**直接起不来**，而不是等某个管理员发现自己的菜单少了。
        services.AddSingleton<IValidateOptions<RolePermissionOptions>, RolePermissionOptionsValidator>();

        // 单例：生效表在进程内缓存，且处在每次登录生成声明的热路径上。
        // 它**懒加载**运行时覆盖 —— 单例可能先于数据库初始化被解析（启动期自检就跑在迁移之前）。
        services.AddSingleton<IRolePermissionOverrideStore, EfRolePermissionOverrideStore>();
        services.AddSingleton<IRolePermissionMap, LayeredRolePermissionMap>();

        return services;
    }
}
