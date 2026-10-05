using AuthHub.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace AuthHub.Infrastructure.Notifications;

/// <summary>
/// 邮件发送的“写日志”实现。
///
/// 为什么不是空实现：开发环境需要能拿到邮箱确认链接 / MFA 验证码，
/// 直接打进日志即可自测；接入真实 SMTP / 云邮件服务时，
/// 只需替换本类的注册，业务代码无需改动。
///
/// 注意：为避免验证码被日志采集系统长期留存，可在此处按环境做脱敏。
/// </summary>
public sealed class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;

    public LoggingEmailSender(ILogger<LoggingEmailSender> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("[开发环境邮件] 收件人={To} 主题={Subject}\n{Body}", to, subject, body);
        return Task.CompletedTask;
    }
}

/// <summary>短信发送的“写日志”实现，同样可直接替换为真实短信服务。</summary>
public sealed class LoggingSmsSender : ISmsSender
{
    private readonly ILogger<LoggingSmsSender> _logger;

    public LoggingSmsSender(ILogger<LoggingSmsSender> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(string phoneNumber, string message, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("[开发环境短信] 手机号={PhoneNumber} 内容={Message}", phoneNumber, message);
        return Task.CompletedTask;
    }
}
