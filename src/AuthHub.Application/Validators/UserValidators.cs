using AuthHub.Application.DTOs.Users;
using FluentValidation;

namespace AuthHub.Application.Validators;

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.UserName)
            .NotEmpty().MinimumLength(3).MaximumLength(64)
            .Matches("^[a-zA-Z0-9._@-]+$")
            .WithMessage("用户名只能包含字母、数字以及 . _ - @ 。");

        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);

        RuleFor(x => x.Password)
            .NotEmpty().MinimumLength(8).MaximumLength(128)
            .Matches("[A-Z]").WithMessage("密码必须包含大写字母。")
            .Matches("[a-z]").WithMessage("密码必须包含小写字母。")
            .Matches("[0-9]").WithMessage("密码必须包含数字。")
            .Matches("[^a-zA-Z0-9]").WithMessage("密码必须包含特殊字符。");

        RuleFor(x => x.DisplayName)
            .MaximumLength(128)
            .When(x => !string.IsNullOrWhiteSpace(x.DisplayName));

        RuleForEach(x => x.Roles)
            .NotEmpty().WithMessage("角色名不能为空。")
            .MaximumLength(64)
            .When(x => x.Roles is not null);
    }
}

public sealed class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(x => x.Email)
            .EmailAddress().WithMessage("邮箱格式不正确。")
            .MaximumLength(256)
            .When(x => !string.IsNullOrWhiteSpace(x.Email));

        RuleFor(x => x.DisplayName)
            .MaximumLength(128)
            .When(x => x.DisplayName is not null);

        RuleFor(x => x.PhoneNumber)
            .MaximumLength(32)
            .When(x => !string.IsNullOrWhiteSpace(x.PhoneNumber));
    }
}

public sealed class AssignRolesRequestValidator : AbstractValidator<AssignRolesRequest>
{
    public AssignRolesRequestValidator()
    {
        RuleFor(x => x.Roles).NotNull().WithMessage("角色列表不能为空（如需清空请传空数组）。");

        RuleForEach(x => x.Roles)
            .NotEmpty().WithMessage("角色名不能为空。")
            .MaximumLength(64);
    }
}
