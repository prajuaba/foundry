#pragma warning disable IL2026, IL3050, IL2075, IL2090, IL2070, IL2060
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using FluentValidation;

namespace Foundry.Api.Middleware;

public class GlobalExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is ValidationException valEx)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            httpContext.Response.ContentType = "application/json";
            
            var problemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Validation Failed",
                Detail = "One or more validation errors occurred.",
                Instance = httpContext.Request.Path
            };
            problemDetails.Extensions["errors"] = valEx.Errors.Select(e => new { e.PropertyName, e.ErrorMessage }).ToList();

            var json = JsonSerializer.Serialize(problemDetails);
            await httpContext.Response.WriteAsync(json, cancellationToken);
            return true;
        }

        // The *other* ValidationException. This file has `using FluentValidation;`, so the branch
        // above binds to FluentValidation.ValidationException -- while the query-string and route
        // binders throw System.ComponentModel.DataAnnotations.ValidationException, a different type
        // with the same simple name, which fell through to the catch-all 500 at the bottom.
        //
        // A caller sending ?priority=nonsense was told the server had failed, when what happened is
        // that they sent something the server could not read. Two types one using-directive apart,
        // and the entire malformed-input path answered with the wrong class of status.
        if (exception is System.ComponentModel.DataAnnotations.ValidationException dataAnnotationsEx)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            httpContext.Response.ContentType = "application/json";

            var problemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Validation Failed",
                Detail = dataAnnotationsEx.Message,
                Instance = httpContext.Request.Path
            };

            var json = JsonSerializer.Serialize(problemDetails);
            await httpContext.Response.WriteAsync(json, cancellationToken);
            return true;
        }

        if (exception is IdempotencyException idempEx)
        {
            httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
            httpContext.Response.ContentType = "application/json";

            var problemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Idempotency Conflict",
                Detail = idempEx.Message,
                Instance = httpContext.Request.Path
            };
            problemDetails.Extensions["idempotencyKey"] = idempEx.IdempotencyKey;

            var json = JsonSerializer.Serialize(problemDetails);
            await httpContext.Response.WriteAsync(json, cancellationToken);
            return true;
        }

        // A write that lost an optimistic-concurrency race is a client-resolvable conflict, not a
        // server fault. Unmapped it surfaced as a 500, which tells a client nothing actionable —
        // 409 plus the entity id lets them re-read, merge and retry.
        if (exception is Foundry.Core.Entities.ConcurrencyException concurrencyEx)
        {
            httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
            httpContext.Response.ContentType = "application/json";

            var problemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Concurrency Conflict",
                Detail = concurrencyEx.Message,
                Instance = httpContext.Request.Path
            };
            problemDetails.Extensions["entityId"] = concurrencyEx.EntityId;
            problemDetails.Extensions["collection"] = concurrencyEx.CollectionName;

            var json = JsonSerializer.Serialize(problemDetails);
            await httpContext.Response.WriteAsync(json, cancellationToken);
            return true;
        }

        // A refused transition is a client-resolvable conflict, not a server fault: the entity is in
        // a state this transition does not apply to, or a guard rejected it. Unmapped it surfaced as
        // a bare 500 with the reason swallowed, so a caller could not tell "you cannot do that yet"
        // from "the server is broken" -- and the workflow engine's own explanation, which names the
        // current state and the one required, never reached them.
        if (exception is Foundry.Rules.WorkflowException workflowEx)
        {
            httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
            httpContext.Response.ContentType = "application/json";

            var problemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Workflow Transition Refused",
                Detail = workflowEx.Message,
                Instance = httpContext.Request.Path
            };

            var json = JsonSerializer.Serialize(problemDetails);
            await httpContext.Response.WriteAsync(json, cancellationToken);
            return true;
        }

        if (exception is UnauthorizedAccessException unauthEx)
        {
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            httpContext.Response.ContentType = "application/json";
            
            var problemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Forbidden",
                Detail = unauthEx.Message,
                Instance = httpContext.Request.Path
            };
            
            var json = JsonSerializer.Serialize(problemDetails);
            await httpContext.Response.WriteAsync(json, cancellationToken);
            return true;
        }

        return false;
    }
}
