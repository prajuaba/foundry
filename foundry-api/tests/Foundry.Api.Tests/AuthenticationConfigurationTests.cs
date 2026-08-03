using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Foundry.Api.Middleware;
using Foundry.Api.Security;
using Foundry.Core.Tenant;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Foundry.Api.Tests;

/// <summary>
/// Token validation configuration.
/// </summary>
/// <remarks>
/// An API that cannot tell a valid token from an invalid one must not start. Every case here is a
/// configuration mistake that would otherwise surface as "all tokens are rejected" or, worse, as
/// tokens being accepted on terms nobody chose.
/// </remarks>
public class AuthenticationConfigurationTests
{
    private static IConfiguration ConfigWith(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(
                Array.ConvertAll(settings, s => new KeyValuePair<string, string?>(s.Key, s.Value))))
            .Build();

    [Fact]
    public void NoAuthorityAndNoSigningKey_IsRefused()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(
            () => services.AddFoundryAuthentication(ConfigWith()));

        Assert.Contains("Authentication:Jwt", error.Message);
        Assert.Contains("Authority", error.Message);
        Assert.Contains("SigningKey", error.Message);
    }

    [Fact]
    public void TheFailureExplainsWhereToPutTheSecret()
    {
        // A message that names the setting but not the mechanism gets the key pasted into
        // appsettings.json, which is the outcome the split exists to prevent.
        var error = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddFoundryAuthentication(ConfigWith()));

        Assert.Contains("user-secrets", error.Message);
        Assert.Contains("not appsettings.json", error.Message);
    }

    [Fact]
    public void BothAuthorityAndSigningKey_IsRefused()
    {
        // Ambiguous rather than redundant: the two validate tokens against different keys, and
        // silently preferring one would decide a security question by implementation order.
        var config = ConfigWith(
            ("Authentication:Jwt:Authority", "https://login.example.com/"),
            ("Authentication:Jwt:SigningKey", "a-signing-key-long-enough-for-hs256-validation"));

        var error = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddFoundryAuthentication(config));

        Assert.Contains("exactly one", error.Message);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("still-far-too-short-for-hs256")]
    public void ASigningKeyBelow256Bits_IsRefused(string key)
    {
        // HS256 needs a 256-bit key. Caught here rather than inside token validation, where it
        // presents as every token being invalid and looks like a client problem.
        var error = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddFoundryAuthentication(
                ConfigWith(("Authentication:Jwt:SigningKey", key))));

        Assert.Contains("32", error.Message);
    }

    [Fact]
    public void AValidSigningKey_RegistersAnAuthenticationScheme()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();

        services.AddFoundryAuthentication(
            ConfigWith(("Authentication:Jwt:SigningKey", "a-signing-key-long-enough-for-hs256-validation")));

        var provider = services.BuildServiceProvider();
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.NotNull(schemes.GetDefaultAuthenticateSchemeAsync().GetAwaiter().GetResult());
    }

    [Fact]
    public void AnAuthorityRegistersAnAuthenticationScheme()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();

        services.AddFoundryAuthentication(
            ConfigWith(("Authentication:Jwt:Authority", "https://login.example.com/")));

        var provider = services.BuildServiceProvider();
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.NotNull(schemes.GetDefaultAuthenticateSchemeAsync().GetAwaiter().GetResult());
    }

    [Fact]
    public void OidcMetadataRequiresHttpsByDefault()
    {
        // This defaulted to false, permitting discovery of the signing keys the whole scheme
        // depends on over plaintext HTTP.
        Assert.True(new FoundryOidcOptions().RequireHttpsMetadata);
        Assert.True(new FoundryAuthenticationOptions().RequireHttpsMetadata);
    }
}

/// <summary>
/// The startup guard on generated endpoints.
/// </summary>
public class GeneratedEndpointSecurityGuardTests
{
    [Fact]
    public void NoAuthenticationScheme_FailsAtStartupNamingTheFix()
    {
        var provider = new ServiceCollection().BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(
            () => GeneratedEndpointSecurityGuard.EnsureAuthenticationIsConfigured(provider));

        Assert.Contains("AddFoundryAuthentication", error.Message);
        Assert.Contains("UseAuthentication", error.Message);
    }

    [Fact]
    public void AnAuthenticationScheme_Passes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddFoundryAuthentication(new FoundryAuthenticationOptions
        {
            SigningKey = "a-signing-key-long-enough-for-hs256-validation"
        });

        GeneratedEndpointSecurityGuard.EnsureAuthenticationIsConfigured(services.BuildServiceProvider());
    }
}

/// <summary>
/// Where the ambient tenant comes from.
/// </summary>
public class TenantContextMiddlewareTests
{
    private sealed class RecordingTenantContext : ITenantContext
    {
        public string? TenantId { get; private set; }
        public bool HasTenant => !string.IsNullOrWhiteSpace(TenantId);
        public void SetTenantId(string tenantId) => TenantId = tenantId;
    }

    private static async Task<string?> ResolveAsync(
        string? headerTenant = null,
        string? queryTenant = null,
        string? claimTenant = null,
        bool authenticated = false,
        bool trustCallerAsserted = false)
    {
        var context = new DefaultHttpContext();

        if (headerTenant is not null)
            context.Request.Headers[TenantContextMiddleware.TenantIdHeaderName] = headerTenant;

        if (queryTenant is not null)
            context.Request.QueryString = QueryString.Create("tenantId", queryTenant);

        if (authenticated)
        {
            var claims = new List<Claim> { new("sub", "someone") };
            if (claimTenant is not null) claims.Add(new Claim("tenant_id", claimTenant));
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        }

        var options = Microsoft.Extensions.Options.Options.Create(
            new TenantContextOptions { TrustCallerAssertedTenant = trustCallerAsserted });

        var tenantContext = new RecordingTenantContext();
        await new TenantContextMiddleware(_ => Task.CompletedTask, options)
            .InvokeAsync(context, tenantContext);

        return tenantContext.TenantId;
    }

    [Fact]
    public async Task ATokensTenantOutranksTheHeader()
    {
        // The ordering was the other way round, so an authenticated caller could override the tenant
        // their own token asserted just by setting a header. Every tenant filter downstream then
        // applied faithfully, to the wrong tenant.
        Assert.Equal("acme", await ResolveAsync(
            headerTenant: "globex", claimTenant: "acme", authenticated: true));
    }

    [Fact]
    public async Task ByDefaultTheHeaderCannotSetTheTenantEvenWhenTheTokenCarriesNone()
    {
        // The hole this closes. A caller holding a valid token that simply does not describe
        // tenancy could name any tenant they liked, and every filter downstream then applied
        // faithfully to that one. Ranking the claim above the header was not enough, because a
        // token with no claim left the header unopposed.
        Assert.Null(await ResolveAsync(headerTenant: "globex", claimTenant: null, authenticated: true));
    }

    [Fact]
    public async Task ByDefaultTheHeaderCannotSetTheTenantForAnAnonymousCaller()
    {
        Assert.Null(await ResolveAsync(headerTenant: "globex"));
    }

    [Fact]
    public async Task ByDefaultTheQueryParameterCannotSetTheTenant()
    {
        Assert.Null(await ResolveAsync(queryTenant: "acme"));
    }

    [Fact]
    public async Task TheHeaderIsHonouredOnlyWhenTheDeploymentOptsIn()
    {
        // The legitimate case: something in front has already established the tenant, and clients
        // cannot reach this service directly.
        Assert.Equal("globex", await ResolveAsync(
            headerTenant: "globex", claimTenant: null, authenticated: true, trustCallerAsserted: true));

        Assert.Equal("globex", await ResolveAsync(headerTenant: "globex", trustCallerAsserted: true));
    }

    [Fact]
    public async Task AQueryParameterIsTheLastResortWhenOptedIn()
    {
        Assert.Equal("acme", await ResolveAsync(queryTenant: "acme", trustCallerAsserted: true));

        Assert.Equal("globex", await ResolveAsync(
            headerTenant: "globex", queryTenant: "acme", trustCallerAsserted: true));
    }

    [Fact]
    public async Task ATokensTenantStillOutranksTheHeaderWhenOptedIn()
    {
        Assert.Equal("acme", await ResolveAsync(
            headerTenant: "globex", claimTenant: "acme", authenticated: true, trustCallerAsserted: true));
    }

    [Fact]
    public async Task NoTenantAnywhereLeavesTheContextUnset()
    {
        // Not an empty string: the repository distinguishes "no tenant" from a tenant named "".
        Assert.Null(await ResolveAsync());
    }

    [Fact]
    public async Task AnUnauthenticatedPrincipalsClaimIsIgnored()
    {
        // A ClaimsPrincipal with no authentication type is not authenticated, and its claims are
        // whatever an unauthenticated request happened to carry. Opted in, the header is what is
        // left; by default there is nothing.
        Assert.Null(await ResolveAsync(
            headerTenant: "globex", claimTenant: "acme", authenticated: false));

        Assert.Equal("globex", await ResolveAsync(
            headerTenant: "globex", claimTenant: "acme", authenticated: false, trustCallerAsserted: true));
    }

    /// <summary>A host that configures nothing must get the safe behaviour, not the convenient one.</summary>
    [Fact]
    public async Task TheSafeBehaviourIsWhatAHostGetsWithoutConfiguringAnything()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[TenantContextMiddleware.TenantIdHeaderName] = "globex";

        var tenantContext = new RecordingTenantContext();

        // No options argument at all — the shape every existing host uses.
        await new TenantContextMiddleware(_ => Task.CompletedTask).InvokeAsync(context, tenantContext);

        Assert.Null(tenantContext.TenantId);
    }
}
