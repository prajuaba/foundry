using System;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Foundry.Api.Security;

/// <summary>
/// Startup check that generated endpoints can actually enforce the access they declare.
/// </summary>
/// <remarks>
/// <para>
/// Generated endpoints carry <c>RequireAuthorization</c>. If no authentication scheme is registered,
/// ASP.NET cannot challenge, and the first request to any endpoint fails with
/// <c>"No authenticationScheme was specified"</c> — a 500, from inside the framework, on a route that
/// looks fine in the source. This turns that into a startup failure that names the missing call.
/// </para>
/// <para>
/// The check exists because of the shape of the defect it replaces. Roles declared in a schema used to
/// be written into the endpoint's OpenAPI description and nowhere else: the documentation stated
/// "Requires roles: Admin", the endpoint advertised 401 and 403 responses, and it was open to anyone.
/// Enforcement that can be silently absent is worse than no enforcement, because it reads as present.
/// </para>
/// </remarks>
public static class GeneratedEndpointSecurityGuard
{
    /// <summary>
    /// Throws if generated endpoints require authorization but nothing can authenticate a caller.
    /// </summary>
    /// <param name="services">The application's service provider.</param>
    public static void EnsureAuthenticationIsConfigured(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var schemes = services.GetService<IAuthenticationSchemeProvider>();

        var defaultScheme = schemes?.GetDefaultAuthenticateSchemeAsync().GetAwaiter().GetResult();
        if (defaultScheme is not null) return;

        var anyScheme = schemes?.GetAllSchemesAsync().GetAwaiter().GetResult();
        if (anyScheme is not null)
        {
            foreach (var _ in anyScheme) return;
        }

        throw new InvalidOperationException(
            "The generated API endpoints require an authenticated caller, but no authentication "
            + "scheme is registered, so no request could ever be authorised.\n"
            + "Add one before building the application:\n"
            + "    builder.Services.AddFoundryAuthentication(builder.Configuration);\n"
            + "and add the middleware before mapping endpoints:\n"
            + "    app.UseAuthentication();\n"
            + "    app.UseAuthorization();");
    }
}
