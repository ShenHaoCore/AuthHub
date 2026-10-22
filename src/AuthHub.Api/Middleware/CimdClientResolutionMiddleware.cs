using System.Text.Json;
using AuthHub.Application.Interfaces;
using AuthHub.Application.Options;
using Microsoft.Extensions.Options;

namespace AuthHub.Api.Middleware;

/// <summary>
/// 把 URL 型 <c>client_id</c> 在进入 OpenIddict 校验链之前变成一条可用的应用记录（CIMD 懒注册）。
///
/// <para><b>为什么是中间件而不是 OpenIddict 的处理器</b>：OpenIddict 的授权请求校验第一步
/// 就是 <c>FindByClientIdAsync</c>，查不到直接 <c>invalid_client</c>。它的处理器链虽然可以插入
/// 自定义环节，但"在哪个内置处理器之前"取决于 OpenIddict 的内部排序常量 —— 那个顺序在
/// 小版本升级里都可能变。中间件挂在认证中间件之前是**管道层面**的先后关系，
/// 与库的内部实现解耦（OpenIddict 的端点由认证中间件处理，见
/// <c>WebApplicationExtensions.UseAuthHubPipeline</c> 的注释）。</para>
///
/// <para><b>为什么只拦授权端点</b>：令牌端点（兑换、刷新）里的 client_id 一定在授权阶段
/// 已经注册过；client_credentials 与 CIMD 无关（自动注册的客户端一律是 public，
/// 拿不到密钥）。多拦一处只会多一次出站请求。</para>
/// </summary>
public sealed class CimdClientResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<CimdClientResolutionMiddleware> _logger;

    public CimdClientResolutionMiddleware(
        RequestDelegate next,
        ILogger<CimdClientResolutionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ICimdClientRegistrar registrar,
        IOptions<CimdOptions> options)
    {
        if (!options.Value.Enabled || !IsAuthorizationEndpoint(context.Request))
        {
            await _next(context);
            return;
        }

        var clientId = await ReadClientIdAsync(context);

        if (string.IsNullOrEmpty(clientId))
        {
            await _next(context);
            return;
        }

        var resolution = await registrar.ResolveAsync(clientId, context.RequestAborted);

        if (resolution.Status == CimdResolutionStatus.Rejected)
        {
            _logger.LogInformation(
                "CIMD 授权请求被拒 | ClientId={ClientId} | 原因={Reason}",
                clientId,
                resolution.Error);

            await WriteInvalidClientAsync(context, resolution.Error ?? "客户端注册无效。");
            return;
        }

        await _next(context);
    }

    private static bool IsAuthorizationEndpoint(HttpRequest request) =>
        request.Path.Equals("/connect/authorize", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 取 <c>client_id</c>。POST 是同意页的表单回传，此时 client_id 在表单里；
    /// GET 在查询串里。
    ///
    /// <para>这里读表单不会影响后续读取：ASP.NET Core 的 <c>FormFeature</c> 会缓存解析结果，
    /// OpenIddict 之后取 <c>Request.Form</c> 拿到的是同一份缓存，不会因为 body 已被消费而变空。</para>
    /// </summary>
    private static async Task<string?> ReadClientIdAsync(HttpContext context)
    {
        if (!context.Request.HasFormContentType)
        {
            return context.Request.Query["client_id"].ToString();
        }

        try
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            return form["client_id"].ToString();
        }
        catch (InvalidDataException)
        {
            // 表单格式错误时交给 OpenIddict 去报协议错误，不在这里抢答
            return null;
        }
    }

    /// <summary>
    /// 拒绝时的响应形状与 <c>AuthorizationController.OAuthError</c> 保持一致。
    ///
    /// <para>这里**不能**重定向回 <c>redirect_uri</c>：那个地址尚未经过白名单校验，
    /// 重定向等于把本服务变成一个开放重定向器。</para>
    /// </summary>
    private static Task WriteInvalidClientAsync(HttpContext context, string description)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json; charset=utf-8";

        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = "invalid_client",
            error_description = description
        }));
    }
}
