using System;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Foundry.Core.Telemetry;

namespace Microsoft.Extensions.DependencyInjection;

public class FoundryTelemetryOptions
{
    public string ServiceName { get; set; } = "FoundryService";
    public string ServiceVersion { get; set; } = "1.0.0";
    public bool EnableTracing { get; set; } = true;
    public bool EnableMetrics { get; set; } = true;
}

/// <summary>
/// Service collection extension methods to register OpenTelemetry distributed tracing and metrics.
/// </summary>
public static class FoundryTelemetryExtensions
{
    /// <summary>
    /// Registers ambient correlation context and OpenTelemetry distributed tracing & metrics.
    /// </summary>
    public static IServiceCollection AddFoundryTelemetry(
        this IServiceCollection services,
        Action<FoundryTelemetryOptions>? configure = null)
    {
        var options = new FoundryTelemetryOptions();
        configure?.Invoke(options);

        // Register ambient correlation context
        services.AddSingleton<ICorrelationContext, CorrelationContext>();

        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(serviceName: options.ServiceName, serviceVersion: options.ServiceVersion);

        if (options.EnableTracing || options.EnableMetrics)
        {
            var otel = services.AddOpenTelemetry();

            if (options.EnableTracing)
            {
                otel.WithTracing(builder =>
                {
                    builder
                        .SetResourceBuilder(resourceBuilder)
                        .AddAspNetCoreInstrumentation(opts =>
                        {
                            opts.RecordException = true;
                        })
                        .AddHttpClientInstrumentation();
                });
            }

            if (options.EnableMetrics)
            {
                otel.WithMetrics(builder =>
                {
                    builder
                        .SetResourceBuilder(resourceBuilder)
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation();
                });
            }
        }

        return services;
    }
}
