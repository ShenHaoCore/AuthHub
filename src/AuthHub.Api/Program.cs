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

// 角色 → 权限归属（授权三层里的「授权策略」层）。可被配置 AuthHub:RolePermissions 覆盖，
// 未配置的角色沿用 Domain 里的出厂默认；权限名写错在启动期就失败（见 RolePermissionOptionsValidator）。
builder.Services.AddAuthHubRolePermissions(builder.Configuration);

builder.Services.AddAuthHubRateLimiting(builder.Configuration);
builder.Services.AddAuthHubCors(builder.Configuration);
builder.Services.AddAuthHubWebApi(enableApiDocs);

// Data Protection 密钥环（会话 Cookie / 防伪令牌 / MFA 票据都依赖它）。
// 返回密钥环位置留给启动日志：容器里没挂卷时它落在镜像层内，重建即丢，
// 而症状是"所有人被登出 + 表单提交 400"，很难联想到密钥环。返回描述里带警告。
var dataProtectionDescription = builder.Services.AddAuthHubDataProtection(
    builder.Configuration, builder.Environment);

// 分层注册
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

var app = builder.Build();

// 强制求值一次角色 → 权限映射的配置：IOptions 是懒加载的，不主动取一次的话，
// 挂在它上面的配置校验要等第一个用户登录才跑，配置错误就落不到启动日志里了。
// 这里刻意安排在数据库初始化之前：配置错误应该比数据库问题更早、更清楚地报出来。
app.ValidateAuthHubStartupConfiguration();

// 环境一致性护栏：确认这份配置与它所在的环境自洽（开发配置被带到服务器上、
// 生产没改占位符、受保护环境却打开了接口文档……这类问题都是静默的）。
// 刻意排在数据库初始化之前 —— "连上了不该连的库"必须在建立连接之前就拦下来。
app.ValidateAuthHubEnvironment();

// ---------------------------------------------------------------------------
// 4. 数据库初始化（建库 / 迁移 + 种子数据，由配置开关控制）
// ---------------------------------------------------------------------------
await app.InitializeAuthHubDatabaseAsync();

// 预热权限归属的生效表（要读 RolePermissionOverrides 表，所以必须在迁移之后）。
// 少了这一步，读库的失败会推迟到"第一个用户登录"时才炸出来。
app.WarmUpAuthHubRolePermissions();

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

// 单独一行，因为这几项是"配错了也不会报错、只会在运行期以别的面目出现"的部署事实：
// 密钥环丢了对应用户被登出、信任范围配错了对应审计 IP 失真。
app.Logger.LogInformation(
    "运行时保护 | Data Protection 密钥环={DataProtectionDescription}",
    dataProtectionDescription);

app.Run();

/// <summary>
/// 供集成测试的 WebApplicationFactory&lt;Program&gt; 引用
/// （顶级语句生成的 Program 类默认是 internal，测试项目需要可访问的类型）。
/// </summary>
public partial class Program
{
}
