using System;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.DependencyInjection;

public class FoundryRateLimitingOptions
{
    public int PermitLimit { get; set; } = 100;
    public int WindowSeconds { get; set; } = 60;
    public int QueueLimit { get; set; } = 20;
}

/// <summary>
/// Extension methods for setting up production rate limiting middleware.
/// </summary>
public static class FoundryRateLimitingExtensions
{
    /// <summary>
    /// Configures sliding window rate limiting policy for API endpoints.
    /// </summary>
    public static IServiceCollection AddFoundryRateLimiter(
        this IServiceCollection services,
        Action<FoundryRateLimitingOptions>? configure = null)
    {
        var options = new FoundryRateLimitingOptions();
        configure?.Invoke(options);

        services.AddRateLimiter(limiterOpts =>
        {
            limiterOpts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiterOpts.AddSlidingWindowLimiter(policyName: "FoundryRateLimiter", slidingOpts =>
            {
                slidingOpts.PermitLimit = options.PermitLimit;
                slidingOpts.Window = TimeSpan.FromSeconds(options.WindowSeconds);
                slidingOpts.SegmentsPerWindow = 4;
                slidingOpts.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                slidingOpts.QueueLimit = options.QueueLimit;
            });
        });

        return services;
    }
}
