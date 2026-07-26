using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.User;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Foundry.Api.MediatR.Behaviors;

public class AuditBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ILogger<AuditBehavior<TRequest, TResponse>> _logger;
    private readonly ICurrentUserContext _currentUserContext;

    public AuditBehavior(
        ILogger<AuditBehavior<TRequest, TResponse>> logger,
        ICurrentUserContext currentUserContext)
    {
        _logger = logger;
        _currentUserContext = currentUserContext;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString();
        var stopwatch = Stopwatch.StartNew();
        var operatorId = _currentUserContext.OperatorId;
        var requestTypeName = typeof(TRequest).Name;

        // Start OpenTelemetry Activity Trace
        using var activity = Diagnostics.Diagnostics.ActivitySource.StartActivity($"Execute {requestTypeName}");
        if (activity != null)
        {
            activity.SetTag("foundry.correlation_id", correlationId);
            activity.SetTag("foundry.operator_id", operatorId);
            activity.SetTag("foundry.request_type", requestTypeName);
        }

        Diagnostics.Diagnostics.RequestCounter.Add(1, new KeyValuePair<string, object?>("request_type", requestTypeName));

        try
        {
            _logger.LogInformation(
                "Starting execution of {RequestType} with CorrelationId: {CorrelationId}, OperatorId: {OperatorId}",
                requestTypeName,
                correlationId,
                operatorId);

            var response = await next();

            stopwatch.Stop();
            var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;

            _logger.LogInformation(
                "Successfully executed {RequestType} with CorrelationId: {CorrelationId}, OperatorId: {OperatorId}, Duration: {Duration}ms",
                requestTypeName,
                correlationId,
                operatorId,
                stopwatch.ElapsedMilliseconds);

            Diagnostics.Diagnostics.RequestDuration.Record(elapsedSeconds, new KeyValuePair<string, object?>("request_type", requestTypeName));
            
            if (activity != null)
            {
                activity.SetStatus(ActivityStatusCode.Ok);
            }

            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;

            _logger.LogError(
                ex,
                "Error executing {RequestType} with CorrelationId: {CorrelationId}, OperatorId: {OperatorId}, Duration: {Duration}ms",
                requestTypeName,
                correlationId,
                operatorId,
                stopwatch.ElapsedMilliseconds);

            Diagnostics.Diagnostics.RequestDuration.Record(elapsedSeconds, new KeyValuePair<string, object?>("request_type", requestTypeName));

            if (activity != null)
            {
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
                {
                    { "exception.type", ex.GetType().FullName },
                    { "exception.message", ex.Message },
                    { "exception.stacktrace", ex.StackTrace }
                }));
            }

            throw;
        }
    }
}
