using AuthHub.Application.DTOs.Account;
using FluentValidation;

namespace AuthHub.Application.Validators;

/// <summary>
/// 注册请求验证。密码规则刻意与 <c>IdentityOptions.Password</c> 保持一致，
/// 让“接口层快速失败”和“Identity 兜底校验”给出同一套标准。
/// </summary>
public sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.UserName)
            .NotEmpty().WithMessage("用户名不能为空。")
            .MinimumLength(3).WithMessage("用户名长度至少 3 位。")
            .MaximumLength(64).WithMessage("用户名长度不能超过 64 位。")
            .Matches("^[a-zA-Z0-9._@-]+$").WithMessage("用户名只能包含字母、数字以及 . _ - @ 。");

        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("邮箱不能为空。")
            .EmailAddress().WithMessage("邮箱格式不正确。")
            .MaximumLength(256);

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("密码不能为空。")
            .MinimumLength(8).WithMessage("密码长度至少 8 位。")
            .MaximumLength(128).WithMessage("密码长度不能超过 128 位。")
            .Matches("[A-Z]").WithMessage("密码必须包含大写字母。")
            .Matches("[a-z]").WithMessage("密码必须包含小写字母。")
            .Matches("[0-9]").WithMessage("密码必须包含数字。")
            .Matches("[^a-zA-Z0-9]").WithMessage("密码必须包含特殊字符。");

        RuleFor(x => x.DisplayName)
            .MaximumLength(128).WithMessage("显示名长度不能超过 128 位。")
            .When(x => !string.IsNullOrWhiteSpace(x.DisplayName));
    }
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.UserNameOrEmail)
            .NotEmpty().WithMessage("用户名或邮箱不能为空。")
            .MaximumLength(256);

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("密码不能为空。")
            .MaximumLength(128);
    }
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().WithMessage("当前密码不能为空。");

        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage("新密码不能为空。")
            .MinimumLength(8).WithMessage("新密码长度至少 8 位。")
            .MaximumLength(128)
            .Matches("[A-Z]").WithMessage("新密码必须包含大写字母。")
            .Matches("[a-z]").WithMessage("新密码必须包含小写字母。")
            .Matches("[0-9]").WithMessage("新密码必须包含数字。")
            .Matches("[^a-zA-Z0-9]").WithMessage("新密码必须包含特殊字符。")
            .NotEqual(x => x.CurrentPassword).WithMessage("新密码不能与当前密码相同。");
    }
}

public sealed class VerifyTwoFactorRequestValidator : AbstractValidator<VerifyTwoFactorRequest>
{
    public VerifyTwoFactorRequestValidator()
    {
        RuleFor(x => x.TwoFactorToken).NotEmpty().WithMessage("两阶段验证凭据缺失，请重新登录。");

        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("验证码不能为空。")
            .MinimumLength(6).WithMessage("验证码至少 6 位。")
            .MaximumLength(32).WithMessage("验证码长度不合法。")
            .Matches("^[a-zA-Z0-9-]+$").WithMessage("验证码包含非法字符。");
    }
}

public sealed class EnableTwoFactorRequestValidator : AbstractValidator<EnableTwoFactorRequest>
{
    public EnableTwoFactorRequestValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("验证码不能为空。")
            .Length(6).WithMessage("Authenticator 验证码为 6 位数字。")
            .Matches("^[0-9]{6}$").WithMessage("Authenticator 验证码只能是数字。");
    }
}

public sealed class SendTwoFactorCodeRequestValidator : AbstractValidator<SendTwoFactorCodeRequest>
{
    public SendTwoFactorCodeRequestValidator()
    {
        RuleFor(x => x.Provider)
            .NotEmpty().WithMessage("验证通道不能为空。")
            .Must(p => p is "Email" or "Phone")
            .WithMessage("验证通道仅支持 Email 或 Phone。");
    }
}
