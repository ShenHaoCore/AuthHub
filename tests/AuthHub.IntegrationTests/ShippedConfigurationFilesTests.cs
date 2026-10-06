using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AuthHub.IntegrationTests;

/// <summary>
/// 随包发布的配置文件本身的两条契约。
///
/// <para><b>为什么需要这组用例</b>：配置文件是**唯一一类改坏了不会在编译期被发现**的产物 ——
/// 少个逗号、注释符写错位置，编译照过、单元测试照跑，只有真的起进程才炸。
/// 而这份仓库里没有任何一步会解析它们（CI 不解析、Dockerfile 只是复制），
/// 于是"配置文件写坏了"可以一路走到部署。</para>
///
/// <para>另一条盯的是注释的<b>写法</b>：说明文字只能用 <c>//</c>，不能用
/// <c>"_comment_xxx": "..."</c> 那种伪键。理由见
/// <see cref="Shipped_config_files_should_not_use_pseudo_comment_keys"/>。</para>
/// </summary>
public class ShippedConfigurationFilesTests
{
    /// <summary>
    /// 库存里所有「随包发布」的配置文件。
    ///
    /// <c>.example</c> 模板也算 —— 它是给人照着复制去用的，坏了同样会误导人。
    /// <c>appsettings.Local.json</c> 不在列：它不入库，各机器内容不同。
    /// </summary>
    private static readonly string[] ShippedFiles =
    [
        "src/AuthHub.Api/appsettings.json",
        "src/AuthHub.Api/appsettings.Development.json",
        "src/AuthHub.Api/appsettings.Production.json",
        "src/AuthHub.Api/appsettings.Local.json.example"
    ];

    [Theory]
    [MemberData(nameof(ShippedFileNames))]
    public void Every_shipped_config_file_should_load_with_the_runtime_configuration_provider(string relativePath)
    {
        // 用与运行时**同一个**提供程序来读，而不是 JsonDocument 之类的通用解析器：
        // 容忍什么、不容忍什么都由它定义（它允许 // 与 /* */ 注释、允许尾逗号），
        // 换成别的解析器就是在验一个不存在的前提。
        var file = Path.GetFileName(relativePath);

        var act = () => new ConfigurationBuilder()
            .SetBasePath(Path.GetDirectoryName(FindRepositoryFile(relativePath))!)
            .AddJsonFile(file, optional: false)
            .Build();

        act.Should().NotThrow(
            because: $"{relativePath} 在运行时是用这个提供程序读的，它读不了就等于服务起不来");
    }

    [Theory]
    [MemberData(nameof(ShippedFileNames))]
    public void Shipped_config_files_should_not_use_pseudo_comment_keys(string relativePath)
    {
        // 伪键是"把注释写成配置项"：JSON 官方不支持注释，于是在对象里塞一个 "_comment_xxx": "说明"。
        // 它看着像注释，实际是真实配置项 —— 会进配置树、能被环境变量覆盖、
        // 将来一旦对某一节改用强类型绑定（ErrorOnUnknownConfiguration）就会让进程起不来。
        // 而 .NET 的 JSON 配置提供程序本来就支持真注释，没有理由付这个代价。
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Path.GetDirectoryName(FindRepositoryFile(relativePath))!)
            .AddJsonFile(Path.GetFileName(relativePath), optional: false)
            .Build();

        var pseudoKeys = configuration.AsEnumerable()
            .Where(pair => pair.Key
                .Split(':')
                .Any(segment => segment.StartsWith("_comment", StringComparison.OrdinalIgnoreCase)))
            .Select(pair => pair.Key)
            .ToArray();

        pseudoKeys.Should().BeEmpty(
            because: $"{relativePath} 里的说明文字应当写成 // 注释（配置系统原生支持），" +
                     "写成 _comment_xxx 键会变成真实配置项，混进配置树");
    }

    public static TheoryData<string> ShippedFileNames()
    {
        var data = new TheoryData<string>();
        foreach (var file in ShippedFiles)
        {
            data.Add(file);
        }

        return data;
    }

    /// <summary>
    /// 从测试输出目录逐级向上找到仓库里的源文件（与 RolePermissionConfigurationTests 同法）。
    ///
    /// 必须对着**源文件**断言：这里要验的是"随包发布的那份文本"，
    /// 而不是某个已经被宿主解析过的结果。
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
