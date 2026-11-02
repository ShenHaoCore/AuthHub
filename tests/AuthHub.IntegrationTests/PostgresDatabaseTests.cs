using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthHub.Infrastructure.Data;
using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AuthHub.IntegrationTests;

/// <summary>
/// PostgreSQL 提供程序（Database:Provider=Postgres）的端到端行为：换提供程序不是"能编译"就算数 ——
/// 把整条链路真正跑在 PG 上：EnsureCreated 建库建表（Identity + OpenIddict + 自定义表）→ 种子 →
/// m2m 客户端凭证签发 → 带 Bearer 调 /api/profile（令牌条目校验回查 PG）。任何一环对 PG 不兼容
/// （典型：DateTimeOffset 与 timestamptz 的映射规则）都会在这里炸出来。
/// </summary>
/// <remarks>
/// - 连接串来自环境变量 <c>AUTHHUB_TEST_POSTGRES</c>，未设时用默认值（与 CI 的 postgres 服务容器一致）。
///   本机没有可达的 PostgreSQL 时提前返回 —— 提供程序是可选项；CI 的服务容器保证这个用例在流水线上
///   始终真实执行。角色需要 CREATEDB 权限（EnsureCreated 要建出 GUID 命名的隔离库）。
/// - 隔离：每次运行用 GUID 命名的临时库，finally 里 EnsureDeleted，不碰实例上的任何既有数据库。
/// </remarks>
public class PostgresDatabaseTests
{
    private const string M2mClientId = AuthHubWebApplicationFactory.M2mClientId;
    private const string M2mClientSecret = AuthHubWebApplicationFactory.M2mClientSecret;
    private const string ApiPath = "/api/profile";
    private const string DefaultTestConnectionString = "Host=localhost;Username=postgres;Password=postgres;Include Error Detail=true";

    [Fact]
    public async Task Host_should_boot_seed_and_serve_on_postgres()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("AUTHHUB_TEST_POSTGRES")
                                   ?? DefaultTestConnectionString;
        if (!await TryPingPostgresAsync(baseConnectionString))
        {
            return;
        }

        var connectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = $"authhub_it_{Guid.NewGuid():N}"
        }.ConnectionString;

        var factory = new AuthHubWebApplicationFactory(new Dictionary<string, string>
        {
            ["Database__Provider"] = "Postgres",
            ["ConnectionStrings__DefaultConnection"] = connectionString
            // MigrateOnStartup 基线已是 true → PG 走 EnsureCreated；Seeding__Enabled 基线已是 true
        });

        try
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("http://localhost/")
            });

            var tokenResponse = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = M2mClientId,
                    ["client_secret"] = M2mClientSecret,
                    ["scope"] = "api:read"
                }));

            tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK,
                "种子客户端在 PG 上应能正常走客户端凭证流程");

            var token = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync())
                .RootElement.GetProperty("access_token").GetString()!;

            var request = new HttpRequestMessage(HttpMethod.Get, ApiPath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var apiResponse = await client.SendAsync(request);

            apiResponse.StatusCode.Should().Be(HttpStatusCode.OK,
                "整条链路（Npgsql 建库建表 → 种子 → 签发 → 令牌条目校验回查）都落在 PostgreSQL 上");
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AuthHubDbContext>().Database.EnsureDeletedAsync();
            factory.Dispose();
        }
    }

    private static async Task<bool> TryPingPostgresAsync(string connectionString)
    {
        try
        {
            // ping 连维护库 postgres —— 连接串里不指定 Database 时 Npgsql 默认连与用户同名的库，
            // 那个库未必存在（用例的真实库由 EnsureCreated 按 GUID 名新建）
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Database = "postgres",
                Timeout = 3
            };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            return true;
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            return false;
        }
    }
}
