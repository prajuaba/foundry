#pragma warning disable IL2026, IL3050, IL2075, IL2090, IL2070, IL2060
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using MediatR;
using Foundry.Api.Manifest;

namespace Foundry.Api.MediatR.Behaviors;

public static class EntityCacheTracker
{
    public static readonly ConcurrentDictionary<Type, HashSet<string>> EntityCacheKeys = new();
}

public class CachingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IMemoryCache _cache;
    private readonly IDistributedCache? _distributedCache;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<CachingBehavior<TRequest, TResponse>> _logger;
    private readonly Foundry.Core.Tenant.ITenantContext? _tenantContext;

    /// <remarks>
    /// <paramref name="tenantContext"/> is optional so that an application composing this behavior
    /// without the Mongo layer still resolves. Where it is absent the caller fingerprint falls back
    /// to the token's own claims, which is what <see cref="BuildCallerFingerprint"/> reads anyway —
    /// so the key is still per-caller, never shared across them.
    /// </remarks>
    public CachingBehavior(
        IMemoryCache cache,
        IHttpContextAccessor httpContextAccessor,
        ILogger<CachingBehavior<TRequest, TResponse>> logger,
        IDistributedCache? distributedCache = null,
        Foundry.Core.Tenant.ITenantContext? tenantContext = null)
    {
        _cache = cache;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _distributedCache = distributedCache;
        _tenantContext = tenantContext;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var requestType = typeof(TRequest);
        var entityType = GetEntityType(requestType);

        if (IsMutation(requestType))
        {
            _logger.LogInformation("Mutation detected: {RequestType} for Entity: {EntityType}. Invalidating cache.", requestType.Name, entityType.Name);
            var response = await next();
            await InvalidateCacheForEntityAsync(entityType, cancellationToken);
            return response;
        }

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            _logger.LogDebug("HttpContext is null for query {RequestType}. Bypassing cache.", requestType.Name);
            return await next();
        }

        var endpoint = httpContext.GetEndpoint();
        if (endpoint == null)
        {
            _logger.LogDebug("Endpoint is null for query {RequestType}. Bypassing cache.", requestType.Name);
            return await next();
        }

        var endpointConfig = endpoint.Metadata.GetMetadata<EndpointConfig>();
        if (endpointConfig == null)
        {
            _logger.LogDebug("EndpointConfig is null for query {RequestType}. Bypassing cache.", requestType.Name);
            return await next();
        }

        var operationName = GetOperationName(requestType);
        _logger.LogDebug("Query detected: {RequestType} for Entity: {EntityType}, Operation: {OperationName}", requestType.Name, entityType.Name, operationName);

        if (endpointConfig.Caching.TryGetValue(operationName, out var cachingConfig) && cachingConfig.Enabled)
        {
            var cacheKey = BuildCacheKey(requestType, request);
            var ttlSeconds = cachingConfig.TtlSeconds > 0 ? cachingConfig.TtlSeconds : 300;

            // 1. Try L1 Memory Cache
            if (_cache.TryGetValue<TResponse>(cacheKey, out var cachedResponse))
            {
                if (cachedResponse != null)
                {
                    _logger.LogInformation("L1 Cache HIT for key: {CacheKey}", cacheKey);
                    Diagnostics.Diagnostics.CacheHits.Add(1, new KeyValuePair<string, object?>("entity_type", entityType.Name), new KeyValuePair<string, object?>("level", "L1"));
                    return cachedResponse;
                }
            }

            // 2. Try L2 Distributed Cache (if registered)
            if (_distributedCache != null)
            {
                try
                {
                    var distributedVal = await _distributedCache.GetStringAsync(cacheKey, cancellationToken);
                    if (!string.IsNullOrEmpty(distributedVal))
                    {
                        var deserialized = JsonSerializer.Deserialize<TResponse>(distributedVal);
                        if (deserialized != null)
                        {
                            _logger.LogInformation("L2 Cache HIT for key: {CacheKey}", cacheKey);
                            Diagnostics.Diagnostics.CacheHits.Add(1, new KeyValuePair<string, object?>("entity_type", entityType.Name), new KeyValuePair<string, object?>("level", "L2"));
                            
                            // Pop L1 cache for speed on next requests
                            _cache.Set(cacheKey, deserialized, TimeSpan.FromSeconds(ttlSeconds));
                            return deserialized;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read from L2 Distributed Cache for key: {CacheKey}", cacheKey);
                }
            }

            _logger.LogInformation("Cache MISS for key: {CacheKey}. Fetching from next handler.", cacheKey);
            Diagnostics.Diagnostics.CacheMisses.Add(1, new KeyValuePair<string, object?>("entity_type", entityType.Name));

            var response = await next();
            
            var cacheEntryOptions = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ttlSeconds)
            };

            // Write to L1 Memory Cache
            _cache.Set(cacheKey, response, cacheEntryOptions);
            TrackCacheKeyForEntity(entityType, cacheKey);

            // Write to L2 Distributed Cache (if registered)
            if (_distributedCache != null && response != null)
            {
                try
                {
                    var serialized = JsonSerializer.Serialize(response);
                    var distOptions = new DistributedCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ttlSeconds)
                    };
                    await _distributedCache.SetStringAsync(cacheKey, serialized, distOptions, cancellationToken);
                    _logger.LogDebug("L2 Cache populated for key: {CacheKey}", cacheKey);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to write to L2 Distributed Cache for key: {CacheKey}", cacheKey);
                }
            }

            _logger.LogDebug("Cached response for key: {CacheKey} with TTL {TtlSeconds}s", cacheKey, ttlSeconds);
            return response;
        }

        _logger.LogDebug("Caching is disabled for operation: {OperationName}", operationName);
        return await next();
    }

    private bool IsMutation(Type requestType)
    {
        var name = requestType.Name;
        return name.StartsWith("InsertCommand", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("UpdateCommand", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("DeleteCommand", StringComparison.OrdinalIgnoreCase);
    }

    private string GetOperationName(Type requestType)
    {
        var name = requestType.Name;
        if (name.StartsWith("GetByIdQuery", StringComparison.OrdinalIgnoreCase))
        {
            return "GET_BY_ID";
        }
        return "GET";
    }

    /// <summary>
    /// Builds the cache key for one query: the request, plus every part of the caller's identity
    /// that can change what comes back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller half is not decoration. The repository narrows and redacts a result *below* this
    /// behavior — tenant filter, owner filter, grant widening, role exemptions and per-category
    /// masking are all applied there — so a key built only from the request describes a query whose
    /// answer differs per caller, and the first caller to run it decides what everyone else sees.
    /// </para>
    /// <para>
    /// Measured before this was fixed, on a schema whose every entity is multi-tenant: tenant
    /// <c>acme</c> listed its two projects, tenant <c>globex</c> received those same two rows, and a
    /// project <c>globex</c> had just written was not among them. After the TTL the order reversed
    /// and <c>acme</c> read zero rows of its own data. That is a cross-tenant read in a framework
    /// whose headline claim is tenant isolation, and it needed nothing but two tokens and one
    /// endpoint to reproduce.
    /// </para>
    /// <para>
    /// Everything the data layer reads to decide a caller's view is therefore in the key, and the
    /// list is exactly that — not a guess at what matters:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>tenant</b>, from <see cref="ITenantContext"/>, which is what the
    /// repository's own filter uses.</description></item>
    /// <item><description><b>subject</b>, which owner-scoped entities filter rows by.</description></item>
    /// <item><description><b>groups</b>, which widen an owner's rows to a shared set.</description></item>
    /// <item><description><b>roles</b>, because owner-exempt and read-exempt roles see rows their
    /// holder does not own.</description></item>
    /// <item><description><b>scopes</b>, because <c>view:{category}</c> decides whether a masked
    /// property comes back in full — the one entry here that changes the contents of a row rather
    /// than which rows there are.</description></item>
    /// </list>
    /// <para>
    /// The cost is a lower hit rate: two callers share an entry only when all five agree, which for
    /// owner-scoped entities means per-user caching. That is the correct price. An entry shared more
    /// widely than the data it holds is not a faster cache, it is a disclosure.
    /// </para>
    /// <para>
    /// Claim values are sorted so that a principal presenting the same claims in a different order
    /// hits the same entry, and the caller half is hashed rather than inlined: keys are logged, and
    /// subjects and group names do not belong in logs.
    /// </para>
    /// </remarks>
    private string BuildCacheKey(Type requestType, TRequest request)
    {
        var keyParts = new List<string> { requestType.FullName ?? requestType.Name };

        var properties = requestType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0);

        foreach (var property in properties)
        {
            var value = property.GetValue(request);
            keyParts.Add($"{property.Name}:{value?.ToString() ?? "null"}");
        }

        keyParts.Add("caller:" + BuildCallerFingerprint());

        return string.Join(":", keyParts);
    }

    /// <summary>
    /// The caller half of the key, defined once in
    /// <see cref="Foundry.Core.Security.CallerViewFingerprint"/> because the data layer's own cache
    /// keys on the same thing and the two must not disagree about what one caller is.
    /// </summary>
    private string BuildCallerFingerprint()
        => Foundry.Core.Security.CallerViewFingerprint.Compute(
            _tenantContext?.TenantId,
            _httpContextAccessor.HttpContext?.User);

    private Type GetEntityType(Type requestType)
    {
        var genericArguments = requestType.GetGenericArguments();
        if (genericArguments.Length > 0)
        {
            return genericArguments[0];
        }
        return requestType;
    }

    private async Task InvalidateCacheForEntityAsync(Type entityType, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Invalidating cache for type: {EntityTypeQualifiedName}", entityType.AssemblyQualifiedName);
        if (EntityCacheTracker.EntityCacheKeys.TryRemove(entityType, out var keys))
        {
            _logger.LogInformation("Found {Count} cache keys to evict for type: {EntityType}", keys.Count, entityType.Name);
            foreach (var key in keys)
            {
                // Evict from L1 Memory Cache
                _cache.Remove(key);
                
                // Evict from L2 Distributed Cache (if registered)
                if (_distributedCache != null)
                {
                    try
                    {
                        await _distributedCache.RemoveAsync(key, cancellationToken);
                        _logger.LogDebug("Evicted L2 cache key: {CacheKey}", key);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to evict L2 Distributed Cache key: {CacheKey}", key);
                    }
                }

                _logger.LogDebug("Evicted cache key: {CacheKey}", key);
            }
        }
        else
        {
            _logger.LogDebug("No cached keys found to evict for type: {EntityType}", entityType.Name);
        }
    }

    private void TrackCacheKeyForEntity(Type entityType, string cacheKey)
    {
        _logger.LogDebug("Tracking cache key: {CacheKey} for type: {EntityTypeQualifiedName}", cacheKey, entityType.AssemblyQualifiedName);
        EntityCacheTracker.EntityCacheKeys.AddOrUpdate(entityType, 
            new HashSet<string> { cacheKey }, 
            (_, existing) =>
            {
                lock (existing)
                {
                    existing.Add(cacheKey);
                }
                return existing;
            });
    }
}
