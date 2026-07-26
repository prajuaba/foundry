#pragma warning disable IL2026, IL3050, IL2075, IL2090, IL2070, IL2060
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Foundry.Core.Outbox;

namespace Foundry.Api.MediatR.Behaviors;

/// <summary>
/// Intercepts MediatR mutation commands and automatically enqueues corresponding
/// <see cref="EntityMutationEvent{T}"/> records into the transactional outbox queue.
/// </summary>
/// <typeparam name="TRequest">The type of the incoming command request.</typeparam>
/// <typeparam name="TResponse">The type of the handler response.</typeparam>
public class OutboxDomainEventBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IOutboxQueue? _outboxQueue;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxDomainEventBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="outboxQueue">The transactional outbox queue (optional to allow fallback if not configured).</param>
    public OutboxDomainEventBehavior(IOutboxQueue? outboxQueue = null)
    {
        _outboxQueue = outboxQueue;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var response = await next();

        if (_outboxQueue == null)
        {
            return response;
        }

        var requestType = typeof(TRequest);
        if (IsMutation(requestType))
        {
            var mutationType = GetMutationType(requestType);
            var entityProp = requestType.GetProperty("Entity");
            if (entityProp != null)
            {
                var entityVal = entityProp.GetValue(request);
                if (entityVal != null)
                {
                    // Construct dynamic generic EntityMutationEvent<TEntity>
                    var entityType = entityVal.GetType();
                    var eventGenericType = typeof(EntityMutationEvent<>).MakeGenericType(entityType);
                    var mutationEvent = Activator.CreateInstance(eventGenericType);

                    if (mutationEvent != null)
                    {
                        var mutTypeProp = eventGenericType.GetProperty("MutationType");
                        var entityValProp = eventGenericType.GetProperty("Entity");
                        var timestampProp = eventGenericType.GetProperty("Timestamp");

                        mutTypeProp?.SetValue(mutationEvent, mutationType);
                        entityValProp?.SetValue(mutationEvent, entityVal);
                        timestampProp?.SetValue(mutationEvent, DateTime.UtcNow);

                        // Enqueue transactionally into the outbox
                        await _outboxQueue.EnqueueAsync((dynamic)mutationEvent, cancellationToken);
                    }
                }
            }
        }

        return response;
    }

    private static bool IsMutation(Type requestType)
    {
        var name = requestType.Name;
        return name.StartsWith("InsertCommand", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("UpdateCommand", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("DeleteCommand", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetMutationType(Type requestType)
    {
        var name = requestType.Name;
        if (name.StartsWith("InsertCommand", StringComparison.OrdinalIgnoreCase)) return "Insert";
        if (name.StartsWith("UpdateCommand", StringComparison.OrdinalIgnoreCase)) return "Update";
        return "Delete";
    }
}
