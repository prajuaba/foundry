// <foundry-scaffold/>
//
// Foundry generated this file once as a starting point and will never overwrite it.
// This is your file: put your business logic here and commit it.
//
#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Foundry.Rules;

namespace Foundry.E2E.Showcase.Rules;

/// <summary>
/// Custom business rule validator for SubmitOrderCommand.
/// </summary>
public class SubmitOrderRule : IBusinessRule<Foundry.E2E.Showcase.SubmitOrderCommand>
{
    public Task<RuleResult> ValidateAsync(Foundry.E2E.Showcase.SubmitOrderCommand request, CancellationToken ct)
    {
        if (request.TotalAmount <= 0)
        {
            return Task.FromResult(RuleResult.Failure(
                "Order total must be greater than zero.", "OrderTotalNotPositive"));
        }

        if (request.TotalAmount > 100_000m)
        {
            return Task.FromResult(RuleResult.Failure(
                "Single order total exceeds the $100,000 transaction limit.", "ExceedsTransactionLimit"));
        }

        if (string.IsNullOrWhiteSpace(request.OrderNumber))
        {
            return Task.FromResult(RuleResult.Failure(
                "An order number is required.", "OrderNumberMissing"));
        }

        return Task.FromResult(RuleResult.Success());
    }
}
