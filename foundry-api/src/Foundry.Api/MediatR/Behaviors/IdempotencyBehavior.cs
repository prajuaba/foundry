#pragma warning disable IL2026, IL3050, IL2075, IL2090, IL2070, IL2060
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using MediatR;
using Foundry.Api.Middleware;

namespace Foundry.Api.MediatR.Behaviors;

/// <summary>
/// Intercepts mutation commands (insert, update, delete) and checks for duplicate request keys
/// supplied via the "X-Idempotency-Key" header to guarantee at-most-once processing.
/// </summary>
/// <typeparam name="TRequest">Type of the incoming request.</typeparam>
/// <typeparam name="TResponse">Type of the expected response.</typeparam>
public class IdempotencyBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private const string IdempotencyHeaderName = "X-Idempotency-Key";
    private readonly IMemoryCache _cache;
    private readonly IDistributedCache? _distributedCache;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<IdempotencyBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdempotencyBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    public IdempotencyBehavior(
        IMemoryCache cache,
        IHttpContextAccessor httpContextAccessor,
        ILogger<IdempotencyBehavior<TRequest, TResponse>> logger,
        IDistributedCache? distributedCache = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _distributedCache = distributedCache;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var requestType = typeof(TRequest);
        if (!IsMutation(requestType))
        {
            return await next();
        }

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            return await next();
        }

        if (!httpContext.Request.Headers.TryGetValue(IdempotencyHeaderName, out var headerValues))
        {
            return await next();
        }

        var idempotencyKey = headerValues.ToString();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return await next();
        }

        var cacheKey = $"idempotency:{idempotencyKey}";

        // 1. Check if key exists in memory cache (L1) or distributed cache (L2)
        string? existingStatus = null;
        if (_cache.TryGetValue<string>(cacheKey, out var l1Status))
        {
            existingStatus = l1Status;
        }
        else if (_distributedCache != null)
        {
            try
            {
                existingStatus = await _distributedCache.GetStringAsync(cacheKey, cancellationToken);
            }
            catch (Exception ex)
            {
                // Fails closed, because carrying on here does not degrade the feature -- it silently
                // removes it. L1 is per instance, so behind more than one replica the distributed
                // cache is the only thing that sees a duplicate, and a warning-and-continue meant a
                // cache outage turned "at most once" into "at least once" while every request
                // returned 200. For the operation this exists to protect, that is a double charge.
                //
                // The caller asked for the guarantee by sending the header, so refusing is the
                // answer they can act on: a 409 is retryable, a duplicate payment is not.
                _logger.LogError(ex,
                    "Idempotency key status could not be read from the distributed cache. Refusing "
                    + "the request rather than processing it without the guarantee it asked for.");

                throw new IdempotencyException(idempotencyKey,
                    "The idempotency store is unavailable, so this request cannot be checked for "
                    + "duplication. Retry it with the same key.");
            }
        }

        if (existingStatus != null)
        {
            _logger.LogWarning("Duplicate request detected for idempotency key: {Key} with status: {Status}", idempotencyKey, existingStatus);
            
            if (existingStatus == "in-flight")
            {
                throw new IdempotencyException(idempotencyKey, "A request with the same idempotency key is currently in progress.");
            }
            else
            {
                throw new IdempotencyException(idempotencyKey, "A request with the same idempotency key has already been executed.");
            }
        }

        // 2. Lock the key (mark as "in-flight")
        _cache.Set(cacheKey, "in-flight", TimeSpan.FromMinutes(5));
        if (_distributedCache != null)
        {
            try
            {
                var options = new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
                };
                await _distributedCache.SetStringAsync(cacheKey, "in-flight", options, cancellationToken);
            }
            catch (Exception ex)
            {
                // Same reasoning, and the same moment: nothing has executed yet. An unrecorded
                // in-flight marker means a concurrent duplicate on another replica sees no lock and
                // runs the command alongside this one.
                _logger.LogError(ex,
                    "The in-flight idempotency marker could not be written to the distributed cache. "
                    + "Refusing the request rather than processing it unprotected.");

                _cache.Remove(cacheKey);

                throw new IdempotencyException(idempotencyKey,
                    "The idempotency store is unavailable, so this request cannot be locked against "
                    + "duplication. Retry it with the same key.");
            }
        }

        try
        {
            var response = await next();

            // 3. Mark as "completed" on success with 1-hour expiration
            _cache.Set(cacheKey, "completed", TimeSpan.FromHours(1));
            if (_distributedCache != null)
            {
                try
                {
                    var options = new DistributedCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1)
                    };
                    await _distributedCache.SetStringAsync(cacheKey, "completed", options, cancellationToken);
                }
                catch (Exception ex)
                {
                    // Not fatal, deliberately, and the asymmetry is the point: the command has
                    // already succeeded. Failing the response would make the caller retry, and the
                    // retry is exactly what would run it a second time. Logged at error because a
                    // later duplicate will now get through, which someone needs to know.
                    _logger.LogError(ex,
                        "The completed idempotency marker could not be written to the distributed "
                        + "cache. A later retry of key {Key} may execute again.", idempotencyKey);
                }
            }

            return response;
        }
        catch
        {
            // 4. Remove the key on failure to allow retry
            _cache.Remove(cacheKey);
            if (_distributedCache != null)
            {
                try
                {
                    await _distributedCache.RemoveAsync(cacheKey, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove key from L2 distributed cache after command failure.");
                }
            }

            throw;
        }
    }

    private static bool IsMutation(Type requestType)
    {
        var name = requestType.Name;
        return name.StartsWith("InsertCommand", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("UpdateCommand", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("DeleteCommand", StringComparison.OrdinalIgnoreCase);
    }
}
