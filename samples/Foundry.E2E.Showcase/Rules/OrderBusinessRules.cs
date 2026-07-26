using System;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Rules;
using Foundry.E2E.Showcase.Commands;

namespace Foundry.E2E.Showcase.Rules
{
    public class OrderAmountValidationRule : IBusinessRule<SubmitOrderCommand>
    {
        public Task<RuleResult> ValidateAsync(SubmitOrderCommand request, CancellationToken ct = default)
        {
            if (request.TotalAmount <= 0)
            {
                return Task.FromResult(RuleResult.Failure("Order total amount must be greater than $0.00.", "OrderTotalZero"));
            }

            if (request.TotalAmount > 100000m)
            {
                return Task.FromResult(RuleResult.Failure("Single order total exceeds maximum allowed transaction limit of $100,000.", "ExceedsLimit"));
            }

            return Task.FromResult(RuleResult.Success());
        }
    }

    public class OrderBusinessRulesService
    {
        private readonly IBusinessRuleEngine _ruleEngine;

        public OrderBusinessRulesService(IBusinessRuleEngine ruleEngine)
        {
            _ruleEngine = ruleEngine;
        }

        public async Task ValidateOrderCommandAsync(SubmitOrderCommand command, CancellationToken ct = default)
        {
            await _ruleEngine.EnsurePassedAsync(command, ct);
        }
    }
}
