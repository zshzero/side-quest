using FluentValidation;

namespace Driftworld.Api.Validation;

public sealed class HandleValidator : AbstractValidator<string>
{
    public HandleValidator()
    {
        RuleFor(h => h)
            .NotEmpty().WithMessage("must not be empty")
            .Length(3, 32).WithMessage("must be 3–32 characters")
            .Matches("^[a-zA-Z0-9_-]+$").WithMessage("must contain only letters, digits, '_', or '-'");
    }
}
