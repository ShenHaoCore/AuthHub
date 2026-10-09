namespace AuthHub.Infrastructure.Notifications;

/// <summary>
/// 邮件发送配置（AuthHub:Email）。
/// Provider：logging（默认）/ smtp / ethereal-auto（仅 Development）。
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "AuthHub:Email";

    public string Provider { get; set; } = "logging";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool EnableSsl { get; set; } = true;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "AuthHub";
    public int TimeoutSeconds { get; set; } = 30;
}
