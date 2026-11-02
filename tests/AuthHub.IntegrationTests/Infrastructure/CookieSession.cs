using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;

namespace AuthHub.IntegrationTests.Infrastructure;

/// <summary>
/// 极简的 Cookie 会话容器（测试专用）—— 每个实例就是一个"独立浏览器"。
/// </summary>
/// <remarks>
/// - 不能用 <c>WebApplicationFactory.CreateClient()</c>：同一 factory 产出的 HttpClient 可能共用底层处理器与
///   Cookie 容器，而 <see cref="AuthHubFixture"/> 上的共享 Client 被其它用例用来断言"未登录"
///   （例如 <c>ApiAuthorizationTests</c> 里那几条 401 / 403）。一旦后台 UI 的用例拿它登录，会话 Cookie
///   会残留下来污染全局状态，故障表现是"某个不相干的用例随机失败"，极难定位。
/// - 这里从 <c>TestServer.CreateHandler()</c> 取干净的传输处理器、自维护内存 Cookie jar，与共享 Client 完全隔离。
/// - TestServer 的处理器不会自动跟随 302：调用方自己决定是断言 302 还是显式再发一次请求（正是想要的）。
/// </remarks>
internal sealed class CookieSession : IDisposable
{
    private readonly CookieHandler _handler;
    private readonly HttpClient _client;

    public CookieSession(TestServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        _handler = new CookieHandler(server.CreateHandler());
        _client = new HttpClient(_handler)
        {
            BaseAddress = new Uri("http://localhost/")
        };
    }

    /// <summary>当前持有的 Cookie 名（用于断言"登录后确实拿到了会话 Cookie"）。</summary>
    public IReadOnlyCollection<string> CookieNames => _handler.Names;

    public Task<HttpResponseMessage> GetAsync(string path)
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, path));

    /// <summary>提交 JSON(PUT/PATCH 之外的便捷入口是 PostFormAsync,JSON 用于 /api/* 控制器)。</summary>
    /// <remarks>
    /// Cookie 认证的变更端点经 ApiAntiforgeryConvention 要求防伪令牌，
    /// 这里自动先取令牌再带 X-CSRF-TOKEN 头 —— 测试代码不必逐处手工附加。
    /// </remarks>
    public async Task<HttpResponseMessage> PostJsonAsync(string path, object payload)
        => await SendJsonAsync(HttpMethod.Post, path, payload);

    public async Task<HttpResponseMessage> PutJsonAsync(string path, object payload)
        => await SendJsonAsync(HttpMethod.Put, path, payload);

    public async Task<HttpResponseMessage> DeleteAsync(string path)
    {
        var token = await GetAntiforgeryTokenAsync();
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await _client.SendAsync(request);
    }

    /// <summary>测试诊断透传：直接发一个已构造好的请求。</summary>
    public Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request)
        => _client.SendAsync(request);

    /// <summary>不带防伪令牌直接提交 JSON —— 专供"缺令牌被拒"的反例用例。</summary>
    public Task<HttpResponseMessage> PostJsonWithoutAntiforgeryAsync(string path, object payload)
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload)
        });

    private async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, object payload)
    {
        var token = await GetAntiforgeryTokenAsync();
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await _client.SendAsync(request);
    }

    /// <summary>从 /api/antiforgery/token 取一枚请求令牌（同时下发配对的 antiforgery Cookie）。</summary>
    private async Task<string> GetAntiforgeryTokenAsync()
    {
        var response = await _client.GetAsync("/api/antiforgery/token");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>();
        return payload?.Token ?? throw new InvalidOperationException("防伪令牌响应缺少 token 字段。");
    }

    private sealed record TokenResponse(string Token);

    /// <summary>
    /// 提交表单。参数类型是 <see cref="IEnumerable{T}"/> 而不是字典 —— 复选框组
    /// （如角色权限弹窗里的 <c>SelectedPermissions</c>）需要重复的字段名，字典表达不了；
    /// <see cref="FormUrlEncodedContent"/> 本来就是按序列编码的。
    /// </summary>
    public Task<HttpResponseMessage> PostFormAsync(string path, IEnumerable<KeyValuePair<string, string>> fields)
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(fields)
        });

    public static async Task<string> ReadHtmlAsync(HttpResponseMessage response)
        => await response.Content.ReadAsStringAsync();

    public void Dispose() => _client.Dispose();

    /// <summary>
    /// 在请求上补 Cookie 头、在响应上收 Set-Cookie。
    /// 值为空视为删除 —— 登出正是靠 <c>Set-Cookie: name=; expires=...</c> 实现的。
    /// </summary>
    private sealed class CookieHandler : DelegatingHandler
    {
        private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

        public CookieHandler(HttpMessageHandler inner) : base(inner)
        {
        }

        public IReadOnlyCollection<string> Names => _cookies.Keys;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (_cookies.Count > 0 && !request.Headers.Contains("Cookie"))
            {
                request.Headers.Add(
                    "Cookie",
                    string.Join("; ", _cookies.Select(pair => $"{pair.Key}={pair.Value}")));
            }
            var response = await base.SendAsync(request, cancellationToken);

            if (response.Headers.TryGetValues("Set-Cookie", out var headers))
            {
                foreach (var header in headers)
                {
                    // 只取 "名字=值" 这一段；其后的 Path/Expires/HttpOnly 等属性不会出现在请求头里
                    var segment = header.Split(';', 2)[0];
                    var separator = segment.IndexOf('=', StringComparison.Ordinal);

                    if (separator <= 0)
                    {
                        continue;
                    }

                    var name = segment[..separator].Trim();
                    var value = segment[(separator + 1)..];

                    if (value.Length == 0)
                    {
                        _cookies.Remove(name);
                    }
                    else
                    {
                        _cookies[name] = value;
                    }
                }
            }

            return response;
        }
    }
}
