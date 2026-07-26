using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Foundry.Rules;

namespace Foundry.Api.MediatR.Behaviors;

/// <summary>
/// Pipeline behavior running Stage 2 (contextual/asynchronous business rules) after structural validations pass.
/// </summary>
public sealed class BusinessRuleBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IBusinessRuleEngine _ruleEngine;

    public BusinessRuleBehavior(IBusinessRuleEngine ruleEngine)
    {
        _ruleEngine = ruleEngine;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var failures = (await _ruleEngine.EvaluateAsync(request, cancellationToken)).ToList();
        if (failures.Any())
        {
            var validationFailures = failures.Select(f =>
                new ValidationFailure(string.Empty, f.ErrorMessage ?? "Business rule validation failed.")
                {
                    ErrorCode = f.RuleCode
                });

            throw new ValidationException(validationFailures);
        }

        return await next();
    }
}
