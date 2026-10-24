namespace AuthHub.Infrastructure.Data;

/// <summary>
/// 数据库提供程序常量与判断。
/// 生产用 SQL Server；PostgreSQL 与 SQL Server 平级（建库走 EnsureCreated，专用迁移集暂未提供）；
/// 开发/测试可用 SQLite（零配置，便于本地与 CI）。
/// </summary>
public static class DatabaseProviders
{
    public const string SqlServer = "SqlServer";
    public const string Sqlite = "Sqlite";
    public const string Postgres = "Postgres";

    public static bool IsSqlite(string? provider)
        => string.Equals(provider, Sqlite, StringComparison.OrdinalIgnoreCase);

    /// <summary>接受 <c>Postgres</c> 与 <c>PostgreSQL</c> 两种写法 —— 这两个名字在日常语境里混用，配置不该在这里挑刺。</summary>
    public static bool IsPostgres(string? provider)
        => string.Equals(provider, Postgres, StringComparison.OrdinalIgnoreCase)
           || string.Equals(provider, "PostgreSQL", StringComparison.OrdinalIgnoreCase);

    /// <summary>没有专用迁移集、启动建表只能走 EnsureCreated 的提供程序。</summary>
    public static bool UsesEnsureCreated(string? provider)
        => IsSqlite(provider) || IsPostgres(provider);
}
