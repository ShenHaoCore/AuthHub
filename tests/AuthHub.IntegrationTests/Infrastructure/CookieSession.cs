using System.Net;
using Microsoft.AspNetCore.TestHost;

namespace AuthHub.IntegrationTests.Infrastructure;

/// <summary>
/// 极简的 Cookie 会话容器（测试专用）—— 每个实例就是一个"独立浏览器"。
///
/// **为什么不能用 <c>WebApplicationFactory.CreateClient()</c>**：
/// 同一个 factory 产出的 HttpClient 可能共用底层处理器与 Cookie 容器，
/// 而 <see cref="AuthHubFixture"/> 上那个共享 Client 被其它用例用来断言"未登录"
/// （例如 <c>ApiAuthorizationTests</c> 里那几条 401 / 403）。
/// 一旦后台 UI 的用例拿它登录，会话 Cookie 会残留下来污染全局状态，
/// 故障表现是"某个不相干的用例随机失败"，极难定位。
///
/// 因此这里从 <c>TestServer.CreateHandler()</c> 取一个干净的传输处理器，
/// 自己在内存里维护 Cookie jar —— 与共享 Client 完全隔离。
///
/// 关于重定向：TestServer 的处理器**不会**自动跟随 302，
/// 所以调用方要自己决定是断言 302、还是显式再发一次请求（这正是我们想要的）。
/// </summary>
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

    public Task<HttpResponseMessage> PostFormAsync(string path, IReadOnlyDictionary<string, string> fields)
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
