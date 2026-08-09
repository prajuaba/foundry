using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.User;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Foundry.Api.MediatR.Behaviors;

/// <summary>
/// MediatR pipeline behavior that provides request-level telemetry using OpenTelemetry and logging.
/// This behavior wraps the execution of a request (including all downstream behaviors and the handler)
/// in an OpenTelemetry Activity span, records timing metrics, and emits structured log entries with
/// correlation and operator context. It increments a request counter on each invocation and records
/// duration distributions for both successful and failed executions.
/// </summary>
/// <remarks>
/// Despite its former name (AuditBehavior), this behavior does NOT write any audit trail data to storage.
/// Audit entries are exclusively handled by the repository layer's IAuditSink implementation, which is invoked
/// via domain events or direct persistence within use cases. This telemetry-only behavior exists solely for
/// observability, debugging, and operational monitoring—not compliance auditing. The name was changed to avoid
/// confusion: if searching for "AuditBehavior", note that this class was previously named AuditBehavior and has
/// been renamed to RequestTelemetryBehavior to clarify that it handles telemetry only, not audit persistence.
/// </remarks>
/// <typeparam name="TRequest">The type of request being handled.</typeparam>
/// <typeparam name="TResponse">The type of response returned by the handler.</typeparam>
public class RequestTelemetryBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ILogger<RequestTelemetryBehavior<TRequest, TResponse>> _logger;
    private readonly ICurrentUserContext _currentUserContext;

    public RequestTelemetryBehavior(
        ILogger<RequestTelemetryBehavior<TRequest, TResponse>> logger,
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
