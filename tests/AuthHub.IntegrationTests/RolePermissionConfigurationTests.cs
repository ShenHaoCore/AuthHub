using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Xunit;

namespace AuthHub.IntegrationTests;

/// <summary>
/// 「角色 → 权限」归属的配置契约。
///
/// 映射从代码搬到配置之后，多出两类新风险，这组用例分别盯住它们：
///   1. **代码默认与随包 appsettings 漂移** —— 运维照着 appsettings 改会得到意外结果，
///      因为他以为"未列出的角色"走的是当前这份默认；
///   2. **配错了却静默生效** —— 权限名写错不会当场报错，只会让某个角色拿不到权限。
/// </summary>
public class RolePermissionConfigurationTests
{
    private const string AppSettingsRelativePath = "src/AuthHub.Api/appsettings.json";

    [Fact]
    public void Shipped_appsettings_should_match_the_code_defaults()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindRepositoryFile(AppSettingsRelativePath)));

        var fromFile = document.RootElement
            .GetProperty("AuthHub")
            .GetProperty("RolePermissions")
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.EnumerateArray().Select(item => item.GetString()!).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var defaults = RolePermissionMap.CreateDefaultRoles();

        fromFile.Keys.Should().BeEquivalentTo(defaults.Keys,
            because: "随包的 appsettings 必须与出厂默认覆盖同一批角色，否则「照文档改配置」会得到意外结果");

        foreach (var (role, permissions) in defaults)
        {
            fromFile[role].Should().BeEquivalentTo(permissions,
                because: $"{role} 的权限归属在代码默认与 appsettings 之间不一致");
        }
    }

    /// <summary>
    /// 配置里出现未声明的权限名时，宿主必须**起不来**。
    ///
    /// 这条用例守的是"静默失效"这个最贵的故障模式：权限名写错不会当场报错，
    /// 只会让该角色拿不到权限 —— 现象是菜单消失、接口 403，
    /// 而排查方向通常会被带偏到策略注册或缓存上。
    /// </summary>
    [Fact]
    public void Undeclared_permission_in_configuration_should_fail_startup()
    {
        // 环境变量是进程级的，跑完必须还原 —— 否则会污染同程序集里后续的用例。
        // 集成测试整体关闭了并行执行（见 AssemblyInfo.cs），因此这里是可控的。
        const string key = "AuthHub__RolePermissions__Auditor__0";
        var original = Environment.GetEnvironmentVariable(key);

        try
        {
            Environment.SetEnvironmentVariable(key, "not.a.declared.permission");

            var exception = Record.Exception(() =>
            {
                using var factory = new AuthHubWebApplicationFactory();
                using var client = factory.CreateClient();
            });

            exception.Should().NotBeNull(
                because: "配置里出现未声明的权限名时必须启动失败 —— 静默 403 比启动失败难查得多");
            exception!.ToString().Should().Contain("not.a.declared.permission",
                because: "错误信息要指出是哪个权限名有问题，否则等于没说");
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, original);
        }
    }

    /// <summary>
    /// 改配置就能改归属 —— 这正是把这一层搬出代码的目的所在。
    ///
    /// 这里验的是整条链：环境变量 → 配置 → Options 绑定 → <see cref="IRolePermissionMap"/>。
    /// 页面上的呈现（权限矩阵是否跟着变）由管理后台那组用例覆盖。
    /// </summary>
    [Fact]
    public void Configured_permission_should_reach_the_role_map_without_code_changes()
    {
        const string key = "AuthHub__RolePermissions__Auditor__0";
        var original = Environment.GetEnvironmentVariable(key);

        try
        {
            Environment.SetEnvironmentVariable(key, AuthHubConstants.Permissions.UsersManage);

            using var factory = new AuthHubWebApplicationFactory();
            using var client = factory.CreateClient();

            var map = factory.Services.GetRequiredService<IRolePermissionMap>();

            map.GetPermissions(AuthHubConstants.Roles.Auditor).Should().BeEquivalentTo(new[]
            {
                AuthHubConstants.Permissions.UsersManage
            }, because: "配置里列出的角色应当覆盖出厂默认，且不需要改任何代码");
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, original);
        }
    }

    /// <summary>
    /// 从测试输出目录逐级向上找到仓库里的文件。
    ///
    /// 这条契约必须对着**源文件**断言：走 HTTP 取到的是宿主解析后的结果，
    /// 而这里要验的恰恰是"随包发布的那份文本"。
    /// </summary>
    private static string FindRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"从 {AppContext.BaseDirectory} 逐级向上找不到 {relativePath}");
    }
}
