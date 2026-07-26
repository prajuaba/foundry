using System;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Microsoft.Extensions.DependencyInjection;

public class FoundryOidcOptions
{
    public string Authority { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public bool RequireHttpsMetadata { get; set; } = false;
}

/// <summary>
/// Extension methods for registering enterprise OIDC/OAuth2 authentication and security.
/// </summary>
public static class FoundrySecurityExtensions
{
    /// <summary>
    /// Registers standard JWT Bearer authentication for enterprise OIDC identity providers (Keycloak, Entra ID, Auth0).
    /// </summary>
    public static IServiceCollection AddFoundryOIDC(
        this IServiceCollection services,
        Action<FoundryOidcOptions> configure)
    {
        var options = new FoundryOidcOptions();
        configure(options);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwtOpts =>
            {
                jwtOpts.Authority = options.Authority;
                jwtOpts.Audience = options.Audience;
                jwtOpts.RequireHttpsMetadata = options.RequireHttpsMetadata;
                jwtOpts.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = !string.IsNullOrEmpty(options.Authority),
                    ValidateAudience = !string.IsNullOrEmpty(options.Audience),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });

        services.AddAuthorization();

        return services;
    }
}
