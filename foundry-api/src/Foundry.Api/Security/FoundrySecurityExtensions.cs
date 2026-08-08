using System;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Foundry.Core.Security;

namespace Microsoft.Extensions.DependencyInjection;

public class FoundryOidcOptions
{
    public string Authority { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Whether the OIDC metadata document must be fetched over HTTPS. Defaults to <c>true</c>.
    /// </summary>
    /// <remarks>
    /// This defaulted to <c>false</c>, which silently permits discovery over plaintext HTTP and puts
    /// the signing keys the whole scheme depends on on the wire. Turning it off is occasionally
    /// necessary against a local identity provider; it should be a decision the application states,
    /// not one it inherits.
    /// </remarks>
    public bool RequireHttpsMetadata { get; set; } = true;
}

/// <summary>
/// How the API validates bearer tokens.
/// </summary>
/// <remarks>
/// Two shapes are supported and exactly one must be configured:
/// <list type="bullet">
/// <item><description>
/// <see cref="Authority"/> — an OIDC provider (Entra ID, Keycloak, Auth0). Signing keys are
/// discovered and rotated automatically. This is the production shape.
/// </description></item>
/// <item><description>
/// <see cref="SigningKey"/> — a symmetric key for tokens this system issues itself. Used for local
/// development, tests, and service-to-service tokens minted inside a trust boundary.
/// </description></item>
/// </list>
/// </remarks>
public class FoundryAuthenticationOptions
{
    /// <summary>OIDC authority, e.g. <c>https://login.microsoftonline.com/{tenant}/v2.0</c>.</summary>
    public string? Authority { get; set; }

    /// <summary>Expected <c>aud</c> claim. Validated whenever it is set.</summary>
    public string? Audience { get; set; }

    /// <summary>Expected <c>iss</c> claim, for symmetric-key validation.</summary>
    public string? Issuer { get; set; }

    /// <summary>Symmetric signing key. At least 32 bytes, as HS256 requires a 256-bit key.</summary>
    public string? SigningKey { get; set; }

    /// <summary>Whether OIDC metadata must be retrieved over HTTPS.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Claim carrying the caller's roles. This is what the generated endpoints' role requirements
    /// are matched against.
    /// </summary>
    /// <remarks>
    /// Identity providers disagree: Entra ID emits <c>roles</c>, Keycloak <c>realm_access.roles</c>,
    /// many others <c>role</c>. Getting this wrong does not fail loudly — it authenticates the caller
    /// and then finds no roles, so every role-protected endpoint answers 403 and looks like a
    /// permissions problem rather than a configuration one.
    /// </remarks>
    public string RoleClaimType { get; set; } = "role";

    /// <summary>
    /// The claim types read as group memberships, for grant-based sharing (see <see cref="Foundry.Core.Security.ISharedResource"/>).
    /// Configurable because a deployment whose identity provider emits a differently-named group claim
    /// would otherwise silently grant nothing, which is indistinguishable from "correctly has no access".
    /// </summary>
    public string[] GroupClaimTypes { get; set; } = ["groups", "group"];

    /// <summary>Claim carrying the caller's identifier, used for audit attribution.</summary>
    public string NameClaimType { get; set; } = "sub";
}

/// <summary>
/// Extension methods for registering enterprise OIDC/OAuth2 authentication and security.
/// </summary>
public static class FoundrySecurityExtensions
{
    /// <summary>Minimum symmetric key length. HS256 signs with a 256-bit key.</summary>
    private const int MinimumSigningKeyBytes = 32;

    /// <summary>
    /// Registers bearer authentication from configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what generated endpoints' <c>RequireAuthorization</c> resolves against. Without an
    /// authentication scheme registered, every generated endpoint answers 500 on the first request
    /// rather than 401 — so this refuses to register a scheme it cannot validate tokens with, and
    /// says which setting is missing.
    /// </para>
    /// <para>
    /// Nothing is inferred and nothing is defaulted. An API that cannot tell a valid token from an
    /// invalid one must not start.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="sectionName">Configuration section holding <see cref="FoundryAuthenticationOptions"/>.</param>
    public static IServiceCollection AddFoundryAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "Authentication:Jwt")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new FoundryAuthenticationOptions();
        configuration.GetSection(sectionName).Bind(options);

        return services.AddFoundryAuthentication(options, sectionName);
    }

    /// <summary>
    /// Registers bearer authentication from explicit options.
    /// </summary>
    public static IServiceCollection AddFoundryAuthentication(
        this IServiceCollection services,
        FoundryAuthenticationOptions options,
        string sectionName = "Authentication:Jwt")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        var hasAuthority = !string.IsNullOrWhiteSpace(options.Authority);
        var hasSigningKey = !string.IsNullOrWhiteSpace(options.SigningKey);

        if (hasAuthority && hasSigningKey)
        {
            throw new InvalidOperationException(
                $"'{sectionName}' sets both 'Authority' and 'SigningKey'. Configure exactly one: "
                + "'Authority' to trust an OIDC provider's keys, or 'SigningKey' to validate tokens "
                + "this system issues itself.");
        }

        if (!hasAuthority && !hasSigningKey)
        {
            throw new InvalidOperationException(
                $"No bearer token validation is configured at '{sectionName}'. The generated API "
                + "endpoints require an authenticated caller, so the application cannot serve any "
                + "request without it.\n"
                + $"Set '{sectionName}:Authority' to your OIDC provider (production), or "
                + $"'{sectionName}:SigningKey' to a key of at least {MinimumSigningKeyBytes} bytes for "
                + "self-issued tokens.\n"
                + "Supply it through user-secrets, an environment variable "
                + $"({sectionName.Replace(':', '_')}__SigningKey) or your secret store — not appsettings.json.");
        }

        if (hasSigningKey && Encoding.UTF8.GetByteCount(options.SigningKey!) < MinimumSigningKeyBytes)
        {
            throw new InvalidOperationException(
                $"'{sectionName}:SigningKey' is {Encoding.UTF8.GetByteCount(options.SigningKey!)} bytes. "
                + $"HS256 requires at least {MinimumSigningKeyBytes}. A short key is rejected here rather "
                + "than deep inside token validation, where it surfaces as every token being invalid.");
        }

        GroupClaims.Types = options.GroupClaimTypes;

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;

                // Claim names are used exactly as the token carries them. The default inbound mapping
                // rewrites 'role' and 'sub' to long WS-Federation URIs, so a RoleClaimType naming the
                // claim the provider actually emits would then match nothing -- and the failure looks
                // like a caller lacking a role rather than a mapping quirk.
                jwt.MapInboundClaims = false;

                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ValidateAudience = !string.IsNullOrWhiteSpace(options.Audience),
                    ValidAudience = options.Audience,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    RoleClaimType = options.RoleClaimType,
                    NameClaimType = options.NameClaimType
                };

                if (!string.IsNullOrWhiteSpace(options.Authority))
                {
                    jwt.Authority = options.Authority;
                    jwt.Audience = options.Audience;
                    jwt.TokenValidationParameters.ValidateIssuer = true;
                }
                else
                {
                    jwt.TokenValidationParameters.ValidateIssuer =
                        !string.IsNullOrWhiteSpace(options.Issuer);
                    jwt.TokenValidationParameters.ValidIssuer = options.Issuer;
                    jwt.TokenValidationParameters.IssuerSigningKey =
                        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey!));
                }
            });

        services.AddAuthorization();

        return services;
    }

    /// <summary>
    /// Registers standard JWT Bearer authentication for enterprise OIDC identity providers (Keycloak, Entra ID, Auth0).
    /// </summary>
    public static IServiceCollection AddFoundryOIDC(
        this IServiceCollection services,
        Action<FoundryOidcOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var oidc = new FoundryOidcOptions();
        configure(oidc);

        return services.AddFoundryAuthentication(new FoundryAuthenticationOptions
        {
            Authority = oidc.Authority,
            Audience = oidc.Audience,
            RequireHttpsMetadata = oidc.RequireHttpsMetadata
        });
    }
}
