namespace AuthHub.Infrastructure.Notifications;

/// <summary>
/// 短信发送配置（AuthHub:Sms）。
/// Provider：logging（默认；Development 自动用内存收件箱 /dev/sms-inbox）/ http。
/// </summary>
public sealed class SmsOptions
{
    public const string SectionName = "AuthHub:Sms";

    public string Provider { get; set; } = "logging";

    /// <summary>HTTP 网关地址。请求体为 JSON：{ "phoneNumber", "message" }。</summary>
    public string HttpUrl { get; set; } = string.Empty;

    /// <summary>可选 Bearer Token，经环境变量 AuthHub__Sms__ApiKey 注入。</summary>
    public string ApiKey { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 30;
}
