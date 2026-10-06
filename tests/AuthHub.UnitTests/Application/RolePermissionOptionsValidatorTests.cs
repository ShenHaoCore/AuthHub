using AuthHub.Application.Options;
using AuthHub.Domain.Constants;
using FluentAssertions;
using Xunit;

namespace AuthHub.UnitTests.Application;

/// <summary>
/// 权限归属配置的启动期校验。
///
/// 这类校验的价值全在"配错了会不会被拦住"：权限名写错不会当场报错，
/// 只会让某个角色静默地拿不到权限（菜单消失、接口 403），
/// 而排查方向通常会被带偏到策略注册或缓存上。启动即失败比事后查便宜得多。
/// </summary>
public class RolePermissionOptionsValidatorTests
{
    private readonly RolePermissionOptionsValidator _validator = new();

    [Fact]
    public void Default_options_should_pass()
        => _validator.Validate(null, new RolePermissionOptions()).Succeeded.Should().BeTrue();

    [Fact]
    public void Undeclared_permission_should_fail_and_name_the_offender()
    {
        var result = _validator.Validate(
            null,
            WithPermissions(AuthHubConstants.Roles.Auditor, "audit.write"));

        result.Failed.Should().BeTrue();
        var failures = result.Failures ?? Array.Empty<string>();
        failures.Should().Contain(message => message.Contains("audit.write", StringComparison.Ordinal),
            because: "错误信息必须指出是哪个权限名有问题，否则等于没说");
    }

    [Fact]
    public void Wrong_casing_should_fail()
    {
        // 权限名会原样写进 authhub:permission 声明，而策略比对的是一律小写的常量值。
        // 写成 Users.Manage 不报错，只会让该角色静默地过不了授权 —— 正是要拦住的。
        var result = _validator.Validate(
            null,
            WithPermissions(AuthHubConstants.Roles.UserManager, "Users.Manage"));

        result.Failed.Should().BeTrue(because: "大小写不符只会静默失配，必须在校验期拦住");
    }

    [Fact]
    public void Blank_permission_should_fail()
    {
        var result = _validator.Validate(
            null,
            WithPermissions(AuthHubConstants.Roles.Auditor, AuthHubConstants.Permissions.AuditRead, "   "));

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Empty_permission_list_should_pass()
    {
        // "X": [] 是有效写法：显式收回该角色的全部权限
        _validator.Validate(null, WithPermissions(AuthHubConstants.Roles.User)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Custom_role_should_be_accepted()
    {
        // 自定义角色本来就允许配权限，校验器不应限制角色名
        var result = _validator.Validate(
            null,
            WithPermissions("ReportViewer", AuthHubConstants.Permissions.AuditRead));

        result.Succeeded.Should().BeTrue(because: "自定义角色配权限是合法用法（也是这套配置化的主要动机）");
    }

    private static RolePermissionOptions WithPermissions(string role, params string[] permissions)
    {
        var options = new RolePermissionOptions();
        options.Roles[role] = permissions;
        return options;
    }
}
