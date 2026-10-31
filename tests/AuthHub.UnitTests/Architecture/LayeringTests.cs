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

    [Fact]
    public void Api_should_touch_infrastructure_only_in_composition_code()
    {
        // architecture.md「依赖方向」：Api → Infrastructure 仅用于 DI 注册，不直接使用其类型。
        // 程序集引用层面无法区分"注册"与"使用"，这里以源码白名单落成可执行契约：
        // 允许出现 Infrastructure using 的只有组合根（Program / Extensions）与
        // Development 专用的调试通道（DevSmsController，读 Infrastructure 的开发短信收件箱）。
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Program.cs",
            "DevSmsController.cs"
        };

        var violations = Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "src", "AuthHub.Api"), "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("using AuthHub.Infrastructure", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file)!)
            .Where(name => !allowed.Contains(name) && !name.EndsWith("Extensions.cs", StringComparison.Ordinal))
            .ToArray();

        violations.Should().BeEmpty("Api 直接使用 Infrastructure 类型的位置超出了组合根白名单：{0}", string.Join(", ", violations));
    }

    /// <summary>从测试输出目录向上走，定位含 Directory.Build.props 的仓库根。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent!;
        }

        return dir?.FullName ?? throw new InvalidOperationException("未找到仓库根（Directory.Build.props）。");
    }

    private static IReadOnlyCollection<string> ReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .Where(name => name.Length > 0)
            .ToArray();
}
