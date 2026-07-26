using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Foundry.Api.Diagnostics;

public static class Diagnostics
{
    private const string ServiceName = "Foundry.Api";
    
    public static readonly ActivitySource ActivitySource = new(ServiceName);
    public static readonly Meter Meter = new(ServiceName);

    // Metrics counters and gauges
    public static readonly Counter<long> RequestCounter = Meter.CreateCounter<long>(
        "foundry_api_requests_total",
        description: "Total number of dynamically processed requests");

    public static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>(
        "foundry_api_request_duration_seconds",
        unit: "s",
        description: "Duration of dynamically processed requests");

    public static readonly Counter<long> CacheHits = Meter.CreateCounter<long>(
        "foundry_api_cache_hits_total",
        description: "Total number of cache hits in CachingBehavior");

    public static readonly Counter<long> CacheMisses = Meter.CreateCounter<long>(
        "foundry_api_cache_misses_total",
        description: "Total number of cache misses in CachingBehavior");

    public static readonly Counter<long> ValidationFailures = Meter.CreateCounter<long>(
        "foundry_api_validation_failures_total",
        description: "Total number of validation failures in ValidationBehavior");
}
