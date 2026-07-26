using FluentValidation;
using Paperclip.OrderingSystem.Domain;

namespace Paperclip.OrderingSystem.Validation;

public class OrderValidator : AbstractValidator<Order>
{
    public OrderValidator()
    {
        RuleFor(x => x.OrderNumber)
            .NotEmpty().WithMessage("OrderNumber is required.")
            .MinimumLength(3).WithMessage("OrderNumber must be at least 3 characters.");

        RuleFor(x => x.TotalAmount)
            .GreaterThanOrEqualTo(0).WithMessage("TotalAmount must be non-negative.");
    }
}
