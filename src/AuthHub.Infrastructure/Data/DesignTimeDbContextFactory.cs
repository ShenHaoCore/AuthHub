using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AuthHub.Infrastructure.Data;

/// <summary>
/// 设计时（dotnet ef）上下文工厂。
/// 让 `dotnet ef migrations add / database update` 不必启动整个 Api 进程。
///
/// 用法：
///   SQL Server（默认，走本机默认实例）
///     dotnet ef migrations add InitialCreate --project src/AuthHub.Infrastructure
///   SQLite
///     AUTHHUB_PROVIDER=Sqlite AUTHHUB_CONNECTION="Data Source=authhub.db" dotnet ef migrations add Xxx
///
/// 默认连接串与 appsettings.Development.json 保持一致，否则会出现
/// 「迁移加在 A 库、应用连 B 库」这种最难察觉的错位。
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AuthHubDbContext>
{
    public const string ProviderEnvironmentVariable = "AUTHHUB_PROVIDER";
    public const string ConnectionEnvironmentVariable = "AUTHHUB_CONNECTION";

    /// <summary>
    /// 本机 SQL Server 默认实例，走**共享内存**（<c>(local)</c>）。
    ///
    /// 这里刻意不写 <c>localhost</c>：SqlClient 把它解析成 TCP，而本机实例的 TCP 协议
    /// 通常是关的（1433 不监听），会得到"连接超时"而不是"实例不存在"这种更有指向性的报错。
    /// 改这段时记得同步 appsettings.Development.json。
    /// </summary>
    private const string DefaultSqlServerConnection =
        @"Server=(local);Database=AuthHub.Dev;Trusted_Connection=True;MultipleActiveResultSets=true;Encrypt=True;TrustServerCertificate=True";

    private const string DefaultSqliteConnection = "Data Source=authhub.design.db";

    public AuthHubDbContext CreateDbContext(string[] args)
    {
        var provider = Environment.GetEnvironmentVariable(ProviderEnvironmentVariable) ?? DatabaseProviders.SqlServer;
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);

        var builder = new DbContextOptionsBuilder<AuthHubDbContext>();

        if (DatabaseProviders.IsSqlite(provider))
        {
            builder.UseSqlite(connectionString ?? DefaultSqliteConnection);
        }
        else
        {
            builder.UseSqlServer(connectionString ?? DefaultSqlServerConnection);
        }

        // 与运行时保持一致，否则迁移会漏掉 OpenIddict 的表
        builder.UseOpenIddict();

        return new AuthHubDbContext(builder.Options);
    }
}
