#pragma warning disable IL2026, IL3050, IL2075, IL2090, IL2070, IL2060
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using FluentValidation;
using MediatR;

namespace Foundry.Api.MediatR.Behaviors;

public class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IServiceProvider _serviceProvider;

    public ValidationBehavior(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var requestType = typeof(TRequest);
        var entityProp = requestType.GetProperty("Entity");
        
        if (entityProp != null)
        {
            var entityVal = entityProp.GetValue(request);
            if (entityVal != null)
            {
                var entityType = entityVal.GetType();
                var errors = new List<FluentValidation.Results.ValidationFailure>();

                // 1. Run DataAnnotations validation
                var validationContext = new System.ComponentModel.DataAnnotations.ValidationContext(entityVal, serviceProvider: _serviceProvider, items: null);
                var validationResults = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
                
                if (!System.ComponentModel.DataAnnotations.Validator.TryValidateObject(entityVal, validationContext, validationResults, validateAllProperties: true))
                {
                    foreach (var validationResult in validationResults)
                    {
                        var memberName = validationResult.MemberNames.FirstOrDefault() ?? string.Empty;
                        errors.Add(new FluentValidation.Results.ValidationFailure(memberName, validationResult.ErrorMessage ?? "Invalid field value"));
                    }
                }

                // 2. Run FluentValidation validation if registered
                var validatorType = typeof(IValidator<>).MakeGenericType(entityType);
                var validator = _serviceProvider.GetService(validatorType) as IValidator;
                
                if (validator != null)
                {
                    var context = new ValidationContext<object>(entityVal);
                    var result = await validator.ValidateAsync(context, cancellationToken);
                    if (!result.IsValid)
                    {
                        errors.AddRange(result.Errors);
                    }
                }

                // 3. Handle errors
                if (errors.Any())
                {
                    // Increment validation failure counter
                    Diagnostics.Diagnostics.ValidationFailures.Add(1, new KeyValuePair<string, object?>("entity_type", entityType.Name));
                    
                    // Tag current OpenTelemetry activity
                    var activity = Activity.Current;
                    if (activity != null)
                    {
                        activity.SetTag("foundry.validation.status", "Failed");
                        activity.SetTag("foundry.validation.errors_count", errors.Count);
                    }

                    throw new ValidationException(errors);
                }
            }
        }

        return await next();
    }
}
