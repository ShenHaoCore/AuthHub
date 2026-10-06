using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace AuthHub.Infrastructure.Configuration;

/// <summary>
/// 本机私有配置覆盖（<c>appsettings.Local.json</c>）的装配。
///
/// <para><b>要解决什么</b>：入库的 <c>appsettings.Development.json</c> 是团队共识 —— 它对所有人都该成立。
/// 但个人机器总有差异：另一台数据库实例、另一个证书路径、一个只在这台机器上存在的联调服务。
/// 把这些写进 Development 文件会让它慢慢变成"谁的机器谁改"，而别人的 F5 就那样坏了；
/// 写进环境变量则每次开新终端都要记得设。</para>
///
/// <para><b>为什么不用 User Secrets</b>：它也能达到目的，但（1）只在 Development 下生效，
/// （2）内容存在用户目录里，仓库里看不到任何"这里可以放什么"的线索。一个带 <c>.example</c>
/// 模板、能被任何环境使用的文件，同时也是一份自解释的文档。</para>
///
/// <para><b>插入位置是这段代码里唯一有语义的地方</b> —— 见 <see cref="AddAuthHubLocalSettings"/>。
/// 它放在 Infrastructure 而不是 Api，是为了让这个位置约束能被单元测试直接钉住
/// （测试项目不引用 Api 项目，放那边的逻辑只能靠端到端测试间接覆盖）。</para>
/// </summary>
public static class LocalSettingsConfiguration
{
    /// <summary>本机私有的覆盖文件。它在 <c>.gitignore</c> 里，只有 <c>.example</c> 模板入库。</summary>
    public const string FileName = "appsettings.Local.json";

    /// <summary>
    /// 把 <c>appsettings.Local.json</c> 插进配置链。
    ///
    /// 目标位置是 <b><c>appsettings.{Environment}.json</c> 之后、环境变量之前</b>，两个约束各有原因：
    /// <list type="bullet">
    /// <item>必须<b>晚于</b>入库的配置文件，否则本机覆盖没有意义；</item>
    /// <item>必须<b>早于</b>环境变量与命令行参数 —— 那些是运行时的最终裁决者。若让本机文件压过它们，
    /// 就会出现"CI 或容器里明明设了环境变量却不生效"，而线索藏在一个可能被遗忘的本地文件里，
    /// 是最难排查的一类问题。</item>
    /// </list>
    ///
    /// 用 <see cref="IConfigurationSource"/> 直接插入而不是 <c>AddJsonFile</c>：后者只会往末尾追加，
    /// 那样本机文件会盖过环境变量，正好踩中上面第二条。
    ///
    /// 文件不存在时静默跳过（<c>Optional</c>）—— 绝大多数机器上都不会有它。
    /// </summary>
    public static IConfigurationBuilder AddAuthHubLocalSettings(
        this IConfigurationBuilder builder,
        IFileProvider contentRootFileProvider)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(contentRootFileProvider);

        var source = new JsonConfigurationSource
        {
            Path = FileName,
            Optional = true,
            ReloadOnChange = true,
            FileProvider = contentRootFileProvider
        };

        var sources = builder.Sources;

        // 插在最后一个 JSON 文件源之后 —— 即 appsettings.{Environment}.json 后面。
        // 按"最后一个 JSON 源"定位而不是按文件名匹配：文件名里带环境名，
        // 而环境名是运行时可变的（Development / Testing / 自定义），不该在这里复述一遍。
        // 万一一个 JSON 源都没有（例如宿主用的是别的配置提供程序），退回追加到末尾。
        var insertAt = sources.Count;
        for (var index = sources.Count - 1; index >= 0; index--)
        {
            if (sources[index] is JsonConfigurationSource)
            {
                insertAt = index + 1;
                break;
            }
        }

        sources.Insert(insertAt, source);
        return builder;
    }
}
