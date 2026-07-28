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
        var messages = await repository.FindManyAsync(
            m => m.ProcessedAt == null && m.RetryCount < 5,
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
                await repository.UpdateAsync(message, null, ct);

                _logger.LogInformation("Successfully published outbox message {Id} of type {Type}.", message.Id, message.EventType);
            }
            catch (Exception ex)
            {
                message.RetryCount++;
                message.ErrorMessage = ex.Message;
                await repository.UpdateAsync(message, null, ct);

                _logger.LogError(ex, "Failed to publish outbox message {Id}. Retry attempt {Count}.", message.Id, message.RetryCount);
            }
        }
    }
}
