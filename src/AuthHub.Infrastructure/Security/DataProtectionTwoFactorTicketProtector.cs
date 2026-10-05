using System.Globalization;
using System.Security.Cryptography;
using AuthHub.Application.Interfaces;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace AuthHub.Infrastructure.Security;

/// <summary>
/// 用 ASP.NET Core Data Protection 保护 MFA 两阶段登录的中间票据。
/// 票据里只放用户 Id、是否记住我、过期时间——不含密码、不含任何可用于登录的秘密。
/// </summary>
public sealed class DataProtectionTwoFactorTicketProtector : ITwoFactorTicketProtector
{
    private const string ProtectorPurpose = "AuthHub.TwoFactorTicket.v1";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly IDataProtector _protector;
    private readonly ILogger<DataProtectionTwoFactorTicketProtector> _logger;

    public DataProtectionTwoFactorTicketProtector(
        IDataProtectionProvider provider,
        ILogger<DataProtectionTwoFactorTicketProtector> logger)
    {
        _protector = provider.CreateProtector(ProtectorPurpose);
        _logger = logger;
    }

    public string Protect(string userId, bool rememberMe)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(Lifetime).ToUnixTimeSeconds();
        var payload = string.Join('|', userId, rememberMe ? "1" : "0", expiresAt.ToString(CultureInfo.InvariantCulture));
        return _protector.Protect(payload);
    }

    public TwoFactorTicket? Unprotect(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var payload = _protector.Unprotect(token);
            var parts = payload.Split('|');
            if (parts.Length != 3)
            {
                return null;
            }

            if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiresAtUnix))
            {
                return null;
            }

            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix);
            if (expiresAt <= DateTimeOffset.UtcNow)
            {
                _logger.LogDebug("两阶段登录票据已过期。");
                return null;
            }

            return new TwoFactorTicket(parts[0], parts[1] == "1", expiresAt);
        }
        catch (CryptographicException)
        {
            // 被篡改或密钥轮换后的旧票据
            _logger.LogWarning("两阶段登录票据校验失败（可能被篡改）。");
            return null;
        }
    }
}
