namespace AuthHub.Application.DTOs.Account;

/// <summary>注册请求。密码复杂度由 IdentityOptions 与验证器双重约束。</summary>
public record RegisterRequest(string UserName, string Email, string Password, string? DisplayName);

/// <summary>登录请求（用户名或邮箱皆可）。</summary>
public record LoginRequest(string UserNameOrEmail, string Password, bool RememberMe = false);

/// <summary>
/// 登录结果。
/// <see cref="RequiresTwoFactor"/> 为 true 时，客户端需携带 <see cref="TwoFactorToken"/>
/// 与用户输入的验证码调用两阶段验证接口。
/// </summary>
public sealed record LoginResponse
{
    public bool Succeeded { get; init; }

    public bool RequiresTwoFactor { get; init; }

    public bool LockedOut { get; init; }

    public string? UserId { get; init; }

    public string? UserName { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>不透明的一次性凭据（数据保护加密），用于完成第二阶段验证。</summary>
    public string? TwoFactorToken { get; init; }

    public static LoginResponse Success(string userId, string userName, string displayName)
        => new() { Succeeded = true, UserId = userId, UserName = userName, DisplayName = displayName };

    public static LoginResponse TwoFactorRequired(string userId, string userName, string twoFactorToken)
        => new() { RequiresTwoFactor = true, UserId = userId, UserName = userName, TwoFactorToken = twoFactorToken };

    public static LoginResponse Locked() => new() { LockedOut = true };
}

/// <summary>第二阶段（MFA）验证请求。</summary>
public record VerifyTwoFactorRequest(string TwoFactorToken, string Code, bool RememberMachine = false);

/// <summary>修改密码请求。</summary>
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>申请密码重置（忘记密码）：只接收邮箱，成功后向该邮箱发送重置链接。</summary>
public record ForgotPasswordRequest(string Email);

/// <summary>
/// 重置密码请求。Token 来自邮箱里的重置链接（Identity 签名令牌，带过期），
/// 校验通过才会真正改密。
/// </summary>
public record ResetPasswordRequest(string Email, string Token, string NewPassword);

/// <summary>重发邮箱确认邮件：用户注册后没收到确认信时使用。</summary>
public record ResendEmailConfirmationRequest(string Email);

/// <summary>启用 TOTP 时的返回：密钥、可扫描 URI、恢复码（仅本次返回）。</summary>
public sealed record TwoFactorSetupResponse(
    string SharedKey,
    string AuthenticatorUri,
    IReadOnlyCollection<string> RecoveryCodes);

/// <summary>确认启用 MFA（用 Authenticator App 生成的 6 位码）。</summary>
public record EnableTwoFactorRequest(string Code);

/// <summary>触发一次性验证码下发（Email / Phone 通道）。</summary>
public record SendTwoFactorCodeRequest(string Provider);

/// <summary>当前用户的档案信息（含角色与展开后的权限）。</summary>
public sealed record UserProfileDto
{
    public string Id { get; init; } = string.Empty;

    public string UserName { get; init; } = string.Empty;

    public string? Email { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string? PhoneNumber { get; init; }

    public bool EmailConfirmed { get; init; }

    public bool TwoFactorEnabled { get; init; }

    public bool IsActive { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastLoginAt { get; init; }

    public IReadOnlyCollection<string> Roles { get; init; } = Array.Empty<string>();

    public IReadOnlyCollection<string> Permissions { get; init; } = Array.Empty<string>();
}
