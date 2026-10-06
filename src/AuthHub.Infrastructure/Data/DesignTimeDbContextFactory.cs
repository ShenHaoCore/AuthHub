using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace AuthHub.Infrastructure.Data;

/// <summary>
/// 设计时（<c>dotnet ef</c>）上下文工厂，让 migrations add / database update 不必启动整个 Api 进程。
///
/// <para><b>连接串的来源刻意与运行时同构</b>（环境变量 &gt; <c>appsettings.{环境}.json</c> &gt;
/// <c>appsettings.json</c>），而不是像以前那样硬编码一个本机连接串。硬编码的坏处是它会跟着代码
/// 走到任何机器上：在 CI 或生产机器上执行 <c>dotnet ef</c> 时，命令会安静地去连**本机**实例 ——
/// 要么失败得莫名其妙（"连接超时"，看不出是连错了地方），要么更糟：那台机器上恰好有个同名库，
/// 于是结构被改在了一个完全无关的数据库上。这类错误没有报错可看，只有事后对不上账。</para>
///
/// <para>用法：</para>
/// <code>
/// # 连接串取自 src/AuthHub.Api/appsettings.{ASPNETCORE_ENVIRONMENT}.json（未设则为 Development）
/// dotnet ef migrations add Xxx --project src/AuthHub.Infrastructure --startup-project src/AuthHub.Api
///
/// # 临时指向别处（优先级最高）
/// AUTHHUB_CONNECTION="Server=...;Database=...;" dotnet ef database update --project src/AuthHub.Infrastructure
///
/// # SQLite
/// AUTHHUB_PROVIDER=Sqlite AUTHHUB_CONNECTION="Data Source=authhub.design.db" dotnet ef migrations list ...
/// </code>
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AuthHubDbContext>
{
    public const string ProviderEnvironmentVariable = "AUTHHUB_PROVIDER";
    public const string ConnectionEnvironmentVariable = "AUTHHUB_CONNECTION";

    private const string DefaultSqliteConnection = "Data Source=authhub.design.db";

    public AuthHubDbContext CreateDbContext(string[] args)
    {
        var configuration = BuildDesignTimeConfiguration();

        var provider = Environment.GetEnvironmentVariable(ProviderEnvironmentVariable)
            ?? configuration["Database:Provider"]
            ?? DatabaseProviders.SqlServer;

        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)
            ?? configuration.GetConnectionString("DefaultConnection");

        // 拿不到连接串时**明确失败**，不回落到任何"方便"的默认值。
        // 回落到本机实例正是这个类原来最容易出问题的地方：命令会成功，
        // 只是作用在了一台与本次变更毫无关系的机器上。
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "设计时（dotnet ef）拿不到数据库连接串。" +
                $"已尝试：环境变量 {ConnectionEnvironmentVariable}、" +
                $"配置文件 appsettings.json / appsettings.{{环境}}.json 的 ConnectionStrings:DefaultConnection。" +
                "请在 src/AuthHub.Api/appsettings.Development.json 里配置它，" +
                $"或用 {ConnectionEnvironmentVariable}=\"Server=...;Database=...;\" 临时指定。" +
                "刻意不提供任何本机默认值 —— 那会让这条命令在别人的机器上安静地连错数据库。");
        }

        var builder = new DbContextOptionsBuilder<AuthHubDbContext>();

        if (DatabaseProviders.IsSqlite(provider))
        {
            builder.UseSqlite(connectionString);
        }
        else
        {
            builder.UseSqlServer(connectionString);
        }

        // 与运行时保持一致，否则迁移会漏掉 OpenIddict 的表
        builder.UseOpenIddict();

        return new AuthHubDbContext(builder.Options);
    }

    /// <summary>
    /// 按与运行时相同的层序拼出设计时配置。
    ///
    /// 这里读的是 <b>Api 项目</b>的 appsettings 文件，而不是 Infrastructure 的 ——
    /// 设计时命令的语义是"为那个即将启动的应用准备数据库"，所以配置的事实源必须是启动项目。
    /// </summary>
    private static IConfiguration BuildDesignTimeConfiguration()
    {
        // 环境名的取法与 ASP.NET Core 一致：ASPNETCORE_ENVIRONMENT 优先，DOTNET_ENVIRONMENT 次之。
        // 都没设时按 Development —— 与 `dotnet run` 的默认 profile 保持一致，
        // 否则本地开发跑迁移会去读 Production 配置（那里只有占位符）。
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Development";

        return new ConfigurationBuilder()
            .SetBasePath(ResolveConfigurationBasePath())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            // 与运行时同序：本机覆盖在入库配置之后、环境变量之前
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
    }

    /// <summary>
    /// 找出含 <c>appsettings.json</c> 的那个目录（即 Api 项目目录）。
    ///
    /// 为什么不能直接 <c>Directory.GetCurrentDirectory()</c>：<c>dotnet ef</c> 的工作目录取决于
    /// 调用方式（在仓库根、在项目目录、在解决方案目录各不一样），而设计时工厂在 Infrastructure
    /// 项目里，也拿不到 Api 项目的路径（反向依赖会把 Api 拉进 Infrastructure）。
    /// 于是按几个可预期的位置依次探测 —— 宁可多试几个，也不要静默地读不到配置。
    /// </summary>
    private static string ResolveConfigurationBasePath()
    {
        var current = Directory.GetCurrentDirectory();

        var candidates = new List<string>
        {
            current,
            Path.Combine(current, "src", "AuthHub.Api")
        };

        // 从输出目录（bin/<Configuration>/<Tfm>）逐级上溯，覆盖"在测试或工具目录下执行"的场景
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 6 && directory is not null; depth++, directory = directory.Parent)
        {
            candidates.Add(directory.FullName);
            candidates.Add(Path.Combine(directory.FullName, "src", "AuthHub.Api"));
        }

        return candidates.FirstOrDefault(
                   path => File.Exists(Path.Combine(path, "appsettings.json")))
               ?? current;
    }
}
