using AuthHub.Application.DTOs.Roles;
using FluentValidation;

namespace AuthHub.Application.Validators;

public sealed class CreateRoleRequestValidator : AbstractValidator<CreateRoleRequest>
{
    public CreateRoleRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("角色名不能为空。")
            .MinimumLength(2).WithMessage("角色名至少 2 位。")
            .MaximumLength(64).WithMessage("角色名不能超过 64 位。")
            .Matches("^[a-zA-Z0-9._-]+$").WithMessage("角色名只能包含字母、数字以及 . _ - 。");

        RuleFor(x => x.Description)
            .MaximumLength(256).WithMessage("角色描述不能超过 256 位。")
            .When(x => !string.IsNullOrWhiteSpace(x.Description));
    }
}

public sealed class UpdateRoleRequestValidator : AbstractValidator<UpdateRoleRequest>
{
    public UpdateRoleRequestValidator()
    {
        RuleFor(x => x.Description)
            .MaximumLength(256).WithMessage("角色描述不能超过 256 位。")
            .When(x => x.Description is not null);
    }
}
