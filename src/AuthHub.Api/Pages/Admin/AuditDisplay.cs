using AuthHub.Domain.Enums;

namespace AuthHub.Api.Pages.Admin;

/// <summary>审计动作分组（用于筛选下拉的 <c>&lt;optgroup&gt;</c>）。</summary>
public sealed record AuditActionGroup(string Title, IReadOnlyList<string> Actions);

/// <summary>
/// 审计动作的展示映射（中文标签 + 徽章色调）。
/// 集中在这里，避免仪表盘与审计日志页各写一套导致口径不一致。
/// </summary>
public static class AuditDisplay
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        // 账号
        [AuditActionType.UserRegistered] = "注册账号",
        [AuditActionType.UserLoginSucceeded] = "登录成功",
        [AuditActionType.UserLoginFailed] = "登录失败",
        [AuditActionType.UserExternalLoginSucceeded] = "第三方登录成功",
        [AuditActionType.UserExternalLoginFailed] = "第三方登录失败",
        [AuditActionType.UserLogout] = "退出登录",
        [AuditActionType.UserPasswordChanged] = "修改密码",
        [AuditActionType.UserLockedOut] = "账号锁定",

        // MFA
        [AuditActionType.TwoFactorEnabled] = "启用 MFA",
        [AuditActionType.TwoFactorDisabled] = "关闭 MFA",
        [AuditActionType.TwoFactorFailed] = "MFA 校验失败",
        [AuditActionType.TwoFactorRecoveryCodeUsed] = "使用恢复码",

        // 令牌与授权
        [AuditActionType.TokenIssued] = "签发令牌",
        [AuditActionType.TokenRefreshed] = "刷新令牌",
        [AuditActionType.TokenRevoked] = "撤销令牌",
        [AuditActionType.ConsentGranted] = "同意授权",
        [AuditActionType.ConsentDenied] = "拒绝授权",
        [AuditActionType.ConsentRevoked] = "解除授权",

        // 客户端与 Scope
        [AuditActionType.ClientCreated] = "创建客户端",
        [AuditActionType.ClientUpdated] = "修改客户端",
        [AuditActionType.ClientDeleted] = "删除客户端",
        [AuditActionType.ScopeCreated] = "创建 Scope",
        [AuditActionType.ScopeUpdated] = "修改 Scope",
        [AuditActionType.ScopeDeleted] = "删除 Scope",

        // 管理操作
        [AuditActionType.UserCreatedByAdmin] = "管理员创建用户",
        [AuditActionType.UserUpdatedByAdmin] = "管理员修改用户",
        [AuditActionType.UserDeletedByAdmin] = "管理员删除用户",
        [AuditActionType.RoleAssigned] = "分配角色",
        [AuditActionType.RoleRevoked] = "移除角色",
        [AuditActionType.RoleCreated] = "创建角色",
        [AuditActionType.RoleUpdated] = "修改角色",
        [AuditActionType.RoleDeleted] = "删除角色",
        [AuditActionType.RolePermissionsUpdated] = "修改角色权限",
        [AuditActionType.RolePermissionsReset] = "恢复角色默认权限"
    };

    /// <summary>审计动作的中文标签（未知动作原样返回，便于后续扩展时仍可读）。</summary>
    public static string Label(string action)
        => Labels.TryGetValue(action, out var label) ? label : action;

    /// <summary>
    /// 全部审计动作，按业务分组（筛选下拉用 <c>&lt;optgroup&gt;</c> 渲染）。
    /// 显式写出顺序而不是依赖字典的枚举顺序 —— 字典的插入顺序是实现细节，不该被界面依赖。
    /// </summary>
    public static IReadOnlyList<AuditActionGroup> ActionGroups { get; } = new[]
    {
        new AuditActionGroup("账号", new[]
        {
            AuditActionType.UserRegistered,
            AuditActionType.UserLoginSucceeded,
            AuditActionType.UserLoginFailed,
            AuditActionType.UserExternalLoginSucceeded,
            AuditActionType.UserExternalLoginFailed,
            AuditActionType.UserLogout,
            AuditActionType.UserPasswordChanged,
            AuditActionType.UserLockedOut
        }),
        new AuditActionGroup("两步验证", new[]
        {
            AuditActionType.TwoFactorEnabled,
            AuditActionType.TwoFactorDisabled,
            AuditActionType.TwoFactorFailed,
            AuditActionType.TwoFactorRecoveryCodeUsed
        }),
        new AuditActionGroup("令牌与授权", new[]
        {
            AuditActionType.TokenIssued,
            AuditActionType.TokenRefreshed,
            AuditActionType.TokenRevoked,
            AuditActionType.ConsentGranted,
            AuditActionType.ConsentDenied,
            AuditActionType.ConsentRevoked
        }),
        new AuditActionGroup("客户端与 Scope", new[]
        {
            AuditActionType.ClientCreated,
            AuditActionType.ClientUpdated,
            AuditActionType.ClientDeleted,
            AuditActionType.ScopeCreated,
            AuditActionType.ScopeUpdated,
            AuditActionType.ScopeDeleted
        }),
        new AuditActionGroup("后台管理", new[]
        {
            AuditActionType.UserCreatedByAdmin,
            AuditActionType.UserUpdatedByAdmin,
            AuditActionType.UserDeletedByAdmin,
            AuditActionType.RoleAssigned,
            AuditActionType.RoleRevoked,
            AuditActionType.RoleCreated,
            AuditActionType.RoleUpdated,
            AuditActionType.RoleDeleted,
            // 单独列出来：权限归属的变更比其他角色操作更需要被翻出来看
            AuditActionType.RolePermissionsUpdated,
            AuditActionType.RolePermissionsReset
        })
    };

    /// <summary>是否为已知的审计动作（用于忽略非法的筛选值，避免把无效条件带给数据库）。</summary>
    public static bool IsKnown(string action) => Labels.ContainsKey(action);

    /// <summary>徽章色调：失败一律红色；成功按事件性质区分关注度。</summary>
    public static string Tone(string action, bool succeeded)
    {
        if (!succeeded)
        {
            return "ah-badge--danger";
        }

        return action switch
        {
            AuditActionType.UserLoginSucceeded => "ah-badge--success",
            AuditActionType.UserExternalLoginSucceeded => "ah-badge--success",
            AuditActionType.TwoFactorEnabled => "ah-badge--info",
            AuditActionType.UserLockedOut => "ah-badge--warning",
            AuditActionType.TokenRevoked => "ah-badge--warning",
            AuditActionType.TwoFactorRecoveryCodeUsed => "ah-badge--warning",
            AuditActionType.ConsentDenied => "ah-badge--warning",
            // 权限归属的变更属于"可以立刻把人变成管理员"的动作，值得在列表里显眼
            AuditActionType.RolePermissionsUpdated => "ah-badge--warning",
            AuditActionType.RolePermissionsReset => "ah-badge--warning",
            _ => string.Empty
        };
    }

    /// <summary>UTC 时间戳的统一展示格式（与页面上标注的时区一致）。</summary>
    public static string FormatUtc(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>截断过长文本（完整内容通过 title 属性给出，不丢信息）。</summary>
    public static string Shorten(string? value, int maxLength = 60)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength), "…");
    }
}
