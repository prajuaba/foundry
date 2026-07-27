using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Foundry.Api.Tests;

/// <summary>
/// Authenticates a test request from headers, so tests exercise a real <see cref="ClaimsPrincipal"/>.
/// </summary>
/// <remarks>
/// <para>
/// These tests used to establish identity by substituting <c>ICurrentUserContext</c> and handing it a
/// principal. That is a *parallel* identity: the endpoint's own <c>HttpContext.User</c> stayed
/// anonymous, and only the code reading <c>ICurrentUserContext</c> saw the roles. It let an
/// authorization test pass while the request was, to ASP.NET, unauthenticated — which is the shape of
/// a test that certifies a guarantee nobody is enforcing.
/// </para>
/// <para>
/// Authenticating for real means one identity: endpoint authorization, <c>SecurityBehavior</c> and
/// audit attribution all read the same principal and cannot disagree.
/// </para>
/// </remarks>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    /// <summary>Comma-separated roles to grant. Absent means an anonymous request.</summary>
    public const string RolesHeader = "X-Test-Roles";

    /// <summary>Subject to attribute the request to.</summary>
    public const string SubjectHeader = "X-Test-Subject";

    /// <summary>Tenant claim, for asserting that a token's tenant outranks the X-Tenant-ID header.</summary>
    public const string TenantHeader = "X-Test-Tenant";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(RolesHeader, out var rolesHeader)
            && !Request.Headers.ContainsKey(SubjectHeader))
        {
            // No credentials presented. NoResult, not Fail: it is what an anonymous caller looks
            // like, and it is what must produce a 401 from a RequireAuthorization endpoint.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var subject = Request.Headers.TryGetValue(SubjectHeader, out var s) && !string.IsNullOrWhiteSpace(s)
            ? s.ToString()
            : "test-user";

        var claims = new List<Claim> { new("sub", subject) };

        claims.AddRange(rolesHeader
            .ToString()
            .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
            .Select(role => new Claim("role", role)));

        if (Request.Headers.TryGetValue(TenantHeader, out var tenant) && !string.IsNullOrWhiteSpace(tenant))
        {
            claims.Add(new Claim("tenant_id", tenant.ToString()));
        }

        // Claim types are the raw names the API is configured to read, matching MapInboundClaims=false
        // in AddFoundryAuthentication, so the test principal is shaped like a real decoded token.
        var identity = new ClaimsIdentity(claims, SchemeName, "sub", "role");
        var principal = new ClaimsPrincipal(identity);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, SchemeName)));
    }
}

/// <summary>
/// The API host, configured so tests can authenticate.
/// </summary>
/// <remarks>
/// Applied at the factory rather than per test: the application refuses to start without a token
/// validation configuration, by design, so every test needs this and none should have to remember it.
/// </remarks>
public class AuthenticatedApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.UseTestAuthentication();
}

/// <summary>Wiring helpers for tests that need an authenticated caller.</summary>
public static class TestAuthentication
{
    /// <summary>
    /// A signing key so <c>AddFoundryAuthentication</c> can start. The test scheme below replaces it
    /// as the default; the real JWT path is covered end to end by the runtime smoke test, which mints
    /// genuine tokens.
    /// </summary>
    public const string SigningKey = "test-signing-key-that-is-long-enough-for-hs256";

    /// <summary>Configures a host to accept the test scheme, and makes it the default.</summary>
    public static IWebHostBuilder UseTestAuthentication(this IWebHostBuilder builder)
    {
        builder.UseSetting("Authentication:Jwt:SigningKey", SigningKey);
        builder.UseSetting("Authentication:Jwt:Issuer", "foundry-tests");

        builder.ConfigureServices(services =>
        {
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });

        return builder;
    }

    /// <summary>Presents the given roles on every request from this client.</summary>
    public static HttpClient As(this HttpClient client, params string[] roles)
    {
        client.DefaultRequestHeaders.Remove(TestAuthHandler.RolesHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(",", roles));
        return client;
    }

    /// <summary>A client authenticated with the given roles, with optional extra service overrides.</summary>
    public static HttpClient CreateClientAs<TEntryPoint>(
        this WebApplicationFactory<TEntryPoint> factory,
        string[] roles,
        System.Action<IServiceCollection>? configureServices = null)
        where TEntryPoint : class
    {
        return factory
            .WithWebHostBuilder(builder =>
            {
                builder.UseTestAuthentication();
                if (configureServices is not null) builder.ConfigureServices(configureServices);
            })
            .CreateClient()
            .As(roles);
    }
}
