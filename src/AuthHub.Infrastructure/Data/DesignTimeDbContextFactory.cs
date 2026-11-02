using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AuthHub.Infrastructure.Data;

/// <summary>设计时（<c>dotnet ef</c>）上下文工厂，让 migrations add / database update 不必启动整个 Api 进程。</summary>
/// <remarks>
/// - 连接串来源刻意与运行时同构（环境变量 &gt; User Secrets（仅 Development）&gt; appsettings.{环境}.json &gt;
///   appsettings.json），不硬编码本机连接串 —— 硬编码会让 <c>dotnet ef</c> 在别的机器上安静地连错数据库。
/// - 既有迁移集是 SQL Server 形状：不要对 PostgreSQL 跑 database update（PG 建库走运行时
///   EnsureCreated，AuthHub:Seeding:MigrateOnStartup）。
/// - 临时指向别处用环境变量（优先级最高）：<c>AUTHHUB_CONNECTION</c> 连接串、<c>AUTHHUB_PROVIDER</c> 提供程序。
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AuthHubDbContext>
{
    public const string ProviderEnvironmentVariable = "AUTHHUB_PROVIDER";
    public const string ConnectionEnvironmentVariable = "AUTHHUB_CONNECTION";

    /// <summary>与 <c>AuthHub.Api.csproj</c> 的 <c>UserSecretsId</c> 一致，供设计时读取同一份机密。</summary>
    private const string ApiUserSecretsId = "authhub-api-secrets";

    private const string DefaultSqliteConnection = "Data Source=authhub.design.db";

    public AuthHubDbContext CreateDbContext(string[] args)
    {
        var configuration = BuildDesignTimeConfiguration();

        var provider = Environment.GetEnvironmentVariable(ProviderEnvironmentVariable)
            ?? configuration["Database:Provider"]
            ?? DatabaseProviders.SqlServer;

        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)
            ?? configuration.GetConnectionString("DefaultConnection");

        // 拿不到连接串时明确失败，不回落到任何默认值 —— 回落本机实例会让命令成功却作用在无关机器上。
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "设计时（dotnet ef）拿不到数据库连接串。" +
                $"已尝试：环境变量 {ConnectionEnvironmentVariable}、" +
                $"配置文件 / Development User Secrets 的 ConnectionStrings:DefaultConnection。" +
                "请在 src/AuthHub.Api/appsettings.Development.json 或 User Secrets 里配置它，" +
                $"或用 {ConnectionEnvironmentVariable}=\"Server=...;Database=...;\" 临时指定。" +
                "刻意不提供任何本机默认值 —— 那会让这条命令在别人的机器上安静地连错数据库。");
        }

        var builder = new DbContextOptionsBuilder<AuthHubDbContext>();

        if (DatabaseProviders.IsSqlite(provider))
        {
            builder.UseSqlite(connectionString);
        }
        else if (DatabaseProviders.IsPostgres(provider))
        {
            builder.UseNpgsql(connectionString);
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
    /// 按与运行时相同的层序拼出设计时配置。读的是 Api 项目（启动项目）的 appsettings ——
    /// 设计时命令的语义是"为那个即将启动的应用准备数据库"。
    /// </summary>
    private static IConfiguration BuildDesignTimeConfiguration()
    {
        // 环境名的取法与 ASP.NET Core 一致：ASPNETCORE_ENVIRONMENT 优先，DOTNET_ENVIRONMENT 次之。
        // 都没设时按 Development（与 dotnet run 默认一致），否则本地迁移会读到只有占位符的 Production 配置。
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Development";

        // 与 WebApplication.CreateBuilder 同序：json →（Development）User Secrets → 环境变量。
        // UserSecretsId 与 Api 项目 csproj 保持一致；机密文件不存在时不抛异常。
        var builder = new ConfigurationBuilder()
            .SetBasePath(ResolveConfigurationBasePath())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true);

        if (string.Equals(environment, Environments.Development, StringComparison.OrdinalIgnoreCase))
        {
            builder.AddUserSecrets(ApiUserSecretsId);
        }

        return builder.AddEnvironmentVariables().Build();
    }

    /// <summary>
    /// 找出含 <c>appsettings.json</c> 的 Api 项目目录。不能直接用当前目录：<c>dotnet ef</c> 的工作目录
    /// 随调用方式变化，设计时工厂在 Infrastructure 里也拿不到 Api 路径，故按可预期位置依次探测。
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
