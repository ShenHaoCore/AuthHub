namespace AuthHub.Infrastructure.Data;

/// <summary>
/// 数据库提供程序常量与判断。
/// 生产用 SQL Server，开发/测试可用 SQLite（零配置，便于本地与 CI）。
/// </summary>
public static class DatabaseProviders
{
    public const string SqlServer = "SqlServer";
    public const string Sqlite = "Sqlite";

    public static bool IsSqlite(string? provider)
        => string.Equals(provider, Sqlite, StringComparison.OrdinalIgnoreCase);
}
