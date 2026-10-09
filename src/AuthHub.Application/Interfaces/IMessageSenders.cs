namespace AuthHub.Application.Interfaces;

/// <summary>邮件发送抽象（确认信、重置密码、MFA Email）。实现：logging / smtp / ethereal-auto。</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default);
}

/// <summary>短信发送抽象（MFA Phone）。实现：logging / http 网关。</summary>
public interface ISmsSender
{
    Task SendAsync(string phoneNumber, string message, CancellationToken cancellationToken = default);
}
