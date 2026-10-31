using AuthHub.Api.Filters;
using AuthHub.Api.Middleware;
using AuthHub.Api.Models;
using AuthHub.Api.Services;
using AuthHub.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

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
            // [ApiController] 的绑定失败默认走框架自己的 ValidationProblemDetails（英文标题、无 code 字段），
            // 与 FluentValidation / 认证失败的出口形成两套 400 契约 —— 这里统一换成 ApiProblemWriter。
            options.InvalidModelStateResponseFactory = ApiProblemWriter.InvalidModelStateResponse;
        })
        .AddJsonOptions(options =>
        {
            // 枚举以字符串输出，便于前端与日志阅读
            options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        });

        // /api 的防伪约定：Cookie 认证的变更端点（POST/PUT/DELETE/PATCH）自动要求防伪令牌，
        // 未来新增控制器无需记得手动挂 [ValidateAntiForgeryToken]。匿名端点豁免（没有可被
        // CSRF 滥用的 ambient 凭证，JSON body 也挡掉跨站表单伪造），GET 按约定不产生副作用。
        services.Configure<MvcOptions>(options => options.Conventions.Add(new ApiAntiforgeryConvention()));

        // 管理后台（Razor Pages）：页面多、表格与表单密集，继续手写 HTML 会产生数千行字符串拼接。
        // 协议页（登录 / 两步验证 / 同意授权）保持字符串产出、零视图依赖，不受影响。
        // Razor Pages 的 POST 处理器默认自动校验防伪令牌，无需逐个挂 [ValidateAntiForgeryToken]。
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
