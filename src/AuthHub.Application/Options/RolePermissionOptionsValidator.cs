using AuthHub.Domain.Constants;
using Microsoft.Extensions.Options;

namespace AuthHub.Application.Options;

/// <summary>
/// 权限归属配置的**启动期**校验，由 RolePermissionExtensions 以 ValidateOnStart 挂上。
///
/// 它拦的是"配了但没生效"这一类故障：权限名拼错、大小写不符、指向一个尚未声明的权限点。
/// 这三种情况都不会当场报错，只会让某个角色静默地拿不到权限 ——
/// 现象是菜单消失或接口 403，而排查方向通常会被带偏到策略注册或缓存上。
/// 启动即失败比事后沿着授权链条查要便宜得多，这也是 AWS IAM Access Analyzer、
/// GCP 权限目录校验等同类系统一致的做法。
///
/// 刻意**不**校验的两件事：
///   1) 角色名是否属于内置角色 —— 自定义角色本来就允许配权限；
///   2) 是否存在"没有任何角色拥有"的权限点 —— 那属于权限设计的取舍，不是配置错误。
/// </summary>
public sealed class RolePermissionOptionsValidator : IValidateOptions<RolePermissionOptions>
{
    public ValidateOptionsResult Validate(string? name, RolePermissionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        foreach (var (role, permissions) in options.Roles)
        {
            if (string.IsNullOrWhiteSpace(role))
            {
                failures.Add("存在空的角色名。");
                continue;
            }

            foreach (var permission in permissions)
            {
                if (string.IsNullOrWhiteSpace(permission))
                {
                    failures.Add($"角色 {role} 的权限列表里存在空值。");
                    continue;
                }

                // 严格区分大小写：权限名会原样写进 authhub:permission 声明，
                // 而策略比对的是一律小写的常量值。写成 Users.Manage 不会报错，
                // 只会让拥有该角色的用户静默地过不了授权 —— 正是这条校验的价值所在。
                if (!AuthHubConstants.Permissions.All.Contains(permission, StringComparer.Ordinal))
                {
                    failures.Add(
                        $"角色 {role} 引用了未声明的权限“{permission}”。" +
                        $"合法值：{string.Join("、", AuthHubConstants.Permissions.All)}。" +
                        "新增权限点属能力目录变更，需要改 AuthHubConstants.Permissions 并重新部署。");
                }
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
