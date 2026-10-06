using AuthHub.Api.Filters;
using AuthHub.Api.Middleware;
using AuthHub.Api.Models;
using AuthHub.Api.Services;
using AuthHub.Application.Interfaces;

namespace AuthHub.Api.Extensions;

/// <summary>
/// HTTP 层自身的注册：MVC 控制器、Razor Pages、错误响应契约、健康检查、API 文档，
/// 以及当前用户访问器与两个自定义中间件。
/// </summary>
internal static class WebApiExtensions
{
    public static IServiceCollection AddAuthHubWebApi(
        this IServiceCollection services,
        bool enableApiDocs)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

        services.AddTransient<ExceptionHandlingMiddleware>();
        services.AddTransient<SecurityHeadersMiddleware>();

        // 注意用的是 AddControllersWithViews() 而不是 AddControllers()：
        // 登录 / 两步验证表单上的 [ValidateAntiForgeryToken] 由 MVC ViewFeatures 里的
        // ValidateAntiforgeryTokenAuthorizationFilter 实现，该服务只在带视图的 MVC 构建器中注册。
        services.AddControllersWithViews(options =>
        {
            // FluentValidation 自动校验（规则集中定义在 Application 层）
            options.Filters.Add<ValidationFilter>();
        })
        .ConfigureApiBehaviorOptions(options =>
        {
            // [ApiController] 的**请求体绑定失败**默认走框架自己的 ValidationProblemDetails：
            // 英文标题、没有 code 字段、traceId 用的是 W3C traceparent。于是同一个服务里
            // 出现两套 400 契约 —— 下游按 code 分支时会静默漏掉一半。
            // 这里换成与 FluentValidation 失败、认证失败、500 完全相同的出口，见 ApiProblemWriter。
            options.InvalidModelStateResponseFactory = ApiProblemWriter.InvalidModelStateResponse;
        })
        .AddJsonOptions(options =>
        {
            // 枚举以字符串输出，便于前端与日志阅读
            options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        });

        // 管理后台页面（Razor Pages）。
        //
        // 本项目的浏览器页面分两类：
        //   1) 协议页（登录 / 两步验证 / 同意授权）：OIDC 流程的一部分，由 Pages/HtmlPages.cs
        //      用字符串产出 HTML，零视图依赖，便于集成测试直接断言页面内容；
        //   2) 管理后台：页面多、表格与表单密集，用 Razor Pages + 共享布局
        //      （Pages/Shared/_AdminLayout.cshtml）。这类页面继续手写 HTML 会产生数千行
        //      字符串拼接，因此这里引入视图引擎 —— 这是对早期"零视图依赖"取舍的一次修订，
        //      协议页保持原样不受影响。
        //
        // 安全性：Razor Pages 的 POST 处理器默认自动校验防伪令牌，
        // 无需逐个挂 [ValidateAntiForgeryToken]（这正是当初选 AddControllersWithViews 的原因）。
        services.AddRazorPages();

        services.AddProblemDetails();
        services.AddHealthChecks();

        if (enableApiDocs)
        {
            services.AddAuthHubOpenApi();
        }

        return services;
    }
}
