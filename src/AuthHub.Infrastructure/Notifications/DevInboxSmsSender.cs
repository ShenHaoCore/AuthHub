using AuthHub.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace AuthHub.Infrastructure.Notifications;

/// <summary>
/// Development 短信发送：写入 <see cref="SmsDevInbox"/> 并打日志，不真发短信。
/// </summary>
public sealed class DevInboxSmsSender : ISmsSender
{
    private readonly SmsDevInbox _inbox;
    private readonly ILogger<DevInboxSmsSender> _logger;

    public DevInboxSmsSender(SmsDevInbox inbox, ILogger<DevInboxSmsSender> logger)
    {
        _inbox = inbox;
        _logger = logger;
    }

    public Task SendAsync(string phoneNumber, string message, CancellationToken cancellationToken = default)
    {
        _inbox.Add(phoneNumber, message);
        _logger.LogWarning(
            "[开发环境短信] 手机号={PhoneNumber} 内容={Message}（可在 /dev/sms-inbox 查看）",
            phoneNumber,
            message);
        return Task.CompletedTask;
    }
}
