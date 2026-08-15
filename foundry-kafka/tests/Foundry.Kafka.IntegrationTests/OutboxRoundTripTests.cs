using System.Text;
using Confluent.Kafka;
using Foundry.Core.Outbox;
using Foundry.Mongo.Repositories;
using Foundry.Mongo.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Foundry.Kafka.IntegrationTests;

/// <summary>
/// The transactional outbox, from a mutation through to a message on a real Kafka topic.
/// </summary>
/// <remarks>
/// <para>
/// Every part of this chain had unit tests and the chain itself had never run. The outbox is the
/// framework's durability claim — a mutation is recorded in the same database as the data, and
/// published afterwards, so a broker outage cannot lose an event — and a claim of that shape is only
/// worth what an end-to-end run says it is.
/// </para>
/// <para>
/// This suite requires a real broker and a real database and <b>fails rather than skips</b> without
/// them, in the same way the MongoDB suites do. A gate that quietly skips its own subject reports
/// success for something it did not check, which is the defect class this repository is named for.
/// Tests are tagged <c>RequiresKafka</c> so the fast build job can exclude them by filter — a visible
/// split rather than a silent one.
/// </para>
/// </remarks>
[Trait("Category", "RequiresKafka")]
public sealed class OutboxRoundTripTests : IDisposable
{
    private const string BootstrapServers = "localhost:9092";
    private const string MongoConnectionString = "mongodb://localhost:27017";

    /// <summary>How long to wait for a message to travel the whole chain.</summary>
    private static readonly TimeSpan RoundTripTimeout = TimeSpan.FromSeconds(30);

    private readonly string _databaseName = $"FoundryOutbox_{Guid.NewGuid():N}";
    private readonly MongoClient _mongo = new(MongoConnectionString);
    private ServiceProvider? _services;

    /// <summary>An event shaped like the ones the outbox behaviour enqueues.</summary>
    public sealed record OrderPlaced(OrderPlaced.OrderBody Entity)
    {
        public sealed record OrderBody(string Id, string Reference);
    }

    /// <summary>The same, for a subject that names its own topic.</summary>
    [Foundry.Core.Attributes.KafkaTopic(DeclaredTopic)]
    public sealed record InvoiceIssued(InvoiceIssued.InvoiceBody Entity)
    {
        public sealed record InvoiceBody(string Id, string Reference);
    }

    /// <summary>Deliberately unlike anything the default naming would produce from the type name.</summary>
    private const string DeclaredTopic = "billing.invoices.v2";

    /// <summary>
    /// A subject carrying a real <see cref="ObjectId"/> and a real enum, rather than strings.
    /// </summary>
    /// <remarks>
    /// Every other fixture here declares its id as <c>string</c>, which is why this suite proved the
    /// chain end to end and still missed what was travelling down it: the outbox serialized payloads
    /// with stock System.Text.Json, so an ObjectId arrived as {"Timestamp":…,"CreationTime":…} --
    /// two members derived from the same four bytes, with the random and counter bytes absent, so
    /// the id could not be reconstructed by anyone consuming it.
    /// </remarks>
    public sealed record ShipmentDispatched(ShipmentDispatched.ShipmentBody Entity)
    {
        public sealed record ShipmentBody(ObjectId Id, ObjectId CarrierId, Carrier Mode, string Reference);
    }

    public enum Carrier
    {
        Ground = 0,
        Air = 1,
    }

    public OutboxRoundTripTests()
    {
        Foundry.Mongo.Infrastructure.Conventions.MongoDbConventions.Register();
    }

    public void Dispose()
    {
        _services?.Dispose();
        try { _mongo.DropDatabase(_databaseName); } catch { /* cleanup is best effort */ }
    }

    private ServiceProvider BuildHost()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        services.AddFoundryMongo(options =>
        {
            options.ConnectionString = MongoConnectionString;
            options.DatabaseName = _databaseName;
        });

        services.AddSingleton<Foundry.Core.Outbox.IOutboxDispatcher>(sp =>
            new Foundry.Kafka.Bridge.KafkaOutboxDispatcher(
                sp.GetRequiredService<Foundry.Kafka.Producer.IKafkaProducer>()));

        services.Configure<Foundry.Kafka.Configuration.KafkaOptions>(options =>
        {
            options.BootstrapServers = BootstrapServers;
            options.ClientId = "foundry-outbox-round-trip";
        });
        services.AddSingleton<Foundry.Kafka.Producer.IKafkaProducer, Foundry.Kafka.Producer.KafkaProducer>();

        _services = services.BuildServiceProvider();
        return _services;
    }

    /// <summary>
    /// Creates the topic, then subscribes before anything is published so no message can be missed.
    /// </summary>
    /// <remarks>
    /// The topic is created explicitly rather than left to the broker's auto-creation, which only
    /// happens on first produce -- a consumer that subscribes first would otherwise fail with
    /// "Unknown topic or partition" and make the test's timing, rather than the outbox, the thing
    /// under test.
    /// </remarks>
    private static IConsumer<string, string> Subscribe(string topic)
    {
        EnsureTopic(topic);

        var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = BootstrapServers,

            // A fresh group per run, reading from the beginning: the assertion is about what this
            // test published, not about whatever a previous run left on the topic.
            GroupId = $"outbox-round-trip-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();

        consumer.Subscribe(topic);
        return consumer;
    }

    private static void EnsureTopic(string topic)
    {
        using var admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();

        try
        {
            admin.CreateTopicsAsync([
                new Confluent.Kafka.Admin.TopicSpecification
                {
                    Name = topic, NumPartitions = 1, ReplicationFactor = 1
                }
            ]).GetAwaiter().GetResult();
        }
        catch (Confluent.Kafka.Admin.CreateTopicsException ex)
            when (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
        {
            // Left by an earlier test in this run. Nothing to do.
        }
    }

    /// <summary>
    /// Consumes until a message matching <paramref name="match"/> arrives, or the timeout expires.
    /// </summary>
    /// <remarks>
    /// Selective rather than "take the first message": these tests share one topic, so a test that
    /// asserted on whatever arrived first would pass or fail on the order xUnit happened to run
    /// them in. Each looks for the reference it published.
    /// </remarks>
    private static ConsumeResult<string, string>? ConsumeMatching(
        IConsumer<string, string> consumer, Func<ConsumeResult<string, string>, bool> match, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var result = consumer.Consume(TimeSpan.FromSeconds(1));
            if (result?.Message is not null && match(result)) return result;
        }

        return null;
    }

    /// <summary>
    /// Refuses to run against absent infrastructure rather than passing without exercising anything.
    /// </summary>
    private static void RequireInfrastructure()
    {
        AssertReachable("localhost", 27017, "MongoDB", "docker compose up -d mongodb");
        AssertReachable("localhost", 9092, "Kafka", "docker compose up -d kafka");
    }

    private static void AssertReachable(string host, int port, string what, string remedy)
    {
        try
        {
            using var probe = new System.Net.Sockets.TcpClient();
            if (probe.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(5)) && probe.Connected) return;
        }
        catch (Exception)
        {
            // Fall through to the assertion below, which carries the remedy.
        }

        Assert.Fail($"{what} is not listening on {host}:{port}. This suite exercises the real "
                    + $"outbox round trip and will not pass without it. Start it with: {remedy}");
    }

    // ── The round trip ──────────────────────────────────────────────────────

    [Fact]
    public async Task AnObjectIdSurvivesTheTripToATopic()
    {
        // The encoding, asserted where it actually matters: on a real broker, at the far end of the
        // real worker loop. The unit test pins MongoOutboxQueue's output; this proves nothing between
        // there and the topic re-encodes it.
        RequireInfrastructure();

        using var consumer = Subscribe("shipment-dispatched-events");

        var services = BuildHost();
        var reference = $"SHP-{Guid.NewGuid():N}";
        var entityId = ObjectId.GenerateNewId();
        var carrierId = ObjectId.GenerateNewId();

        using (var scope = services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IOutboxQueue>();
            await queue.EnqueueAsync(
                new ShipmentDispatched(
                    new ShipmentDispatched.ShipmentBody(entityId, carrierId, Carrier.Air, reference)),
                CancellationToken.None);
        }

        var worker = new OutboxPublisherWorker(services, NullLogger<OutboxPublisherWorker>.Instance);
        using var cts = new CancellationTokenSource(RoundTripTimeout);
        await worker.StartAsync(cts.Token);

        try
        {
            var message = ConsumeMatching(
                consumer, r => r.Message.Value.Contains(reference), RoundTripTimeout);

            Assert.NotNull(message);
            var value = message!.Message.Value;

            // Both ids readable as ids, not as the struct's innards.
            Assert.Contains(entityId.ToString(), value, StringComparison.Ordinal);
            Assert.Contains(carrierId.ToString(), value, StringComparison.Ordinal);
            Assert.DoesNotContain("CreationTime", value, StringComparison.Ordinal);

            // And the enum by name, so the meaning does not depend on declaration order.
            Assert.Contains("\"Air\"", value, StringComparison.Ordinal);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AnEnqueuedEventReachesKafka()
    {
        RequireInfrastructure();

        // "OrderPlaced" becomes the topic "order-placed-events".
        using var consumer = Subscribe("order-placed-events");

        var services = BuildHost();
        var reference = $"REF-{Guid.NewGuid():N}";
        var entityId = ObjectId.GenerateNewId().ToString();

        using (var scope = services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IOutboxQueue>();
            await queue.EnqueueAsync(
                new OrderPlaced(new OrderPlaced.OrderBody(entityId, reference)), CancellationToken.None);
        }

        // The worker's own loop, not a reimplementation of it: this is what a running application
        // relies on, so it is what the test drives.
        var worker = new OutboxPublisherWorker(services, NullLogger<OutboxPublisherWorker>.Instance);
        using var cts = new CancellationTokenSource(RoundTripTimeout);
        await worker.StartAsync(cts.Token);

        try
        {
            var message = ConsumeMatching(
                consumer, r => r.Message.Value.Contains(reference), RoundTripTimeout);

            Assert.NotNull(message);
            Assert.Contains(entityId, message!.Message.Value);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AnEventWhoseSubjectNamesATopicArrivesOnThatTopic()
    {
        RequireInfrastructure();

        // The claim that could not be checked without a broker. A schema declaring kafkaTopic
        // configured the generated consumer and nothing else: the publisher derived its own name
        // from the event type, so the consumer subscribed to a topic nothing was written to and both
        // halves reported success. Subscribing to the declared topic and nothing else is what makes
        // this fail if the declaration is dropped anywhere along the way.
        using var declared = Subscribe(DeclaredTopic);
        using var derived = Subscribe("invoice-issued-events");

        var services = BuildHost();
        var reference = $"INV-{Guid.NewGuid():N}";
        var entityId = ObjectId.GenerateNewId().ToString();

        using (var scope = services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IOutboxQueue>();
            await queue.EnqueueAsync(
                new InvoiceIssued(new InvoiceIssued.InvoiceBody(entityId, reference)), CancellationToken.None);
        }

        var worker = new OutboxPublisherWorker(services, NullLogger<OutboxPublisherWorker>.Instance);
        using var cts = new CancellationTokenSource(RoundTripTimeout);
        await worker.StartAsync(cts.Token);

        try
        {
            var message = ConsumeMatching(
                declared, r => r.Message.Value.Contains(reference), RoundTripTimeout);

            Assert.NotNull(message);
            Assert.Equal(DeclaredTopic, message!.Topic);

            // And nothing went to the name the default rule would have produced, which is where every
            // such message used to land.
            Assert.Null(ConsumeMatching(
                derived, r => r.Message.Value.Contains(reference), TimeSpan.FromSeconds(3)));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ThePartitionKeyIsTheEntityId()
    {
        // Kafka guarantees ordering only within a partition, and the partition is chosen by key.
        // Keying by entity id is what keeps one record's mutations in order; a random key per
        // message would let a consumer apply an update before the insert it depends on, with every
        // publish still reporting success.
        RequireInfrastructure();

        using var consumer = Subscribe("order-placed-events");

        var services = BuildHost();
        var entityId = ObjectId.GenerateNewId().ToString();
        var reference = $"KEY-{Guid.NewGuid():N}";

        using (var scope = services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IOutboxQueue>()
                .EnqueueAsync(new OrderPlaced(new OrderPlaced.OrderBody(entityId, reference)), CancellationToken.None);
        }

        var worker = new OutboxPublisherWorker(services, NullLogger<OutboxPublisherWorker>.Instance);
        using var cts = new CancellationTokenSource(RoundTripTimeout);
        await worker.StartAsync(cts.Token);

        try
        {
            var mine = ConsumeMatching(
                consumer, r => r.Message.Value.Contains(reference), RoundTripTimeout);

            Assert.NotNull(mine);
            Assert.Equal(entityId, mine!.Message.Key);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CorrelationContextTravelsInTheHeaders()
    {
        // The reason the outbox records it at all: a consumer several services away should be able
        // to tie a message back to the request that caused it.
        RequireInfrastructure();

        using var consumer = Subscribe("order-placed-events");

        var services = BuildHost();
        var reference = $"HDR-{Guid.NewGuid():N}";

        using (var scope = services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IOutboxQueue>()
                .EnqueueAsync(
                    new OrderPlaced(new OrderPlaced.OrderBody(ObjectId.GenerateNewId().ToString(), reference)),
                    CancellationToken.None);
        }

        var worker = new OutboxPublisherWorker(services, NullLogger<OutboxPublisherWorker>.Instance);
        using var cts = new CancellationTokenSource(RoundTripTimeout);
        await worker.StartAsync(cts.Token);

        try
        {
            var mine = ConsumeMatching(
                consumer, r => r.Message.Value.Contains(reference), RoundTripTimeout);

            Assert.NotNull(mine);

            var header = Assert.Single(
                mine!.Message.Headers.Where(h => h.Key == "X-Correlation-Id"));
            Assert.False(string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(header.GetValueBytes())));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // ── Durability ──────────────────────────────────────────────────────────

    [Fact]
    public async Task APublishedMessageIsMarkedProcessedAndNotSentTwice()
    {
        // The outbox's other half. A message that publishes and is never marked would be republished
        // on every poll, turning an at-least-once guarantee into an unbounded loop.
        RequireInfrastructure();

        var services = BuildHost();
        var reference = $"ONCE-{Guid.NewGuid():N}";

        using (var scope = services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IOutboxQueue>()
                .EnqueueAsync(
                    new OrderPlaced(new OrderPlaced.OrderBody(ObjectId.GenerateNewId().ToString(), reference)),
                    CancellationToken.None);
        }

        var worker = new OutboxPublisherWorker(services, NullLogger<OutboxPublisherWorker>.Instance);
        using var cts = new CancellationTokenSource(RoundTripTimeout);
        await worker.StartAsync(cts.Token);

        try
        {
            using var scope = services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<OutboxMessage>>();

            var deadline = DateTime.UtcNow + RoundTripTimeout;
            OutboxMessage? stored = null;

            while (DateTime.UtcNow < deadline)
            {
                var rows = await repository.FindManyAsync();
                stored = rows.FirstOrDefault(m => m.Payload.Contains(reference, StringComparison.Ordinal));
                if (stored?.ProcessedAt is not null) break;
                await Task.Delay(500);
            }

            Assert.NotNull(stored);
            Assert.NotNull(stored!.ProcessedAt);
            Assert.Null(stored.ErrorMessage);
            Assert.Equal(0, stored.RetryCount);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TheMessageIsDurableBeforeItIsPublished()
    {
        // This is the whole point of an outbox: the row exists in the same database as the data, so
        // the event survives a process that dies before the broker ever hears about it.
        RequireInfrastructure();

        var services = BuildHost();
        var reference = $"DURABLE-{Guid.NewGuid():N}";

        using (var scope = services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IOutboxQueue>()
                .EnqueueAsync(
                    new OrderPlaced(new OrderPlaced.OrderBody(ObjectId.GenerateNewId().ToString(), reference)),
                    CancellationToken.None);
        }

        // No worker has run. Read the row back through a different provider entirely.
        var raw = _mongo.GetDatabase(_databaseName)
            .GetCollection<MongoDB.Bson.BsonDocument>("OutboxMessages");

        var document = await raw.Find(Builders<BsonDocument>.Filter.Empty).FirstOrDefaultAsync();

        Assert.NotNull(document);
        Assert.Contains(reference, document.ToString());
        Assert.True(document.GetValue("processedAt", BsonNull.Value).IsBsonNull);
    }
}
