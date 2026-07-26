using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Foundry.Api.Security;

/// <summary>
/// Middleware enforcing production security headers (HSTS, CSP, X-Frame-Options, X-Content-Type-Options).
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        headers["X-Frame-Options"] = "DENY";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-XSS-Protection"] = "1; mode=block";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:;";
        headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";

        await _next(context);
    }
}

public static class SecurityHeadersMiddlewareExtensions
{
    /// <summary>
    /// Enforces commercial-grade security response headers across all HTTP responses.
    /// </summary>
    public static IApplicationBuilder UseFoundrySecurityHeaders(this IApplicationBuilder app)
    {
        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }
}
