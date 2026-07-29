// <foundry-scaffold/>
//
// Foundry generated this file once as a starting point and will never overwrite it.
// This is your file: put your business logic here and commit it.
//
#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Foundry.Rules;
using Foundry.Api.MediatR;

namespace Foundry.E2E.Showcase.Rules;

/// <summary>
/// Entity CRUD business rule validator for Order on POST.
/// </summary>
/// <remarks>
/// Named by the schema -- <c>"apiBusinessRules": { "POST": ["OrderCreditLimitRule"] }</c> on Order --
/// so it runs inside the MediatR pipeline for the generated POST endpoint, not only for the custom
/// submit endpoint. The compiler emitted the class and typed it against the right command; the
/// policy below is the part a schema cannot state.
/// </remarks>
public class OrderCreditLimitRule : IBusinessRule<InsertCommand<Foundry.E2E.Showcase.Order>>
{
    private readonly Foundry.Mongo.Repositories.IRepository<Foundry.E2E.Showcase.Customer> _customers;

    public OrderCreditLimitRule(Foundry.Mongo.Repositories.IRepository<Foundry.E2E.Showcase.Customer> customers)
    {
        _customers = customers;
    }

    public async Task<RuleResult> ValidateAsync(InsertCommand<Foundry.E2E.Showcase.Order> request, CancellationToken ct)
    {
        var order = request.Entity;

        var customer = await _customers.GetByIdAsync(order.CustomerId);
        if (customer is null)
        {
            return RuleResult.Failure(
                $"No customer {order.CustomerId} exists for this order.", "UnknownCustomer");
        }

        if (order.TotalAmount > customer.CreditLimit)
        {
            return RuleResult.Failure(
                $"Order total {order.TotalAmount:C} exceeds the customer's credit limit of "
                + $"{customer.CreditLimit:C}.", "CreditLimitExceeded");
        }

        return RuleResult.Success();
    }
}
