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

        // A request body ASP.NET could not read at all.
        //
        // Model binding runs before any Foundry code does, so a POST omitting a required property,
        // or sending a string where a number belongs, threw BadHttpRequestException out of
        // System.Text.Json and fell through to the catch-all 500. The two halves of the same
        // mistake answered differently: `?priority=nonsense` in the query string was correctly
        // rejected with 400 by the branch above, while the identical error in the body was reported
        // as a server fault. A caller could not tell "you sent something I cannot read" from "I am
        // broken", and a 500 invites a retry that cannot ever succeed.
        //
        // The exception's own StatusCode is used rather than a hardcoded 400: the same type carries
        // 413 for a body over the size limit, and answering 400 there would be a second wrong code.
        if (exception is BadHttpRequestException badRequestEx)
        {
            var status = badRequestEx.StatusCode is >= 400 and < 500
                ? badRequestEx.StatusCode
                : StatusCodes.Status400BadRequest;

            httpContext.Response.StatusCode = status;
            httpContext.Response.ContentType = "application/json";

            var problemDetails = new ProblemDetails
            {
                Status = status,
                // Deliberately not badRequestEx.Message, and not the inner JsonException's message.
                // Those are written by the BCL, not by this framework, and the deserialization one
                // names the CLR type it was binding -- "JSON deserialization for type
                // 'Contoso.Orders.Api.Domain.Invoice' was missing required properties" -- which
                // hands a caller the application's internal namespace to no purpose. The parts a
                // client can act on are extracted below instead.
                Title = "Malformed Request Body",
                Detail = "The request body could not be read. It is either not valid JSON, or does "
                    + "not match the shape this endpoint expects.",
                Instance = httpContext.Request.Path
            };

            if (exception.InnerException is JsonException jsonEx)
            {
                // The JSON path to the offending member, e.g. "$.lineItems[2].quantity". This is
                // System.Text.Json's own structured locator, so it needs no parsing and leaks
                // nothing beyond the document the caller themselves sent.
                if (!string.IsNullOrEmpty(jsonEx.Path))
                {
                    problemDetails.Extensions["path"] = jsonEx.Path;
                }

                var missing = ExtractMissingProperties(jsonEx.Message);
                if (missing.Count > 0)
                {
                    problemDetails.Extensions["missingProperties"] = missing;
                }
            }

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

    /// <summary>
    /// Pulls the property names out of System.Text.Json's missing-required-properties message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The message reads: <c>JSON deserialization for type 'X' was missing required properties
    /// including: 'a', 'b'.</c> Only the quoted names after the marker are taken, so the type name
    /// ahead of it never reaches the response.
    /// </para>
    /// <para>
    /// Reading a BCL message is not something to do casually, and it is guarded accordingly: no
    /// marker, no extension. The alternative is telling a caller only that their body was wrong
    /// without saying which field, when the runtime already worked out the answer. If a future
    /// runtime rewords the message the caller loses the hint and still gets the correct status,
    /// which is the failure this is allowed to have.
    /// </para>
    /// </remarks>
    private static List<string> ExtractMissingProperties(string message)
    {
        const string Marker = "missing required properties including:";

        var names = new List<string>();
        var start = message.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0) return names;

        var tail = message.AsSpan(start + Marker.Length);
        var inQuote = false;
        var current = new System.Text.StringBuilder();

        foreach (var c in tail)
        {
            if (c == '\'')
            {
                if (inQuote)
                {
                    if (current.Length > 0) names.Add(current.ToString());
                    current.Clear();
                }

                inQuote = !inQuote;
                continue;
            }

            if (inQuote) current.Append(c);
        }

        return names;
    }
}
