using System.Security.Claims;
using AuthHub.Domain.Constants;

namespace AuthHub.Api.Pages.Admin;

/// <summary>侧边栏菜单项。<see cref="Permission"/> 为空表示“进入后台即可见”。</summary>
public sealed record AdminNavItem(
    string Key,
    string Title,
    string Href,
    string IconPath,
    string? Permission,
    string Group);

/// <summary>侧边栏分组。</summary>
public sealed record AdminNavGroup(string Title, IReadOnlyList<AdminNavItem> Items);

/// <summary>面包屑项（<see cref="Href"/> 为空表示当前页，不可点击）。</summary>
public sealed record BreadcrumbItem(string Text, string? Href);

/// <summary>
/// 后台导航的单一事实源：侧边栏、面包屑、页面标题都从这里取。
///
/// 权限点与 <see cref="AuthHubConstants.Permissions"/> 一一对应 —— 菜单可见性
/// 只做“界面收敛”，真正的访问控制在各页面的授权策略上（见 UiPolicies），
/// 不依赖前端是否隐藏了入口。
/// </summary>
public static class AdminNav
{
    /// <summary>24×24 描边图标的 path 数据（与 authhub.css 的 .ah-icon 配合）。</summary>
    public static class Icons
    {
        public const string Dashboard = "M3 3h7v7H3zM14 3h7v7h-7zM14 14h7v7h-7zM3 14h7v7H3z";
        public const string Users = "M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2M9 3a4 4 0 1 0 0 8 4 4 0 0 0 0-8zM23 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75";
        public const string Roles = "M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z";
        public const string Clients = "M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16zM3.27 6.96 12 12.01l8.73-5.05M12 22.08V12";
        public const string Scopes = "M21 2l-2 2m-7.61 7.61a5.5 5.5 0 1 1-7.778 7.778 5.5 5.5 0 0 1 7.777-7.777zm0 0L15.5 7.5m0 0 3 3L22 7l-3-3";
        public const string Audit = "M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01";
        public const string Profile = "M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2M12 3a4 4 0 1 0 0 8 4 4 0 0 0 0-8z";
        public const string Code = "M16 18l6-6-6-6M8 6l-6 6 6 6";
        public const string Menu = "M3 12h18M3 6h18M3 18h18";
        public const string Search = "M11 19a8 8 0 1 0 0-16 8 8 0 0 0 0 16zM21 21l-4.35-4.35";
        public const string Plus = "M12 5v14M5 12h14";
        public const string Edit = "M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z";
        public const string Trash = "M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6M10 11v6M14 11v6";
        public const string Lock = "M5 11h14a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-7a2 2 0 0 1 2-2zM7 11V7a5 5 0 0 1 10 0v4";
        public const string Unlock = "M5 11h14a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-7a2 2 0 0 1 2-2zM7 11V7a5 5 0 0 1 9.9-1";
        public const string Logout = "M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4M16 17l5-5-5-5M21 12H9";
        public const string Refresh = "M23 4v6h-6M1 20v-6h6M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15";
        public const string Copy = "M9 9h10a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H9a2 2 0 0 1-2-2V11a2 2 0 0 1 2-2zM5 15H4a2 2 0 0 1-2-2V3a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2v1";
        public const string Alert = "M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0zM12 9v4M12 17h.01";
        public const string Check = "M20 6L9 17l-5-5";
        public const string Clock = "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20zM12 6v6l4 2";
        public const string Info = "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20zM12 16v-4M12 8h.01";
        public const string Key = "M12.65 10a6 6 0 1 1-1.3 1.3L12 10zm0 0h9m-3 0v3";
        public const string Mail = "M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zM22 7l-10 6L2 7";
        public const string Phone = "M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07 19.5 19.5 0 0 1-6-6A19.79 19.79 0 0 1 2.12 4.18 2 2 0 0 1 4.11 2h3a2 2 0 0 1 2 1.72c.13.96.36 1.9.7 2.81a2 2 0 0 1-.45 2.11L8.09 9.91a16 16 0 0 0 6 6l1.27-1.27a2 2 0 0 1 2.11-.45c.9.34 1.85.57 2.81.7A2 2 0 0 1 22 16.92z";
    }

    /// <summary>侧边栏分组（顺序即渲染顺序）。</summary>
    public static IReadOnlyList<AdminNavGroup> Groups { get; } = new[]
    {
        new AdminNavGroup("总览", new[]
        {
            new AdminNavItem("dashboard", "仪表盘", "/admin", Icons.Dashboard, null, "总览")
        }),
        new AdminNavGroup("身份与访问", new[]
        {
            new AdminNavItem("users", "用户", "/admin/users", Icons.Users, AuthHubConstants.Permissions.UsersManage, "身份与访问"),
            new AdminNavItem("roles", "角色与权限", "/admin/roles", Icons.Roles, AuthHubConstants.Permissions.RolesManage, "身份与访问")
        }),
        new AdminNavGroup("OAuth 资源", new[]
        {
            new AdminNavItem("clients", "客户端", "/admin/clients", Icons.Clients, AuthHubConstants.Permissions.ClientsManage, "OAuth 资源"),
            new AdminNavItem("scopes", "Scope", "/admin/scopes", Icons.Scopes, AuthHubConstants.Permissions.ScopesManage, "OAuth 资源")
        }),
        new AdminNavGroup("运维", new[]
        {
            new AdminNavItem("audit", "审计日志", "/admin/audit-logs", Icons.Audit, AuthHubConstants.Permissions.AuditRead, "运维"),
            new AdminNavItem("profile", "我的账户", "/admin/profile", Icons.Profile, null, "运维")
        })
    };

    /// <summary>全部菜单项的扁平序列。</summary>
    public static IEnumerable<AdminNavItem> AllItems => Groups.SelectMany(group => group.Items);

    /// <summary>按 key 查找菜单项。</summary>
    public static AdminNavItem? Find(string? key)
        => string.IsNullOrEmpty(key)
            ? null
            : AllItems.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));

    /// <summary>当前身份是否可见该菜单项。</summary>
    public static bool IsVisible(AdminNavItem item, ClaimsPrincipal user)
        => item.Permission is null || user.HasClaim(AuthHubConstants.ClaimTypes.Permission, item.Permission);

    /// <summary>由菜单 key 推导面包屑。</summary>
    public static IReadOnlyList<BreadcrumbItem> Breadcrumb(string? key, string currentTitle)
    {
        var item = Find(key);
        if (item is null)
        {
            return new[] { new BreadcrumbItem("后台", "/admin"), new BreadcrumbItem(currentTitle, null) };
        }

        var crumbs = new List<BreadcrumbItem> { new("后台", "/admin") };

        if (!string.Equals(item.Key, "dashboard", StringComparison.Ordinal))
        {
            crumbs.Add(new BreadcrumbItem(item.Group, null));
        }

        crumbs.Add(new BreadcrumbItem(currentTitle, null));
        return crumbs;
    }

    /// <summary>权限分组标题：用于角色页的权限树与权限矩阵。</summary>
    public static string PermissionGroupTitle(string permission) => permission switch
    {
        AuthHubConstants.Permissions.ClientsManage => "客户端",
        AuthHubConstants.Permissions.ScopesManage => "Scope",
        AuthHubConstants.Permissions.UsersManage => "用户",
        AuthHubConstants.Permissions.RolesManage => "角色",
        AuthHubConstants.Permissions.TokensRevoke => "令牌",
        AuthHubConstants.Permissions.AuditRead => "审计",
        _ => "其他"
    };
}
