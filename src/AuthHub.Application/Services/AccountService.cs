using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Account;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Entities;
using AuthHub.Domain.Enums;
using AuthHub.Domain.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace AuthHub.Application.Services;

/// <summary>
/// 账号业务：注册、两阶段登录（密码 + MFA）、改密、MFA 生命周期管理。
/// 只依赖 Identity 的抽象（UserManager / SignInManager），不依赖 HTTP，
/// 因此可以被单元测试直接驱动。
///
/// 本类按职责拆为 partial class 多个文件（接口与公开契约完全不变）：
///   - AccountService.cs           主文件：字段、构造函数、共享辅助方法
///   - AccountService.Login.cs     注册与登录（两阶段）及登出
///   - AccountService.TwoFactor.cs MFA 生命周期（开启 / 关闭 / 验证码通道）
///   - AccountService.Profile.cs   档案、密码（改密 / 重置）与邮箱确认
/// </summary>
public sealed partial class AccountService : IAccountService
{
    private const int RecoveryCodeCount = 10;

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IAuditLogService _audit;
    private readonly ITwoFactorTicketProtector _tickets;
    private readonly IEmailSender _emailSender;
    private readonly IReadOnlyDictionary<string, ITwoFactorChannel> _twoFactorChannels;
    private readonly ICurrentUser _currentUser;
    private readonly IRolePermissionMap _rolePermissions;
    private readonly IClock _clock;
    private readonly ILogger<AccountService> _logger;

    public AccountService(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IAuditLogService audit,
        ITwoFactorTicketProtector tickets,
        IEmailSender emailSender,
        IEnumerable<ITwoFactorChannel> twoFactorChannels,
        ICurrentUser currentUser,
        IRolePermissionMap rolePermissions,
        IClock clock,
        ILogger<AccountService> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _audit = audit;
        _tickets = tickets;
        _emailSender = emailSender;
        // 按通道名索引。重名直接炸在启动期 —— 否则运行期会静默用后注册的那个覆盖先注册的，
        // 表现为"某个通道的验证码发到了另一个通道的文案里"，很难查。
        _twoFactorChannels = twoFactorChannels.ToDictionary(
            channel => channel.Provider,
            StringComparer.Ordinal);
        _currentUser = currentUser;
        _rolePermissions = rolePermissions;
        _clock = clock;
        _logger = logger;
    }

    // ------------------------------------------------------------------ 内部辅助（被各 partial 文件共用）

    private async Task<ApplicationUser?> FindByIdentifierAsync(string identifier)
        => identifier.Contains('@', StringComparison.Ordinal)
            ? await _userManager.FindByEmailAsync(identifier)
            : await _userManager.FindByNameAsync(identifier);

    private async Task<ApplicationUser?> GetCurrentUserAsync()
        => _currentUser.UserId is { } userId ? await _userManager.FindByIdAsync(userId) : null;

    private async Task SignInAndStampAsync(
        ApplicationUser user,
        bool isPersistent,
        string authenticationMethod,
        CancellationToken cancellationToken)
    {
        await _signInManager.SignInAsync(user, isPersistent, authenticationMethod);

        user.LastLoginAt = _clock.UtcNow;
        user.LastLoginIp = _currentUser.IpAddress;

        var update = await _userManager.UpdateAsync(user);
        if (!update.Succeeded)
        {
            // 只影响审计字段，不阻断登录
            _logger.LogWarning("更新用户最后登录信息失败：{Errors}", string.Join("; ", update.Errors.Select(e => e.Description)));
        }
    }

    private async Task<Result<UserProfileDto>> BuildProfileAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var roles = await _userManager.GetRolesAsync(user);
        var permissions = _rolePermissions.ResolvePermissions(roles);

        // 权限声明在令牌/会话里也需要，因此额外写回身份
        var profile = new UserProfileDto
        {
            Id = user.Id,
            UserName = user.UserName ?? string.Empty,
            Email = user.Email,
            DisplayName = user.DisplayName,
            PhoneNumber = user.PhoneNumber,
            EmailConfirmed = user.EmailConfirmed,
            TwoFactorEnabled = user.TwoFactorEnabled,
            IsActive = user.IsActive,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
            Roles = roles.ToArray(),
            Permissions = permissions
        };

        return Result.Success(profile);
    }

    private async Task<IReadOnlyCollection<string>> GenerateRecoveryCodesInternalAsync(ApplicationUser user)
    {
        var codes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);
        return codes?.ToArray() ?? Array.Empty<string>();
    }

    /// <returns>是否真正发出确认邮件（失败时已记日志，不抛给调用方）。</returns>
    private async Task<bool> TrySendEmailConfirmationAsync(ApplicationUser user, string? linkBase, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(user.Email)) return false;

        try
        {
            var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            var link = BuildTokenLink(linkBase, "/account/confirm-email", ("userId", user.Id), ("token", token));
            await _emailSender.SendAsync(
                user.Email,
                "【AuthHub】请确认您的邮箱",
                $"请点击下方链接完成邮箱确认（链接 1 小时内有效）：\n\n{link}\n\n" +
                "如果不是你本人操作，请忽略此邮件。",
                cancellationToken);
            await _audit.LogAsync(
                new AuditEntry(AuditActionType.UserEmailConfirmationRequested, true, user.Id, user.UserName),
                cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            // 邮件通道不可用不能导致注册失败
            _logger.LogWarning(ex, "发送邮箱确认邮件失败，用户 {UserId}", user.Id);
            return false;
        }
    }

    /// <summary>
    /// 把 token 链接拼成绝对地址。token 由 Identity 产出，可能含 + / = 等 URL 不安全字符，
    /// 必须用 <c>System.Uri.EscapeDataString</c> 编码。
    /// </summary>
    private static string BuildTokenLink(string? baseUrl, string path, params (string Key, string Value)[] query)
    {
        var schemeAndHost = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : baseUrl.TrimEnd('/');
        var queryString = string.Join("&", query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));
        return $"{schemeAndHost}{path}?{queryString}";
    }
}
