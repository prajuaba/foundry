using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Foundry.Core.Tenant;

namespace Foundry.Api.Middleware;

/// <summary>
/// ASP.NET Core middleware that resolves ambient tenant context from X-Tenant-ID header, query parameter, or claims.
/// </summary>
public class TenantContextMiddleware
{
    public const string TenantIdHeaderName = "X-Tenant-ID";
    private readonly RequestDelegate _next;

    public TenantContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        string? tenantId = context.Request.Headers[TenantIdHeaderName].ToString();

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            tenantId = context.Request.Query["tenantId"].ToString();
        }

        if (string.IsNullOrWhiteSpace(tenantId) && context.User.Identity?.IsAuthenticated == true)
        {
            tenantId = context.User.FindFirst("tenant_id")?.Value
                    ?? context.User.FindFirst("tenantId")?.Value;
        }

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            tenantContext.SetTenantId(tenantId);
        }

        await _next(context);
    }
}
