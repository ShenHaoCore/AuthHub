using AuthHub.Domain.Constants;
using AuthHub.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace AuthHub.UnitTests.Infrastructure;

/// <summary>
/// 权限点目录的完整性：每个权限点都必须有中文显示名。
///
/// <para>漏配的后果是界面上该权限只能裸显代码名 —— <c>resources.manage</c> 曾漏配，
/// 管理后台的权限矩阵与编辑弹窗里它成了唯一没有中文名的权限点。
/// 代码名本身不能省（<c>AuthHub:RolePermissions</code> 配置、授权策略与审计都按它引用），
/// 但显示名是目录的一部分，与 <see cref="AuthHubConstants.Permissions.All"/> 一一对应。</para>
/// </summary>
public class RoleAdminServicePermissionCatalogTests
{
    [Fact]
    public void Every_permission_should_have_a_display_description()
    {
        // GetPermissionCatalog 不触碰任何依赖，null 即可构造
        var service = new RoleAdminService(
            dbContext: null!,
            roleManager: null!,
            audit: null!,
            rolePermissions: null!);

        var catalog = service.GetPermissionCatalog();

        catalog.Should().HaveSameCount(AuthHubConstants.Permissions.All,
            "目录应覆盖全部权限点，不多不少");
        catalog.Should().OnlyContain(
            p => !string.IsNullOrWhiteSpace(p.Description),
            because: "缺显示名的权限点在管理界面只能裸显代码名");
        catalog.Select(p => p.Name).Should().Equal(AuthHubConstants.Permissions.All);
    }
}
