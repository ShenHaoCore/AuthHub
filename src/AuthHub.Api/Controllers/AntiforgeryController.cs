using AuthHub.Api.Extensions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AuthHub.Api.Controllers;

/// <summary>/api 的防伪令牌发放：Cookie 认证变更端点的脚本接入入口。</summary>
/// <remarks>
/// - 用法：GET 本端点拿一次性令牌放进 X-CSRF-TOKEN 头，再调 Cookie 认证的变更端点；响应同时下发配对的 antiforgery Cookie（HttpOnly=false，脚本可读，单独拿到无效）。
/// - 令牌与当前身份绑定签发与校验，登录态变更后需重新获取。
/// </remarks>
[ApiController]
[Route("api/antiforgery")]
[Tags("防伪令牌")]
public sealed class AntiforgeryController : ControllerBase
{
    /// <summary>取一枚防伪令牌（配 X-CSRF-TOKEN 头使用）。</summary>
    [HttpGet("token")]
    [AllowAnonymous]
    [EndpointSummary("获取防伪令牌")]
    [EndpointDescription("返回 { token }，与 antiforgery Cookie 配对；调用 Cookie 认证的变更端点时放进 X-CSRF-TOKEN 头。已登录时令牌绑定当前身份，登录态变更后需重新获取。")]
    public async Task<IActionResult> GetToken(
        [FromServices] IAntiforgery antiforgery,
        [FromServices] IOptions<AntiforgeryOptions> antiforgeryOptions)
    {
        // [AllowAnonymous] 端点上认证中间件会跳过 authenticate（.NET 8+ 的优化），User 恒为匿名；
        // 而防伪令牌与当前身份绑定签发与校验 —— 必须显式认证，并把回填 User 一起做掉
        //（AuthenticateAsync 本身只返回结果，不写 HttpContext.User）。
        var authResult = await HttpContext.AuthenticateAsync(AuthHubSchemes.Cookie);
        if (authResult.Succeeded)
        {
            HttpContext.User = authResult.Principal!;
        }

        // 令牌与"当前认证身份"绑定生成与校验（claim-based EAD）。旧 antiforgery Cookie 可能
        // 是登录前签发的（绑定旧身份），原样复用会产出与当前用户不匹配的对子 —— 摘掉入口
        // Cookie 强制按当前身份重签。Request.Cookies 在首个中间件就被解析缓存，只改原始
        // Cookie 头不会刷新它，必须整体替换集合。未登录时摘不摘无差别（EAD 为空）。
        var cookieName = antiforgeryOptions.Value.Cookie.Name ?? "authhub.antiforgery";
        if (HttpContext.Request.Cookies.ContainsKey(cookieName))
        {
            HttpContext.Request.Cookies = new FilteredCookies(
                HttpContext.Request.Cookies.Where(pair => pair.Key != cookieName));
        }

        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new { token = tokens.RequestToken });
    }
}

/// <summary>按需剔除后的只读 Cookie 集合（给 HttpRequest.Cookies 赋值用）。</summary>
internal sealed class FilteredCookies(IEnumerable<KeyValuePair<string, string>> source) : IRequestCookieCollection
{
    private readonly Dictionary<string, string> _items = new(source, StringComparer.Ordinal);

    public string? this[string key] => _items.TryGetValue(key, out var value) ? value : null;

    public int Count => _items.Count;

    public ICollection<string> Keys => _items.Keys;

    public bool ContainsKey(string key) => _items.ContainsKey(key);

    public bool TryGetValue(string key, out string value)
    {
        return _items.TryGetValue(key, out value!);
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _items.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
