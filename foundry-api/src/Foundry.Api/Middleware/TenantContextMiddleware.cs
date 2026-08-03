using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Foundry.Core.Tenant;

namespace Foundry.Api.Middleware;

/// <summary>
/// Controls where <see cref="TenantContextMiddleware"/> is willing to learn the tenant from.
/// </summary>
public sealed class TenantContextOptions
{
    /// <summary>
    /// Whether an <c>X-Tenant-ID</c> header or a <c>tenantId</c> query parameter may set the tenant
    /// when the caller's token does not. Off by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Turn this on only where something in front of this service has already established the tenant
    /// and clients cannot reach it directly — an authenticating gateway, a service mesh, a local
    /// development host. In any deployment a client can reach, this makes the tenant a value the
    /// caller chooses, and every tenant filter downstream will then apply faithfully to whichever
    /// tenant they named.
    /// </para>
    /// <para>
    /// If you enable it, the trust boundary is the thing in front, and it must strip these from
    /// inbound requests before setting its own.
    /// </para>
    /// </remarks>
    public bool TrustCallerAssertedTenant { get; set; }
}

/// <summary>
/// Resolves the ambient tenant for a request from the caller's authenticated token.
/// </summary>
/// <remarks>
/// <para>
/// Two defects, in order. First the header was read before the token, so an authenticated caller
/// whose token said <c>tenant_id: acme</c> could send <c>X-Tenant-ID: globex</c> and the header won.
/// That was fixed by making the claim authoritative — but the header and query parameter were still
/// consulted whenever the token carried no tenant, so a caller holding a valid token that simply did
/// not describe tenancy could still name any tenant they liked. The comment on that code said the
/// token was "the only source that is not caller-assertable" while the code went on to accept two
/// sources that are.
/// </para>
/// <para>
/// The token is now the only source by default. Deployments that legitimately carry the tenant in a
/// header — a gateway that has already terminated authentication — opt in explicitly:
/// </para>
/// <code>
/// services.Configure&lt;TenantContextOptions&gt;(o => o.TrustCallerAssertedTenant = true);
/// </code>
/// <para>
/// When no tenant can be established, none is set. That is deliberate: a multi-tenant write with no
/// ambient tenant fails rather than being written somewhere arbitrary.
/// </para>
/// </remarks>
public class TenantContextMiddleware
{
    /// <summary>Header carrying the tenant, honoured only under <see cref="TenantContextOptions.TrustCallerAssertedTenant"/>.</summary>
    public const string TenantIdHeaderName = "X-Tenant-ID";

    private readonly RequestDelegate _next;
    private readonly TenantContextOptions _options;

    /// <param name="next">The next middleware.</param>
    /// <param name="options">
    /// Optional. Absent, the token is the only accepted source — the safe default, so a host that
    /// configures nothing cannot end up trusting caller-supplied input.
    /// </param>
    public TenantContextMiddleware(RequestDelegate next, IOptions<TenantContextOptions>? options = null)
    {
        _next = next;
        _options = options?.Value ?? new TenantContextOptions();
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

    private string? ResolveTenantId(HttpContext context)
    {
        // An authenticated caller's own token is the only source that is not caller-assertable.
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var claimed = context.User.FindFirst("tenant_id")?.Value
                       ?? context.User.FindFirst("tenantId")?.Value;

            if (!string.IsNullOrWhiteSpace(claimed)) return claimed;
        }

        // Everything below this line is chosen by whoever sent the request.
        if (!_options.TrustCallerAssertedTenant) return null;

        var header = context.Request.Headers[TenantIdHeaderName].ToString();
        if (!string.IsNullOrWhiteSpace(header)) return header;

        var query = context.Request.Query["tenantId"].ToString();
        return string.IsNullOrWhiteSpace(query) ? null : query;
    }
}
