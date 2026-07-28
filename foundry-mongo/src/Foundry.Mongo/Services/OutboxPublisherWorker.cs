using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Foundry.Core.Outbox;
using Foundry.Core.Entities;
using MongoDB.Bson;
using Foundry.Mongo.Repositories;
using Foundry.Core.Paging;

namespace Foundry.Mongo.Services;

/// <summary>
/// Background worker that periodically polls the database outbox for unprocessed messages
/// and dispatches them via the registered <see cref="IOutboxDispatcher"/>.
/// </summary>
public sealed class OutboxPublisherWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OutboxPublisherWorker> _logger;
    private readonly TimeSpan _pollingInterval;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxPublisherWorker"/> class.
    /// </summary>
    public OutboxPublisherWorker(IServiceProvider serviceProvider, ILogger<OutboxPublisherWorker> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pollingInterval = TimeSpan.FromSeconds(2); // Poll every 2 seconds
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Outbox Publisher Background Worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOutboxMessagesAsync(stoppingToken);
                await Task.Delay(_pollingInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during outbox message polling. Retrying in 5 seconds.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("Outbox Publisher Background Worker stopped.");
    }

    /// <summary>Attempts made before a message is abandoned.</summary>
    private const int MaxAttempts = 5;

    /// <summary>Delay before the next attempt, doubling each time and capped.</summary>
    /// <remarks>
    /// 10s, 20s, 40s, 80s, then the cap — about four minutes across the five attempts, against the
    /// ten seconds it used to be.
    /// </remarks>
    private static TimeSpan BackoffFor(int retryCount)
        => TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, retryCount)));

    private async Task ProcessOutboxMessagesAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<OutboxMessage>>();
        var dispatcher = scope.ServiceProvider.GetService<IOutboxDispatcher>();

        if (dispatcher == null)
        {
            _logger.LogWarning("No IOutboxDispatcher is registered. Outbox messages cannot be processed.");
            return;
        }

        // Query unprocessed messages with retry limits, oldest first.
        //
        // This passed sortBy: null with SortOrder.Descending, and FindManyAsync only applies a sort
        // when sortBy is non-empty -- so no sort was applied at all and the requested order was
        // silently ignored. Messages were published in whatever order MongoDB returned them, which
        // defeats the ordering guarantee the outbox exists to provide.
        //
        // Sorted on "_id" rather than "CreatedAt": ObjectId is monotonic by generation time, and
        // "_id" is the field name regardless of the camelCase convention pack the driver is
        // registered with. Sorting by "CreatedAt" would silently target a field that does not exist
        // under that convention, which is the same class of no-op being fixed here.
        // Due now, not given up on. "RetryCount < 5" used to carry both meanings at once, which is
        // why exhaustion was invisible: the fifth failure simply stopped matching the query.
        var now = DateTime.UtcNow;

        var messages = await repository.FindManyAsync(
            m => m.ProcessedAt == null
                 && m.DeadLetteredAt == null
                 && (m.NextAttemptAt == null || m.NextAttemptAt <= now),
            sortBy: "_id",
            sortOrder: SortOrder.Ascending,
            limit: 100,
            session: null,
            ct: ct
        );
        if (!messages.Any())
        {
            return;
        }

        _logger.LogInformation("Found {Count} outbox messages to publish.", messages.Count);

        foreach (var message in messages)
        {
            try
            {
                await dispatcher.DispatchAsync(
                    message.EventType,
                    message.Payload,
                    message.CorrelationId,
                    message.TraceParent,
                    message.Topic,
                    ct
                );

                message.ProcessedAt = DateTime.UtcNow;
                message.ErrorMessage = null;
                message.NextAttemptAt = null;
                await repository.UpdateAsync(message, null, ct);

                _logger.LogInformation("Successfully published outbox message {Id} of type {Type}.", message.Id, message.EventType);
            }
            catch (Exception ex)
            {
                message.RetryCount++;
                message.ErrorMessage = ex.Message;

                if (message.RetryCount >= MaxAttempts)
                {
                    // Said out loud, once, at the moment it happens. This used to be the point where
                    // the message silently stopped matching the query -- so a broker outage looked
                    // identical to a queue draining normally, and the messages were still sitting in
                    // the collection with nothing marking them as abandoned.
                    message.DeadLetteredAt = DateTime.UtcNow;
                    await repository.UpdateAsync(message, null, ct);

                    _logger.LogCritical(ex,
                        "Outbox message {Id} of type {Type} was abandoned after {Count} failed attempts "
                        + "and will not be retried. It remains in the outbox collection with "
                        + "DeadLetteredAt set; republishing it requires clearing that field.",
                        message.Id, message.EventType, message.RetryCount);

                    continue;
                }

                // Exponential, because the retries used to run on the polling interval: five attempts
                // two seconds apart meant a ten-second outage exhausted the message. The same five
                // attempts now span minutes, which is the difference between surviving a broker
                // restart and losing everything queued during one.
                message.NextAttemptAt = DateTime.UtcNow + BackoffFor(message.RetryCount);
                await repository.UpdateAsync(message, null, ct);

                _logger.LogError(ex,
                    "Failed to publish outbox message {Id}. Attempt {Count} of {Max}; next attempt at {NextAttemptAt:O}.",
                    message.Id, message.RetryCount, MaxAttempts, message.NextAttemptAt);
            }
        }
    }
}
