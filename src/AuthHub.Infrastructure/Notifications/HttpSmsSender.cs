using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthHub.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthHub.Infrastructure.Notifications;

/// <summary>
/// 通过可配置 HTTP 网关发短信（MFA Phone 等）。
/// POST JSON <c>{ "phoneNumber", "message" }</c>；厂商协议不同时在网关侧适配即可。
/// </summary>
public sealed class HttpSmsSender : ISmsSender
{
    public const string HttpClientName = "AuthHub.Sms";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SmsOptions _options;
    private readonly ILogger<HttpSmsSender> _logger;

    public HttpSmsSender(
        IHttpClientFactory httpClientFactory,
        IOptions<SmsOptions> options,
        ILogger<HttpSmsSender> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendAsync(string phoneNumber, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.HttpUrl))
        {
            throw new InvalidOperationException("AuthHub:Sms:HttpUrl 未配置，无法发送短信。");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.HttpUrl)
        {
            Content = JsonContent.Create(
                new { phoneNumber, message },
                options: JsonOptions)
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"短信网关返回 {(int)response.StatusCode}：{Truncate(body, 200)}");
        }

        _logger.LogInformation("短信已发送：手机号={Phone}", MaskPhone(phoneNumber));
    }

    private static string MaskPhone(string phone)
    {
        var digits = phone.Trim();
        if (digits.Length <= 4) return "****";
        return string.Concat(new string('*', digits.Length - 4), digits.AsSpan(digits.Length - 4));
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
