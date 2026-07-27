using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Foundry.Core.Tenant;

namespace Foundry.Api.Middleware;

/// <summary>
/// Resolves the ambient tenant for a request, from the caller's token or from a trusted header.
/// </summary>
/// <remarks>
/// <para>
/// Order matters and it used to be backwards. The header was read first and the token claim only
/// consulted if nothing else had supplied a value — so an authenticated caller whose token said
/// <c>tenant_id: acme</c> could send <c>X-Tenant-ID: globex</c> and the header won. Every tenant
/// filter downstream then applied faithfully, to the wrong tenant.
/// </para>
/// <para>
/// The claim is now authoritative whenever the caller is authenticated. The header and query
/// parameter remain for callers a token cannot describe — a gateway that has already terminated
/// authentication, a background job, local development — and are only consulted when the token
/// carries no tenant. They are still caller-supplied input: in a deployment where clients reach this
/// service directly, issue tenants in the token rather than relying on the header.
/// </para>
/// </remarks>
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
        var tenantId = ResolveTenantId(context);

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            tenantContext.SetTenantId(tenantId);
        }

        await _next(context);
    }

    private static string? ResolveTenantId(HttpContext context)
    {
        // An authenticated caller's own token is the only source that is not caller-assertable.
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var claimed = context.User.FindFirst("tenant_id")?.Value
                       ?? context.User.FindFirst("tenantId")?.Value;

            if (!string.IsNullOrWhiteSpace(claimed)) return claimed;
        }

        var header = context.Request.Headers[TenantIdHeaderName].ToString();
        if (!string.IsNullOrWhiteSpace(header)) return header;

        var query = context.Request.Query["tenantId"].ToString();
        return string.IsNullOrWhiteSpace(query) ? null : query;
    }
}
