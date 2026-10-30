using System.Reflection;
using AuthHub.Application.Common;
using AuthHub.Domain.Entities;
using AuthHub.Infrastructure.Data;
using FluentAssertions;
using Xunit;

namespace AuthHub.UnitTests.Architecture;

/// <summary>分层依赖守护：把 csproj 注释与 architecture.md 写明的依赖方向变成可执行契约。</summary>
/// <remarks>
/// 断言走编译期引用（<see cref="Assembly.GetReferencedAssemblies"/>）：它精确反映 csproj 的
/// PackageReference / FrameworkReference / 项目引用，不受运行期影响。
/// 规则与 architecture.md「依赖方向」「几处有意的分层取舍」两节一一对应，白名单均是有意为之的约束。
/// </remarks>
public sealed class LayeringTests
{
    private static readonly Assembly Domain = typeof(ApplicationUser).Assembly;
    private static readonly Assembly Application = typeof(Result).Assembly;
    private static readonly Assembly Infrastructure = typeof(AuthHubDbContext).Assembly;
    private static readonly Assembly UnitTests = typeof(LayeringTests).Assembly;

    [Fact]
    public void Domain_should_not_reference_outer_layers_or_web_stacks()
    {
        // 唯一例外（有意为之）：Microsoft.Extensions.Identity.Stores，见 architecture.md 分层取舍第 1 条。
        var forbidden = ReferencesOf(Domain)
            .Where(name => name != "AuthHub.Domain")
            .Where(name =>
                name.StartsWith("AuthHub.", StringComparison.Ordinal) ||
                name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) ||
                name.StartsWith("Microsoft.EntityFrameworkCore.", StringComparison.Ordinal))
            .ToArray();

        forbidden.Should().BeEmpty("Domain 是最内层，任何外层或 Web 栈引用都是分层破坏");
    }

    [Fact]
    public void Application_should_not_reference_infrastructure_api_ef_core_or_mvc_http()
    {
        // 唯一例外（有意为之）：Microsoft.AspNetCore.Identity（SignInManager），见 Application csproj 注释；
        // csproj 同时承诺「不接触 Controller / HttpContext / EF Core」，据此禁掉 Mvc / Http / EF。
        var forbidden = ReferencesOf(Application)
            .Where(name => name is "AuthHub.Infrastructure" or "AuthHub.Api" ||
                name.StartsWith("Microsoft.EntityFrameworkCore.", StringComparison.Ordinal) ||
                name.StartsWith("Microsoft.AspNetCore.Mvc", StringComparison.Ordinal) ||
                name.StartsWith("Microsoft.AspNetCore.Http", StringComparison.Ordinal))
            .ToArray();

        forbidden.Should().BeEmpty("Application 只依赖 Domain 与 Identity 抽象，不接触实现层与 Web 契约");
    }

    [Fact]
    public void Infrastructure_should_not_reference_api()
    {
        var forbidden = ReferencesOf(Infrastructure)
            .Where(name => name == "AuthHub.Api")
            .ToArray();

        forbidden.Should().BeEmpty("依赖方向是 Api ──▶ Infrastructure，反向引用会形成环");
    }

    [Fact]
    public void UnitTests_should_not_reference_api()
    {
        // 见 architecture.md「几处有意的分层取舍」末条：单元测试项目不引用 Api，
        // 倒逼需要被单测钉住的判定逻辑（反代网段解析、种子决策等）不落在 Api 层。
        var forbidden = ReferencesOf(UnitTests)
            .Where(name => name == "AuthHub.Api")
            .ToArray();

        forbidden.Should().BeEmpty("单元测试引用 Api 会瓦解「判定逻辑不落在 Api 层」的可测性约束");
    }

    private static IReadOnlyCollection<string> ReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .Where(name => name.Length > 0)
            .ToArray();
}
