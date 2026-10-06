using System.Text.Json;
using AuthHub.Infrastructure.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.FileProviders;

namespace AuthHub.UnitTests.Infrastructure;

/// <summary>
/// 本机私有覆盖文件（<c>appsettings.Local.json</c>）的装配。
///
/// 这组用例全部围绕**一个位置约束**：本机文件要晚于入库的配置文件、但要早于环境变量。
/// 两个方向都容易写错，而写错的后果都是"配置不生效"，症状上看不出是哪一边出的问题 ——
/// 所以两个方向各有用例钉住，另加一条直接断言配置源顺序的用例，免得断言只停留在"值对不对"。
/// </summary>
public class LocalSettingsConfigurationTests : IDisposable
{
    private const string ProbeKey = "Probe";

    private readonly string _directory;
    private readonly PhysicalFileProvider _fileProvider;

    public LocalSettingsConfigurationTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"authhub-local-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _fileProvider = new PhysicalFileProvider(_directory);
    }

    public void Dispose()
    {
        _fileProvider.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 句柄偶尔要等一会儿才释放：临时目录交给操作系统回收即可
        }

        GC.SuppressFinalize(this);
    }

    private void WriteSettings(string fileName, string probeValue)
        => File.WriteAllText(
            Path.Combine(_directory, fileName),
            JsonSerializer.Serialize(new Dictionary<string, string> { [ProbeKey] = probeValue }));

    /// <summary>
    /// 按运行时的层序拼一条配置链。内存源放在最后代表环境变量与命令行参数
    /// （它们在真实宿主里就是排在最后的那几类源，语义相同：运行时的最终裁决者）。
    /// </summary>
    private IConfiguration Build(
        string environmentFile = "appsettings.Development.json",
        IEnumerable<KeyValuePair<string, string?>>? runtimeSources = null)
    {
        IConfigurationBuilder builder = new ConfigurationBuilder();

        builder.AddJsonFile(_fileProvider, "appsettings.json", optional: true, reloadOnChange: false);
        builder.AddJsonFile(_fileProvider, environmentFile, optional: true, reloadOnChange: false);
        builder.AddAuthHubLocalSettings(_fileProvider);
        builder.AddInMemoryCollection(runtimeSources);

        return builder.Build();
    }

    [Fact]
    public void Local_settings_should_override_the_environment_file()
    {
        // 晚于 {环境}.json —— 否则本机覆盖没有意义
        WriteSettings("appsettings.json", "来自 appsettings.json");
        WriteSettings("appsettings.Testing.json", "来自 appsettings.Testing.json");
        WriteSettings(LocalSettingsConfiguration.FileName, "来自 appsettings.Local.json");

        Build(environmentFile: "appsettings.Testing.json")[ProbeKey]
            .Should().Be("来自 appsettings.Local.json");
    }

    [Fact]
    public void Environment_variables_should_still_win_over_the_local_file()
    {
        // 早于环境变量 —— 这一条比上一条更重要。反过来的话，容器或 CI 里
        // "环境变量明明设了却不生效"，而线索躺在一个可能被遗忘的本地文件里。
        WriteSettings("appsettings.json", "来自 appsettings.json");
        WriteSettings(LocalSettingsConfiguration.FileName, "来自 appsettings.Local.json");

        var configuration = Build(runtimeSources: [new KeyValuePair<string, string?>(ProbeKey, "来自环境变量")]);

        configuration[ProbeKey].Should().Be(
            "来自环境变量",
            because: "本机文件不该有本事压过部署平台注入的配置");
    }

    [Fact]
    public void A_missing_local_file_should_be_optional()
    {
        // 绝大多数机器上都不会有这个文件，它缺席不能变成启动失败
        WriteSettings("appsettings.json", "来自 appsettings.json");

        Build()[ProbeKey].Should().Be("来自 appsettings.json");
    }

    [Fact]
    public void The_local_source_should_sit_between_the_json_sources_and_the_rest()
    {
        // 直接断言配置源顺序，而不是只看最终取值：取值对可能是巧合
        // （比如两份文件恰好写了同一个值），顺序才是这份装配真正的契约。
        WriteSettings("appsettings.json", "来自 appsettings.json");

        IConfigurationBuilder builder = new ConfigurationBuilder();
        builder.AddJsonFile(_fileProvider, "appsettings.json", optional: true, reloadOnChange: false);
        builder.AddAuthHubLocalSettings(_fileProvider);
        builder.AddInMemoryCollection();

        var sources = builder.Sources;
        var localIndex = IndexOf(sources, source =>
            source is JsonConfigurationSource json && json.Path == LocalSettingsConfiguration.FileName);

        // 锚点要排除 Local 源自己 —— 它本身也是 JsonConfigurationSource，
        // 算进去的话"最后一个 JSON 源"就永远是它，断言变成恒真（或恒假）。
        var anchorIndex = sources
            .Select((source, index) => (source, index))
            .Where(pair => pair.source is JsonConfigurationSource json
                           && json.Path != LocalSettingsConfiguration.FileName)
            .Max(pair => pair.index);

        var memoryIndex = IndexOf(sources, source => source is MemoryConfigurationSource);

        localIndex.Should().BeGreaterThan(anchorIndex, because: "它要能盖过入库的配置文件");
        localIndex.Should().BeLessThan(memoryIndex, because: "它不该盖过环境变量这类运行时源");
    }

    [Fact]
    public void It_should_still_be_added_when_there_is_no_json_source_to_anchor_on()
    {
        // 宿主若不使用 JSON 配置提供程序，就没有"最后一个 JSON 源"可插。
        // 这时退化成追加到末尾（优先级最高）比什么都不做要好 —— 后者是静默失效。
        IConfigurationBuilder builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection();

        builder.AddAuthHubLocalSettings(_fileProvider);

        builder.Sources[^1].Should().BeOfType<JsonConfigurationSource>();
    }

    private static int IndexOf(IList<IConfigurationSource> sources, Func<IConfigurationSource, bool> predicate)
        => sources
            .Select((source, index) => (source, index))
            .Single(pair => predicate(pair.source))
            .index;
}
