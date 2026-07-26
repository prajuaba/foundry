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
                _logger.LogWarning(ex, "Failed to read idempotency key status from L2 distributed cache.");
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
                _logger.LogWarning(ex, "Failed to write in-flight status to L2 distributed cache.");
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
                    _logger.LogWarning(ex, "Failed to write completed status to L2 distributed cache.");
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
