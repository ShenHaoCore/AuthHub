using AuthHub.Application.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using AuthHub.Application.Interfaces;
using AuthHub.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthHub.Infrastructure.Security;

/// <summary>带 SSRF 防护的 CIMD 元数据拉取器，手工逐跳跟随重定向。</summary>
/// <remarks>
/// - 防护两层缺一不可：URL 层仅 https、无凭据、无片段、端口 443（<see cref="CimdAddressPolicy.IsAllowedMetadataUri"/>）；
///   连接层每次建连前解析域名，要求所有解析结果都是公网地址并用解析出的 IP 直连（<see cref="ConnectPublicOnlyAsync"/>）。
/// - 连接层才是防 DNS rebinding 的关键：公网域名可解析到内网 IP 而URL 层完全合规；
///   这里建连拿的是已解析 IP 且同一 socket 完成，没有"校验后再解析一次"的替换时间窗。
/// - 重定向不交给 HttpClient：AllowAutoRedirect 的中间跳不会重新过 URL 层校验（可降级 http、可跳内网）；
///   这里手工逐跳跟随、每跳重校验，超过 <c>MaxRedirects</c> 立即失败。
/// </remarks>
public sealed class CimdMetadataFetcher : ICimdMetadataFetcher, IDisposable
{
    /// <summary>出站请求的 User-Agent：让被请求方能从日志里认出这是谁在拉。</summary>
    private const string UserAgent = "AuthHub-CIMD/1.0";

    private readonly CimdOptions _options;
    private readonly ILogger<CimdMetadataFetcher> _logger;
    private readonly HttpClient _client;

    public CimdMetadataFetcher(IOptions<CimdOptions> options, ILogger<CimdMetadataFetcher> logger)
    {
        _options = options.Value;
        _logger = logger;

        var handler = new SocketsHttpHandler
        {
            // 重定向自己跟，才能逐跳校验（见类注释）
            AllowAutoRedirect = false,
            // 元数据文档不需要会话；带上 Cookie 容器反而制造跨站状态
            UseCookies = false,
            // 不解压：压缩炸弹是"用小响应换大内存"的经典手法，而元数据文档本来就该很小
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = _options.FetchTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectPublicOnlyAsync
        };

        // 超时由每次拉取的 CancellationToken 统一控制（覆盖全部重定向跳），
        // 必须关掉 HttpClient 自带的超时，避免两个超时口径不一致
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<CimdFetchResult> FetchAsync(Uri metadataUri, CancellationToken cancellationToken = default)
    {
        if (!CimdAddressPolicy.IsAllowedMetadataUri(metadataUri))
        {
            return CimdFetchResult.Failure(
                "元数据地址必须是使用 443 端口、不含凭据与片段的 HTTPS 绝对地址。");
        }

        if (!IsHostAllowed(metadataUri))
        {
            return CimdFetchResult.Failure($"域名 {metadataUri.Host} 不在允许自动注册的名单内。");
        }

        // 一个超时预算覆盖"全部重定向跳"，避免每跳各自计时被拖成 N 倍
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.FetchTimeout);

        var current = metadataUri;

        try
        {
            for (var hop = 0; hop <= _options.EffectiveMaxRedirects; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.UserAgent.ParseAdd(UserAgent);

                using var response = await _client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

                if (IsRedirect(response.StatusCode))
                {
                    var next = ResolveRedirect(current, response);
                    if (next is null)
                    {
                        return CimdFetchResult.Failure("元数据地址返回了不可跟随的重定向。");
                    }

                    if (!CimdAddressPolicy.IsAllowedMetadataUri(next) || !IsHostAllowed(next))
                    {
                        return CimdFetchResult.Failure($"重定向目标 {next} 不被允许。");
                    }

                    _logger.LogDebug("CIMD 元数据重定向 | 从={From} | 到={To}", LogSanitizer.Sanitize(current.ToString()), LogSanitizer.Sanitize(next.ToString()));
                    current = next;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return CimdFetchResult.Failure(
                        $"元数据地址返回 {(int)response.StatusCode}。");
                }

                if (!IsJson(response.Content.Headers.ContentType))
                {
                    return CimdFetchResult.Failure("元数据响应不是 application/json。");
                }

                var declaredLength = response.Content.Headers.ContentLength;
                if (declaredLength > _options.EffectiveMaxMetadataBytes)
                {
                    return CimdFetchResult.Failure(
                        $"元数据超过 {_options.EffectiveMaxMetadataBytes} 字节上限。");
                }

                var document = await ReadLimitedAsync(response.Content, timeout.Token);
                return document is null
                    ? CimdFetchResult.Failure($"元数据超过 {_options.EffectiveMaxMetadataBytes} 字节上限。")
                    : CimdFetchResult.Success(document);
            }

            return CimdFetchResult.Failure(
                $"重定向超过 {_options.EffectiveMaxRedirects} 跳上限。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CimdFetchResult.Failure($"拉取元数据超时（{_options.FetchTimeout.TotalSeconds} 秒）。");
        }
        catch (HttpRequestException ex)
        {
            // 连接层拒绝（私网地址、TLS 失败、DNS 失败）都到这里；只记消息不记堆栈 —— 这是"被拒绝"不是程序出错。
            return CimdFetchResult.Failure($"无法拉取元数据：{ex.Message}");
        }
    }

    public void Dispose() => _client.Dispose();

    // ------------------------------------------------------------------ 连接层防护

    /// <summary>
    /// 只在解析结果全部为公网地址时建连并用解析出的 IP 直连 —— 整套 SSRF 防护的实际生效点：
    /// 上层所有 URL 校验都可被"公网域名 + 内网 A 记录"绕过，只有这里拿到的是即将建连的那个 IP。
    /// </summary>
    private static async ValueTask<Stream> ConnectPublicOnlyAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;

        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);

        if (addresses.Length == 0)
        {
            throw new HttpRequestException($"域名 {host} 没有解析到任何地址。");
        }

        foreach (var address in addresses)
        {
            if (!CimdAddressPolicy.IsPublic(address))
            {
                throw new HttpRequestException(
                    $"域名 {host} 解析到非公网地址 {address}，已拒绝连接。");
            }
        }

        // 解析结果已确认全部公网，取第一个建连（失败由 HttpClient 的上层逻辑处理）
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(
                new IPEndPoint(addresses[0], context.DnsEndPoint.Port), cancellationToken);

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------ 内部辅助

    private bool IsHostAllowed(Uri uri)
    {
        if (_options.AllowedHosts.Count == 0)
        {
            return true;
        }

        return _options.AllowedHosts.Any(host =>
            string.Equals(host?.Trim(), uri.Host, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static Uri? ResolveRedirect(Uri current, HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        if (location is null)
        {
            return null;
        }

        // Location 允许相对地址；相对解析后仍要过一遍完整校验（协议可能被降级成 http）
        return location.IsAbsoluteUri ? location : new Uri(current, location);
    }

    private static bool IsJson(MediaTypeHeaderValue? contentType)
    {
        if (contentType is null)
        {
            return false;
        }

        var mediaType = contentType.MediaType;
        return mediaType is not null
               && (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
                   || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>边读边计数，超上限立刻放弃；先读成字符串再判长度时内存已经被吃掉了。</summary>
    private async Task<string?> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var limit = _options.EffectiveMaxMetadataBytes;

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();

        var chunk = new byte[8 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
