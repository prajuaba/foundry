using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Foundry.Cli;

public static class TokenMint
{
    public static int Handle(string[] args)
    {
        var signingKey = GetSigningKey(args);
        if (signingKey is null)
        {
            Console.Error.WriteLine("[Error] No signing key provided. Pass --signing-key or set Authentication__Jwt__SigningKey environment variable.");
            return 1;
        }

        // Validate signing key length
        var byteCount = Encoding.UTF8.GetByteCount(signingKey);
        if (byteCount < 32)
        {
            Console.Error.WriteLine($"[Error] Signing key is {byteCount} bytes. HS256 requires at least 32 bytes.");
            return 1;
        }

        // Parse command-line arguments
        var sub = "dev-user";
        var roles = new List<string>();
        var tenant = string.Empty;
        var groups = new List<string>();
        var scopes = new List<string>();
        var audience = string.Empty;
        var issuer = string.Empty;
        var expiresInSeconds = 3600; // default: 1h
        var pretty = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--signing-key" when i + 1 < args.Length:
                    i++;
                    break;
                case "--sub" when i + 1 < args.Length:
                    sub = args[++i];
                    break;
                case "--role" when i + 1 < args.Length:
                    roles.Add(args[++i]);
                    break;
                case "--tenant" when i + 1 < args.Length:
                    tenant = args[++i];
                    break;
                case "--group" when i + 1 < args.Length:
                    groups.Add(args[++i]);
                    break;
                case "--scope" when i + 1 < args.Length:
                    scopes.Add(args[++i]);
                    break;
                case "--audience" when i + 1 < args.Length:
                    audience = args[++i];
                    break;
                case "--issuer" when i + 1 < args.Length:
                    issuer = args[++i];
                    break;
                case "--expires-in" when i + 1 < args.Length:
                    expiresInSeconds = ParseDuration(args[++i]);
                    break;
                case "--pretty":
                    pretty = true;
                    break;
            }
        }

        try
        {
            var now = DateTime.UtcNow;
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new("sub", sub)
            };

            // Add roles as separate claims (one per role)
            foreach (var role in roles)
            {
                claims.Add(new("role", role));
            }

            if (!string.IsNullOrEmpty(tenant))
            {
                claims.Add(new("tenant_id", tenant));
            }

            // Add groups as separate claims (one per group)
            foreach (var group in groups)
            {
                claims.Add(new("groups", group));
            }

            // Add scopes as separate claims (one per scope)
            foreach (var scope in scopes)
            {
                claims.Add(new("scope", scope));
            }

            // Add the iat claim as a numeric type so it serializes as a JSON number, not a string
            claims.Add(new Claim(JwtRegisteredClaimNames.Iat, ToUnixTime(now).ToString(), ClaimValueTypes.Integer64));

            var token = new JwtSecurityToken(
                issuer: string.IsNullOrEmpty(issuer) ? null : issuer,
                audience: string.IsNullOrEmpty(audience) ? null : audience,
                claims: claims,
                notBefore: now,
                expires: now.AddSeconds(expiresInSeconds),
                signingCredentials: credentials);

            var handler = new JwtSecurityTokenHandler();
            var jwtString = handler.WriteToken(token);

            // Output to stdout
            Console.Out.WriteLine(jwtString);

            // Output decoded token to stderr if --pretty
            if (pretty)
            {
                try
                {
                    var decodedToken = handler.ReadJwtToken(jwtString);

                    var headerDict = new Dictionary<string, object>
                    {
                        ["alg"] = "HS256",
                        ["typ"] = "JWT"
                    };

                    var payloadDict = new Dictionary<string, object>();
                    foreach (var claim in decodedToken.Claims)
                    {
                        // Group multiple claims of same type into arrays
                        if (payloadDict.ContainsKey(claim.Type))
                        {
                            if (payloadDict[claim.Type] is List<string> list)
                            {
                                list.Add(claim.Value);
                            }
                            else
                            {
                                var existing = payloadDict[claim.Type];
                                var newList = new List<string> { existing.ToString() ?? "", claim.Value };
                                payloadDict[claim.Type] = newList;
                            }
                        }
                        else
                        {
                            // Try to parse as number for iat, nbf, exp
                            if (claim.Type is "iat" or "nbf" or "exp" &&
                                long.TryParse(claim.Value, out var numValue))
                            {
                                payloadDict[claim.Type] = numValue;
                            }
                            else
                            {
                                payloadDict[claim.Type] = claim.Value;
                            }
                        }
                    }

                    var output = new { header = headerDict, payload = payloadDict };
                    var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
                    Console.Error.WriteLine(json);
                }
                catch
                {
                    // Silently skip pretty printing on error
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Error] Failed to generate token: {ex.Message}");
            return 1;
        }
    }

    private static string? GetSigningKey(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--signing-key" || args[i] == "-k") && i + 1 < args.Length)
            {
                return args[++i];
            }
        }

        var envKey = Environment.GetEnvironmentVariable("Authentication__Jwt__SigningKey");
        return string.IsNullOrEmpty(envKey) ? null : envKey;
    }

    private static long ToUnixTime(DateTime dt)
    {
        return new DateTimeOffset(dt).ToUnixTimeSeconds();
    }

    private static int ParseDuration(string duration)
    {
        duration = duration.Trim().ToLowerInvariant();

        if (duration.EndsWith("h"))
        {
            if (int.TryParse(duration[..^1], out var hours))
            {
                return hours * 3600;
            }
        }
        else if (duration.EndsWith("m"))
        {
            if (int.TryParse(duration[..^1], out var minutes))
            {
                return minutes * 60;
            }
        }
        else if (duration.EndsWith("s"))
        {
            if (int.TryParse(duration[..^1], out var seconds))
            {
                return seconds;
            }
        }
        else if (int.TryParse(duration, out var secs))
        {
            return secs;
        }

        // Default to 1 hour if parsing fails
        return 3600;
    }
}
