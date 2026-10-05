using AuthHub.Application.Interfaces;
using AuthHub.Domain.Entities;

namespace AuthHub.Application.Services;

/// <summary>
/// MFA 的邮件通道。
///
/// 文案刻意带上了「若非本人操作请立即修改密码」——邮件会长期留在收件箱里，
/// 与短信那种"往下翻就看不见"的媒介不同，多一句提示是有意义的。
/// </summary>
public sealed class EmailTwoFactorChannel : ITwoFactorChannel
{
    /// <summary>Identity 的默认邮件 provider 名。</summary>
    public const string ProviderName = "Email";

    private readonly IEmailSender _sender;

    public EmailTwoFactorChannel(IEmailSender sender) => _sender = sender;

    public string Provider => ProviderName;

    public string? ResolveTarget(ApplicationUser user) =>
        string.IsNullOrWhiteSpace(user.Email) ? null : user.Email;

    public Task SendAsync(string target, string code, CancellationToken cancellationToken) =>
        _sender.SendAsync(
            target,
            "【AuthHub】登录验证码",
            $"您的验证码是 {code}，5 分钟内有效。若非本人操作请立即修改密码。",
            cancellationToken);
}

/// <summary>
/// MFA 的短信通道。
///
/// 与邮件通道的差别不只是 sender：文案更短、没有主题、不带"改密码"的提示。
/// 这些差异原先散在 <c>AccountService</c> 的 <c>switch</c> 分支里，
/// 现在跟着通道走 —— 改短信文案不会再碰到业务类。
/// </summary>
public sealed class PhoneTwoFactorChannel : ITwoFactorChannel
{
    /// <summary>Identity 的默认短信 provider 名。</summary>
    public const string ProviderName = "Phone";

    private readonly ISmsSender _sender;

    public PhoneTwoFactorChannel(ISmsSender sender) => _sender = sender;

    public string Provider => ProviderName;

    public string? ResolveTarget(ApplicationUser user) =>
        string.IsNullOrWhiteSpace(user.PhoneNumber) ? null : user.PhoneNumber;

    public Task SendAsync(string target, string code, CancellationToken cancellationToken) =>
        _sender.SendAsync(
            target,
            $"【AuthHub】验证码 {code}，5 分钟内有效。",
            cancellationToken);
}
