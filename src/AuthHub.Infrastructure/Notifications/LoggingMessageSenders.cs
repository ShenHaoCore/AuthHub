using AuthHub.Application.Common;
using AuthHub.Application.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AuthHub.Infrastructure.Notifications;

/// <summary>
/// 邮件发送的“写日志”实现（email provider 未配置时的兜底）。
///
/// <para>为什么不是空实现：开发环境需要能拿到邮箱确认链接 / MFA 验证码，直接打进日志即可自测；
/// 接入真实 SMTP / 云邮件服务时，只需替换本类的注册，业务代码无需改动。</para>
///
/// <para>按环境脱敏：受保护环境（非 Development）只记收件人 / 主题 / 正文长度 ——
/// 正文里是重置与邮箱确认链接（内含一次性令牌），打进日志会被采集系统长期留存；
/// 本类是 provider 未配置时的默认注册，生产忘配邮件时链接必须仍然落不进日志。
/// 开发环境保留完整正文，自测体验不变。</para>
/// </summary>
public sealed class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;
    private readonly bool _isDevelopment;

    public LoggingEmailSender(ILogger<LoggingEmailSender> logger, IHostEnvironment environment)
    {
        _logger = logger;
        _isDevelopment = environment.IsDevelopment();
    }

    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        if (_isDevelopment)
        {
            _logger.LogWarning("[开发环境邮件] 收件人={To} 主题={Subject} 正文={Body}",
                LogSanitizer.Sanitize(to), LogSanitizer.Sanitize(subject), LogSanitizer.Sanitize(body));
        }
        else
        {
            // 受保护环境：正文与令牌不落日志；长度足够排查"信到底有没有构造出来"
            _logger.LogWarning("[邮件兜底通道] 收件人={To} 主题={Subject} 正文长度={Length}（正文含一次性链接，不落日志）",
                LogSanitizer.Sanitize(to), LogSanitizer.Sanitize(subject), body?.Length ?? 0);
        }

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
