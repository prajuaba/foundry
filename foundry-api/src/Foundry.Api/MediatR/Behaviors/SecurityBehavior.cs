using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Foundry.Core.User;
using Foundry.Api.Manifest;

namespace Foundry.Api.MediatR.Behaviors;

public class SecurityBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ICurrentUserContext _currentUserContext;
    private readonly ILogger<SecurityBehavior<TRequest, TResponse>> _logger;

    public SecurityBehavior(
        IHttpContextAccessor httpContextAccessor,
        ICurrentUserContext currentUserContext,
        ILogger<SecurityBehavior<TRequest, TResponse>> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _currentUserContext = currentUserContext;
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        
        // If HttpContext is null, we are likely running in a test or background job where HTTP security context isn't set up.
        if (httpContext == null)
        {
            _logger.LogWarning(
                "SecurityBehavior: No HttpContext available for {RequestType}. " +
                "Security checks skipped. This is expected for background jobs " +
                "but may indicate misconfiguration for HTTP requests.",
                typeof(TRequest).Name);
            return await next();
        }

        var endpoint = httpContext.GetEndpoint();
        if (endpoint == null)
        {
            return await next();
        }

        var endpointConfig = endpoint.Metadata.GetMetadata<EndpointConfig>();
        if (endpointConfig == null)
        {
            return await next();
        }

        var operationName = GetOperationName(httpContext.Request.Method, request);
        
        if (endpointConfig.Roles.TryGetValue(operationName, out var allowedRoles))
        {
            if (allowedRoles != null && allowedRoles.Count > 0)
            {
                var claimsPrincipal = _currentUserContext.User;
                
                if (claimsPrincipal == null || !claimsPrincipal.Identity?.IsAuthenticated == true)
                {
                    throw new UnauthorizedAccessException("User is not authenticated");
                }

                // Check if user is in any allowed role. We check ClaimsPrincipal.IsInRole or checking for role claims.
                var isAuthorized = allowedRoles.Any(role => 
                    claimsPrincipal.IsInRole(role) ||
                    claimsPrincipal.HasClaim(c => c.Type == ClaimTypes.Role && c.Value.Equals(role, StringComparison.OrdinalIgnoreCase)) ||
                    claimsPrincipal.HasClaim(c => c.Type == "role" && c.Value.Equals(role, StringComparison.OrdinalIgnoreCase))
                );
                
                if (!isAuthorized)
                {
                    _logger.LogWarning("Access Denied: User {OperatorId} does not possess any required roles [{Roles}] for {Operation} on {Route}", 
                        _currentUserContext.OperatorId, string.Join(", ", allowedRoles), operationName, endpointConfig.Route);
                    throw new UnauthorizedAccessException($"User does not have permission to execute {operationName} on {endpointConfig.Route}. Required roles: {string.Join(", ", allowedRoles)}");
                }
            }
        }

        return await next();
    }

    private string GetOperationName(string httpMethod, TRequest request)
    {
        var method = httpMethod.ToUpperInvariant();
        if (method == "GET")
        {
            // Detect GetByIdQuery
            var requestType = request.GetType();
            if (requestType.Name.StartsWith("GetByIdQuery", StringComparison.OrdinalIgnoreCase))
            {
                return "GET_BY_ID";
            }
            return "GET";
        }
        return method;
    }
}
