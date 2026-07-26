using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Foundry.Core.Telemetry;

namespace Foundry.Api.Middleware;

/// <summary>
/// ASP.NET Core middleware that extracts or generates an ambient X-Correlation-ID header on HTTP requests.
/// </summary>
public class CorrelationIdMiddleware
{
    public const string CorrelationIdHeaderName = "X-Correlation-ID";
    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ICorrelationContext correlationContext)
    {
        string correlationId = context.Request.Headers[CorrelationIdHeaderName].ToString();

        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = Guid.NewGuid().ToString("N");
        }

        correlationContext.SetCorrelationId(correlationId);

        context.Response.OnStarting(() =>
        {
            if (!context.Response.Headers.ContainsKey(CorrelationIdHeaderName))
            {
                context.Response.Headers[CorrelationIdHeaderName] = correlationId;
            }
            return Task.CompletedTask;
        });

        using (_logger.BeginScope("CorrelationId: {CorrelationId}", correlationId))
        {
            await _next(context);
        }
    }
}
