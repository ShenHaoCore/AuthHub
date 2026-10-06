using AuthHub.Api.Extensions;
using AuthHub.Api.Middleware;
using AuthHub.Application;
using AuthHub.Domain.Constants;
using AuthHub.Infrastructure;
using AuthHub.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// 1. 结构化日志（Serilog：Console + File，生产可追加 Seq / ELK Sink）
// ---------------------------------------------------------------------------
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// ---------------------------------------------------------------------------
// 2. 运行期配置
// ---------------------------------------------------------------------------
// 这里只读「管道装配与启动日志」要用的那几项；其余配置由需要的扩展方法就近读取，
// 免得所有配置项都堆在这个文件里。
var databaseProvider = builder.Configuration["Database:Provider"] ?? DatabaseProviders.SqlServer;
var requireHttps = builder.Configuration.GetValue("AuthHub:Security:RequireHttps", true);
var enablePasswordFlow = builder.Configuration.GetValue("AuthHub:Features:EnablePasswordFlow", false);
var enableApiDocs = builder.Configuration.GetValue("AuthHub:Features:EnableApiDocs", builder.Environment.IsDevelopment());

// ---------------------------------------------------------------------------
// 3. 服务注册
// ---------------------------------------------------------------------------
// 每一块的注册内容都在 Extensions/*.cs 里，这里只保留「装配顺序」。
// 有三处顺序带语义，别调换：
//   ① AddAuthHubIdentity 要在 AddAuthHubAuthentication 之前 —— Identity 会先把默认方案
//      指向会话 Cookie，再由后者改写；
//   ② AddAuthHubAuthentication 要在 AddAuthHubOpenIddict 之后 —— 两者都会配置
//      AuthenticationOptions 的默认方案，后配置者生效；
//   ③ 替换 IAuthorizationMiddlewareResultHandler 要在 AddAuthHubAuthorization 之后。
builder.Services.AddAuthHubPersistence(builder.Configuration);
builder.Services.AddAuthHubIdentity(builder.Configuration);

// 返回签名 / 加密密钥的形态描述，留给启动日志
var keyDescription = builder.Services.AddAuthHubOpenIddict(builder.Configuration, builder.Environment);

builder.Services.AddAuthHubAuthentication();
builder.Services.AddAuthHubAuthorization();

// 替换掉的默认实现：让 /api/* 的 401/403 由我们统一写成 ProblemDetails。
// 用 Replace 而不是直接 AddSingleton，是为了不依赖“后注册覆盖先注册”的隐式约定。
// 背景（多方案 Challenge/Forbid 导致的状态码二次写入异常）见 AuthHubAuthorizationResultHandler。
builder.Services.Replace(ServiceDescriptor.Singleton<
    IAuthorizationMiddlewareResultHandler,
    AuthHubAuthorizationResultHandler>());

builder.Services.AddAuthHubRateLimiting(builder.Configuration);
builder.Services.AddAuthHubCors(builder.Configuration);
builder.Services.AddAuthHubWebApi(enableApiDocs);

// 分层注册
builder.Services.AddApplication();
builder.Services.AddInfrastructure();

var app = builder.Build();

// ---------------------------------------------------------------------------
// 4. 数据库初始化（建库 / 迁移 + 种子数据，由配置开关控制）
// ---------------------------------------------------------------------------
await app.InitializeAuthHubDatabaseAsync();

// ---------------------------------------------------------------------------
// 5. 中间件管道与端点
// ---------------------------------------------------------------------------
app.UseAuthHubPipeline(requireHttps);
app.MapAuthHubEndpoints(enableApiDocs);

app.Logger.LogInformation(
    "AuthHub 启动完成 | 环境={Environment} | 数据库={Provider} | 密钥={KeyDescription} | 强制HTTPS={RequireHttps} | 密码流程={PasswordFlow} | API文档={ApiDocs}",
    app.Environment.EnvironmentName,
    databaseProvider,
    keyDescription,
    requireHttps,
    enablePasswordFlow,
    enableApiDocs);

app.Run();

/// <summary>
/// 供集成测试的 WebApplicationFactory&lt;Program&gt; 引用
/// （顶级语句生成的 Program 类默认是 internal，测试项目需要可访问的类型）。
/// </summary>
public partial class Program
{
}
