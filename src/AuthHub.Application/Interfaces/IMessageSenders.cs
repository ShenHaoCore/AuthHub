namespace AuthHub.Application.Interfaces;

/// <summary>
/// 邮件发送抽象。MFA 的 Email 通道、通知类邮件都走这里。
/// 默认实现是“写日志”的空实现，接入真实 SMTP / 云邮件服务时替换即可。
/// </summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default);
}

/// <summary>短信发送抽象，MFA 的 SMS 通道使用。</summary>
public interface ISmsSender
{
    Task SendAsync(string phoneNumber, string message, CancellationToken cancellationToken = default);
}
