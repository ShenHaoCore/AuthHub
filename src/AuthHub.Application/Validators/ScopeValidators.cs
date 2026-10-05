using AuthHub.Application.DTOs.Scopes;
using FluentValidation;

namespace AuthHub.Application.Validators;

public sealed class CreateScopeRequestValidator : AbstractValidator<CreateScopeRequest>
{
    public CreateScopeRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Scope 名称不能为空。")
            .MinimumLength(2).WithMessage("Scope 名称至少 2 位。")
            .MaximumLength(100).WithMessage("Scope 名称不能超过 100 位。")
            .Matches("^[a-zA-Z0-9:._-]+$")
            .WithMessage("Scope 名称只能包含字母、数字以及 : . _ - 。");

        RuleFor(x => x.DisplayName).MaximumLength(256)
            .When(x => !string.IsNullOrWhiteSpace(x.DisplayName));

        RuleFor(x => x.Description).MaximumLength(512)
            .When(x => !string.IsNullOrWhiteSpace(x.Description));

        RuleForEach(x => x.Resources)
            .NotEmpty().WithMessage("资源标识不能为空。")
            .MaximumLength(256)
            .When(x => x.Resources is not null);
    }
}

public sealed class UpdateScopeRequestValidator : AbstractValidator<UpdateScopeRequest>
{
    public UpdateScopeRequestValidator()
    {
        RuleFor(x => x.DisplayName).MaximumLength(256)
            .When(x => x.DisplayName is not null);

        RuleFor(x => x.Description).MaximumLength(512)
            .When(x => x.Description is not null);

        RuleForEach(x => x.Resources)
            .NotEmpty().WithMessage("资源标识不能为空。")
            .MaximumLength(256)
            .When(x => x.Resources is not null);
    }
}
