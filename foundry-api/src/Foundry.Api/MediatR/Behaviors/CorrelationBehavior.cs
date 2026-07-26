using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Foundry.Core.Telemetry;

namespace Foundry.Api.MediatR.Behaviors;

/// <summary>
/// MediatR pipeline behavior that injects ambient correlation logging scope into all command/query handlers.
/// </summary>
public class CorrelationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ICorrelationContext _correlationContext;
    private readonly ILogger<CorrelationBehavior<TRequest, TResponse>> _logger;

    public CorrelationBehavior(
        ICorrelationContext correlationContext,
        ILogger<CorrelationBehavior<TRequest, TResponse>> logger)
    {
        _correlationContext = correlationContext;
        _logger = logger;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var correlationId = _correlationContext.CorrelationId;
        using (_logger.BeginScope("CorrelationId: {CorrelationId}", correlationId))
        {
            return await next();
        }
    }
}
