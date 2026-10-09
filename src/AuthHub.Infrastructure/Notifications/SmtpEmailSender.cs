using System.Net;
using System.Net.Mail;
using AuthHub.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthHub.Infrastructure.Notifications;

/// <summary>
/// 基于 <see cref="SmtpClient"/> 的真实邮件发送实现。
///
/// <para>
/// 为什么用 BCL 的 <c>SmtpClient</c> 而不是 MailKit：本项目只发纯文本/HTML 通知邮件，
/// 不需要 MIME 高级特性；BCL 已满足需求，少一个三方依赖。注意 <c>SmtpClient</c> 在
/// 现代 .NET 里有过时警告，但功能完整、微软仍维护，对纯文本发信足够。
/// </para>
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        var fromAddress = string.IsNullOrWhiteSpace(_options.FromAddress) ? _options.UserName : _options.FromAddress;
        var from = string.IsNullOrWhiteSpace(_options.FromName)
            ? new MailAddress(fromAddress)
            : new MailAddress(fromAddress, _options.FromName);
        using var message = new MailMessage(from, new MailAddress(to))
        {
            Subject = subject,
            Body = body,
            IsBodyHtml = false
        };

        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.EnableSsl,
            Timeout = _options.TimeoutSeconds * 1000,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Credentials = new NetworkCredential(_options.UserName, _options.Password)
        };

        // 异常向上抛：调用方（AccountService）统一捕获并落审计/日志，不让邮件失败拖垮主流程。
        await client.SendMailAsync(message, cancellationToken);
        _logger.LogInformation("邮件已发送：收件人={To} 主题={Subject}", to, subject);
    }
}
