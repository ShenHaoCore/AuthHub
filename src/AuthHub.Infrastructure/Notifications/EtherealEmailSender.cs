using AuthHub.Application.Common;
using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;
using AuthHub.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthHub.Infrastructure.Notifications;

/// <summary>动态生成 Ethereal 测试账号的邮件发送实现（仅用于本机自测）。</summary>
/// <remarks>
/// - 调 <c>https://api.nodemailer.com/user</c> 生成随机测试账号，邮件不真发到收件人、只留在 Ethereal，
///   可用生成的账号在 <c>https://ethereal.email/login</c> 查看完整正文。
/// - 账号在进程首次发信时懒生成，之后整个进程复用；Ethereal 账号设计上即一次性，重启生成新账号。
/// - 不写单元测试：核心逻辑是"调外部 API + SMTP 发信"两个薄包装，mock 成本高于被测代码，正确性靠本机启动实测。
/// </remarks>
public sealed class EtherealEmailSender : IEmailSender
{
    private const string CreateAccountEndpoint = "https://api.nodemailer.com/user";

    /// <summary>进程级共享 <see cref="HttpClient"/>，避免每次发信新建导致端口耗尽。</summary>
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly EmailOptions _options;
    private readonly ILogger<EtherealEmailSender> _logger;

    /// <summary>进程级账号的懒任务（异步版 Lazy 惯用写法：工厂立即返回 Task，不阻塞调用线程）；并发首次发信只调一次 API。</summary>
    private readonly Lazy<Task<EtherealAccount>> _accountTask;

    public EtherealEmailSender(IOptions<EmailOptions> options, ILogger<EtherealEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
        _accountTask = new Lazy<Task<EtherealAccount>>(
            CreateAccountAsync,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        // 首次发信时懒生成账号；之后整个进程复用同一组凭据
        var account = await _accountTask.Value;

        // Ethereal 要求 From 与认证账号一致，否则拒绝发信（防伪造）
        var from = account.User;
        using var message = new MailMessage(from, to)
        {
            Subject = subject,
            Body = body,
            IsBodyHtml = false
        };
        using var client = new SmtpClient(account.Smtp.Host, account.Smtp.Port)
        {
            // Ethereal 在 587 端口要求 STARTTLS 才允许认证；EnableSsl=true 时 BCL SmtpClient 会先 EHLO → STARTTLS → 升级 TLS → AUTH。
            EnableSsl = true,
            Timeout = _options.TimeoutSeconds * 1000,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Credentials = new NetworkCredential(account.User, account.Pass)
        };

        await client.SendMailAsync(message, cancellationToken);
        _logger.LogInformation("邮件已发送到 Ethereal：收件人={To} 主题={Subject}", LogSanitizer.Sanitize(to), LogSanitizer.Sanitize(subject));
    }

    /// <summary>调 Ethereal API 创建一组测试账号。失败时抛异常，由调用方捕获。</summary>
    private async Task<EtherealAccount> CreateAccountAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            var response = await SharedClient.PostAsJsonAsync(
                CreateAccountEndpoint,
                new { requestor = "AuthHub", version = "1.0.0" },
                cts.Token);
            response.EnsureSuccessStatusCode();

            var account = await response.Content.ReadFromJsonAsync<EtherealAccount>(JsonOptions, cts.Token);
            if (account is null || string.IsNullOrEmpty(account.User) || string.IsNullOrEmpty(account.Pass))
            {
                throw new InvalidOperationException("Ethereal 返回的账号信息不完整");
            }

            // Warning 级别确保在本机自测常见的 MinimumLevel=Information 下也能见到；明文打印符合 Ethereal 一次性测试凭据的设计。
            _logger.LogWarning(
                "Ethereal 测试账号已生成。本机自测可登录 https://ethereal.email/login 查看邮件：用户名={User} 密码={Pass}",
                account.User, account.Pass);

            return account;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "生成 Ethereal 账号失败。请检查网络是否能访问 api.nodemailer.com");
            throw;
        }
    }

    private sealed class EtherealAccount
    {
        public string Status { get; set; } = string.Empty;
        public string User { get; set; } = string.Empty;
        public string Pass { get; set; } = string.Empty;
        public string? Url { get; set; }
        public EtherealSmtpConfig Smtp { get; set; } = new();
    }

    private sealed class EtherealSmtpConfig
    {
        public string Host { get; set; } = "smtp.ethereal.email";
        public int Port { get; set; } = 587;
        public bool Secure { get; set; }
    }
}
