using System;

namespace Foundry.Mongo.Repositories;

/// <summary>
/// Configuration options specifically for the CachedRepository decorator.
/// </summary>
public sealed class CachedRepositoryOptions
{
    /// <summary>The default Time-To-Live (TTL) for cache entries.</summary>
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromMinutes(5);
}
