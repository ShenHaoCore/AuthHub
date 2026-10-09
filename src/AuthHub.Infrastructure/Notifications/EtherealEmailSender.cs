using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;
using AuthHub.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthHub.Infrastructure.Notifications;

/// <summary>
/// 启动期动态生成 Ethereal 测试账号的邮件发送实现（仅用于本机自测）。
///
/// <para>
/// 调 <c>https://api.nodemailer.com/user</c> 生成一组随机测试账号，邮件不真发到收件人，
/// 只在 Ethereal 服务器上留存，可用生成的账号在 <c>https://ethereal.email/login</c>
/// 查看完整邮件正文（含重置链接 / 邮箱确认链接）。
/// </para>
///
/// <para>
/// 账号在进程第一次发信时按需生成（懒加载，避免无人发信时白白消耗一个），
/// 之后整个进程复用同一组账号。Ethereal 账号设计上即一次性，重启进程会生成新账号，
/// 旧账号保留到 Ethereal 自动回收（约几天到几周）。
/// </para>
///
/// <para>
/// <b>为什么不写单元测试</b>：核心逻辑是"调外部 API + SMTP 发信"两个薄包装，
/// mock HTTP/Smtp 的成本高于被测代码本身，价值低；正确性靠本机启动实测验证。
/// </para>
/// </summary>
public sealed class EtherealEmailSender : IEmailSender
{
    private const string CreateAccountEndpoint = "https://api.nodemailer.com/user";

    /// <summary>
    /// 进程级共享 <see cref="HttpClient"/>：避免每次发信新建一个导致端口耗尽。
    /// Ethereal API 响应快（&lt;1s），15 秒超时已足够。
    /// </summary>
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Ethereal API 返回 JSON 用 camelCase，统一一份反序列化选项避免每处都传参。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly EmailOptions _options;
    private readonly ILogger<EtherealEmailSender> _logger;
    private readonly Lazy<EtherealAccount> _account;

    public EtherealEmailSender(IOptions<EmailOptions> options, ILogger<EtherealEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
        // ExecutionAndPublication：线程安全 + 只调一次 API。多线程并发首次发信时只生成一个账号。
        // 用同步 Lazy（而非异步 Lazy）是为了让 SendAsync 在拿到账号后能干净地 await 后续 SmtpClient；
        // 同步阻塞会占用一个线程池线程约 1 秒（API 响应时间），本机自测场景完全可接受。
        _account = new Lazy<EtherealAccount>(CreateAccount, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        // Lazy.Value 在 .NET 9 上线程安全（ExecutionAndPublication 模式），首次访问会同步执行工厂函数
        var account = _account.Value;

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
            // Ethereal 在 587 端口要求 STARTTLS 才允许认证；明文连接会被拒（"authentication Required"）。
            // BCL SmtpClient 在 EnableSsl=true + 587 端口会先 EHLO → 收到 STARTTLS → 升级 TLS → 再 AUTH。
            EnableSsl = true,
            Timeout = _options.TimeoutSeconds * 1000,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Credentials = new NetworkCredential(account.User, account.Pass)
        };

        await client.SendMailAsync(message, cancellationToken);
        _logger.LogInformation("邮件已发送到 Ethereal：收件人={To} 主题={Subject}", to, subject);
    }

    /// <summary>调 Ethereal API 创建一组测试账号。失败时抛异常，由调用方捕获。</summary>
    private EtherealAccount CreateAccount()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var response = SharedClient.PostAsJsonAsync(
                CreateAccountEndpoint,
                new { requestor = "AuthHub", version = "1.0.0" },
                cts.Token).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            var account = response.Content.ReadFromJsonAsync<EtherealAccount>(JsonOptions, cts.Token).GetAwaiter().GetResult();
            if (account is null || string.IsNullOrEmpty(account.User) || string.IsNullOrEmpty(account.Pass))
            {
                throw new InvalidOperationException("Ethereal 返回的账号信息不完整");
            }

            // 用 Warning 级别：本机自测通常 MinimumLevel=Information，确保能见到；
            // 账号明文打印符合 Ethereal 设计（一次性测试凭据，本来就是给开发者看的）
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

    /// <summary>Ethereal API 返回的账号结构。属性按 camelCase 反序列化。</summary>
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
