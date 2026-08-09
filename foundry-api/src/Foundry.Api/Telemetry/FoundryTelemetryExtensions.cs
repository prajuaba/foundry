using System;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Foundry.Core.Telemetry;

namespace Microsoft.Extensions.DependencyInjection;

public class FoundryTelemetryOptions
{
    private static readonly string ServiceVersionValue = GetServiceVersion();

    public string ServiceName { get; set; } = "FoundryService";

    /// <summary>
    /// Service version read from entry assembly's AssemblyInformationalVersionAttribute. Falls back to 1.0.0 if unavailable.
    /// Build metadata (everything after '+') is stripped.
    /// </summary>
    public string ServiceVersion { get; set; } = ServiceVersionValue;

    public bool EnableTracing { get; set; } = true;
    public bool EnableMetrics { get; set; } = true;

    /// <summary>
    /// OTLP exporter endpoint URI. When null or empty, no exporter is added and telemetry stays in-process.
    /// </summary>
    public string? OtlpEndpoint { get; set; } = null;

    /// <summary>
    /// Enable console exporter for local development debugging.
    /// </summary>
    public bool EnableConsoleExporter { get; set; } = false;

    private static string GetServiceVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
        var plusIndex = version.IndexOf('+');
        return plusIndex >= 0 ? version.Substring(0, plusIndex) : version;
    }
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

        // Resolve and validate OTLP endpoint once, before pipeline setup
        Uri? otlpUri = ResolveAndValidateOtlpEndpoint(options);

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

                    if (otlpUri is not null)
                    {
                        builder.AddOtlpExporter(o => o.Endpoint = otlpUri);
                    }

                    if (options.EnableConsoleExporter)
                    {
                        builder.AddConsoleExporter();
                    }
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

                    if (otlpUri is not null)
                    {
                        builder.AddOtlpExporter(o => o.Endpoint = otlpUri);
                    }

                    if (options.EnableConsoleExporter)
                    {
                        builder.AddConsoleExporter();
                    }
                });
            }
        }

        return services;
    }

    private static Uri? ResolveAndValidateOtlpEndpoint(FoundryTelemetryOptions options)
    {
        string? endpointValue = null;
        string? source = null;

        if (!string.IsNullOrEmpty(options.OtlpEndpoint))
        {
            endpointValue = options.OtlpEndpoint;
            source = "FoundryTelemetryOptions.OtlpEndpoint";
        }
        else
        {
            var envValue = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
            if (!string.IsNullOrEmpty(envValue))
            {
                endpointValue = envValue;
                source = "the OTEL_EXPORTER_OTLP_ENDPOINT environment variable";
            }
        }

        if (endpointValue is null)
        {
            return null;
        }

        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Invalid OTLP endpoint '{endpointValue}' configured via {source}. Expected an absolute URI, for example http://localhost:4317.");
        }

        return uri;
    }
}
