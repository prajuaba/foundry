using System;
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

namespace Foundry.IntegrationTests;

/// <summary>
/// Authenticates a test request from headers.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately duplicated from the API test project rather than shared through
/// <c>Foundry.Testing</c>. That library ships, and a handler whose entire purpose is to grant
/// arbitrary roles on request is not something to publish in a package an application can reference:
/// the convenience is worth far less than the risk of it being registered somewhere real.
/// </para>
/// <para>
/// These tests previously established identity by substituting <c>ICurrentUserContext</c>, which left
/// <c>HttpContext.User</c> anonymous — a parallel identity that only the code reading that interface
/// could see.
/// </para>
/// </remarks>
public sealed class IntegrationTestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "IntegrationTest";
    public const string RolesHeader = "X-Test-Roles";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(RolesHeader, out var roles))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new("sub", "integration-test") };
        claims.AddRange(roles
            .ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(role => new Claim("role", role)));

        var identity = new ClaimsIdentity(claims, SchemeName, "sub", "role");

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>The sample API host, configured so integration tests can authenticate.</summary>
public class AuthenticatedSampleFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.UseIntegrationTestAuthentication();
}

public static class IntegrationTestAuthentication
{
    /// <summary>
    /// A signing key so the host can start. The scheme below replaces it as the default; the real
    /// JWT path is proven end to end by the runtime smoke test, which mints genuine tokens.
    /// </summary>
    public const string SigningKey = "integration-test-signing-key-long-enough-for-hs256";

    public static IWebHostBuilder UseIntegrationTestAuthentication(this IWebHostBuilder builder)
    {
        builder.UseSetting("Authentication:Jwt:SigningKey", SigningKey);
        builder.UseSetting("Authentication:Jwt:Issuer", "foundry-integration-tests");

        builder.ConfigureServices(services =>
        {
            services.AddAuthentication(IntegrationTestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, IntegrationTestAuthHandler>(
                    IntegrationTestAuthHandler.SchemeName, _ => { });
        });

        return builder;
    }

    public static HttpClient As(this HttpClient client, params string[] roles)
    {
        client.DefaultRequestHeaders.Remove(IntegrationTestAuthHandler.RolesHeader);
        client.DefaultRequestHeaders.Add(IntegrationTestAuthHandler.RolesHeader, string.Join(",", roles));
        return client;
    }
}
