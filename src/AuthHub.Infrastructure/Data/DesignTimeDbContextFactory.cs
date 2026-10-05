using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AuthHub.Infrastructure.Data;

/// <summary>
/// 设计时（dotnet ef）上下文工厂。
/// 让 `dotnet ef migrations add / database update` 不必启动整个 Api 进程。
///
/// 用法：
///   SQL Server（默认，走本机 LocalDB）
///     dotnet ef migrations add InitialCreate --project src/AuthHub.Infrastructure
///   SQLite
///     AUTHHUB_PROVIDER=Sqlite AUTHHUB_CONNECTION="Data Source=authhub.db" dotnet ef migrations add Xxx
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AuthHubDbContext>
{
    public const string ProviderEnvironmentVariable = "AUTHHUB_PROVIDER";
    public const string ConnectionEnvironmentVariable = "AUTHHUB_CONNECTION";

    private const string DefaultSqlServerConnection =
        @"Server=(localdb)\MSSQLLocalDB;Database=AuthHub.Dev;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True";

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
