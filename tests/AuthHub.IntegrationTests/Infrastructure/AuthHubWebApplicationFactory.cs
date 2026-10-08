using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthHub.IntegrationTests.Infrastructure;

/// <summary>
/// 集成测试宿主。
///
/// 关键点：Program.cs 在 <c>builder.Build()</c> 之前就读取了一批配置
/// （数据库提供程序、连接串、是否强制 HTTPS、是否播种……）。
/// WebApplicationFactory 的 <c>ConfigureAppConfiguration</c> 对最小托管（minimal hosting）
/// 是延迟生效的，来不及影响这些“早期读取”，因此这里改用**环境变量**覆盖
/// （WebApplication.CreateBuilder 构造时就会读入环境变量）。
///
/// 由于环境变量是进程级的，集成测试程序集整体关闭了并行执行，见 AssemblyInfo.cs。
/// </summary>
public class AuthHubWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath;
    private readonly string _environment;

    /// <param name="overrides">在基线之上覆盖的环境变量。</param>
    /// <param name="environment">
    /// 宿主环境名。默认 <c>Testing</c> —— 启动期的环境护栏刻意豁免它（夹具的配置本来就是
    /// "不该拿去部署"的样子）。需要验证护栏本身时才传 <c>Production</c> 之类的值。
    /// </param>
    public AuthHubWebApplicationFactory(
        IReadOnlyDictionary<string, string>? overrides = null,
        string environment = "Testing")
    {
        _environment = environment;
        _databasePath = Path.Combine(Path.GetTempPath(), $"authhub-it-{Guid.NewGuid():N}.db");

        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Database__Provider"] = "Sqlite",
            ["ConnectionStrings__DefaultConnection"] = $"Data Source={_databasePath}",
            ["AuthHub__Issuer"] = "http://localhost/",
            ["AuthHub__Security__RequireHttps"] = "false",
            ["AuthHub__Features__EnableApiDocs"] = "false",
            ["AuthHub__Features__EnablePasswordFlow"] = "false",
            ["AuthHub__Seeding__Enabled"] = "true",
            ["AuthHub__Seeding__MigrateOnStartup"] = "true",
            ["AuthHub__RateLimiting__TokenRequestsPerMinute"] = "1000",
            ["AuthHub__RateLimiting__LoginRequestsPerMinute"] = "1000",
            // 显式置空：环境变量是进程级的，而"密钥环目录"这类配置只有个别用例会设。
            // 不重置的话，那个值会残留下来影响后续所有工厂 —— 测试之间就开始互相污染了。
            ["AuthHub__Security__DataProtectionKeysPath"] = "",
            ["AuthHub__Security__TrustForwardedHeaders"] = "false",
            // 第三方登录开关同理：个别用例会启用某个提供商，不设回 false 就会残留给后续工厂
            ["AuthHub__Authentication__GitHub__Enabled"] = "false",
            ["AuthHub__Authentication__Google__Enabled"] = "false",
            ["AuthHub__Authentication__WeCom__Enabled"] = "false",
            ["AuthHub__Seed__AdminPassword"] = "Admin@12345",
            ["AuthHub__Seed__DemoPassword"] = "Alice@12345",
            ["AuthHub__Seed__WebClientSecret"] = "web-secret",
            ["AuthHub__Seed__M2mClientSecret"] = "m2m-secret",
        };

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                settings[key] = value;
            }
        }

        foreach (var (key, value) in settings)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    /// <summary>示例 M2M 客户端（种子数据）。</summary>
    public const string M2mClientId = "m2m-service";

    /// <summary>示例 M2M 客户端密钥（种子数据）。</summary>
    public const string M2mClientSecret = "m2m-secret";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        // SQLite 文件库用完即删，避免临时目录堆积
        foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
        {
            try
            {
                var path = _databasePath + suffix;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // 文件仍被占用时忽略：临时目录会由操作系统回收
            }
        }
    }
}
