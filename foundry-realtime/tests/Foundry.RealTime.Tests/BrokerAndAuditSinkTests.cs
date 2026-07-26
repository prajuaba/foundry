using Foundry.Core.Attributes;
using Foundry.Core.Audit;
using Foundry.RealTime;
using Foundry.RealTime.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Foundry.RealTime.Tests;

/// <summary>
/// Fan-out behaviour of <see cref="RealTimeNotificationBroker"/> and the audit-sink decorator.
/// </summary>
/// <remarks>
/// <see cref="RealTimeAuditSink"/> sits in front of the application's real audit sink. Anything that
/// makes it throw or skip stops audit records being written, so the decorator's failure behaviour
/// matters more than the broadcasting it adds.
/// </remarks>
public class BrokerAndAuditSinkTests
{
    [RealTime(false)]
    private sealed class QuietEntity;

    [RealTime(true)]
    private sealed class LoudEntity;

    private sealed class PlainEntity;

    private sealed class RecordingChannel(string name) : INotificationService
    {
        public string ChannelName => name;
        public List<AuditLogEntry> Sent { get; } = [];

        public Task SendMutationAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Sent.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingChannel : INotificationService
    {
        public string ChannelName => "Failing";
        public Task SendMutationAsync(AuditLogEntry entry, CancellationToken ct = default)
            => throw new InvalidOperationException("transport down");
    }

    private sealed class RecordingBroker : IRealTimeNotificationBroker
    {
        public List<AuditLogEntry> Broadcast { get; } = [];

        public Task BroadcastMutationAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Broadcast.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSink : IAuditSink
    {
        public List<AuditLogEntry> Written { get; } = [];

        public Task WriteAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Written.Add(entry);
            return Task.CompletedTask;
        }

        public Task WriteManyAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken ct = default)
        {
            Written.AddRange(entries);
            return Task.CompletedTask;
        }
    }

    private static AuditLogEntry EntryFor(Type entityType) =>
        AuditLogEntry.ForInsert("ada", entityType.FullName!, "abc", "Things");

    // ---- broker fan-out ----

    [Fact]
    public async Task EveryChannelReceivesTheMutation()
    {
        var a = new RecordingChannel("A");
        var b = new RecordingChannel("B");
        var broker = new RealTimeNotificationBroker([a, b], NullLogger<RealTimeNotificationBroker>.Instance);

        await broker.BroadcastMutationAsync(EntryFor(typeof(LoudEntity)));

        Assert.Single(a.Sent);
        Assert.Single(b.Sent);
    }

    [Fact]
    public async Task AFailingChannelDoesNotStopTheOthers()
    {
        // One dead transport must not suppress the rest. The broker logs and continues, which is the
        // right trade here -- delivery is best-effort, and the audit write is what must not be lost.
        var healthy = new RecordingChannel("Healthy");
        var broker = new RealTimeNotificationBroker(
            [new FailingChannel(), healthy], NullLogger<RealTimeNotificationBroker>.Instance);

        await broker.BroadcastMutationAsync(EntryFor(typeof(LoudEntity)));

        Assert.Single(healthy.Sent);
    }

    [Fact]
    public async Task NoChannelsRegistered_IsHarmless()
    {
        var broker = new RealTimeNotificationBroker([], NullLogger<RealTimeNotificationBroker>.Instance);
        await broker.BroadcastMutationAsync(EntryFor(typeof(LoudEntity)));
    }

    // ---- audit sink decoration ----

    [Fact]
    public async Task TheInnerSinkAlwaysReceivesTheEntry()
    {
        var inner = new RecordingSink();
        var sink = new RealTimeAuditSink(new RecordingBroker(), inner);

        await sink.WriteAsync(EntryFor(typeof(LoudEntity)));

        Assert.Single(inner.Written);
    }

    [Fact]
    public async Task TheInnerSinkReceivesEntriesEvenWhenBroadcastingIsDisabled()
    {
        // [RealTime(false)] turns off broadcasting, not auditing. Conflating the two would silently
        // stop writing audit records for that entity.
        var inner = new RecordingSink();
        var broker = new RecordingBroker();
        var sink = new RealTimeAuditSink(broker, inner);

        await sink.WriteAsync(EntryFor(typeof(QuietEntity)));

        Assert.Single(inner.Written);
        Assert.Empty(broker.Broadcast);
    }

    [Fact]
    public async Task BroadcastingHappensForAnEntityThatEnablesIt()
    {
        var broker = new RecordingBroker();
        await new RealTimeAuditSink(broker, new RecordingSink()).WriteAsync(EntryFor(typeof(LoudEntity)));

        Assert.Single(broker.Broadcast);
    }

    [Fact]
    public async Task AnEntityWithNoAttribute_Broadcasts()
    {
        // Documented default: real-time is on unless [RealTime(false)] says otherwise.
        var broker = new RecordingBroker();
        await new RealTimeAuditSink(broker, new RecordingSink()).WriteAsync(EntryFor(typeof(PlainEntity)));

        Assert.Single(broker.Broadcast);
    }

    [Fact]
    public async Task WithNoInnerSink_BroadcastingStillWorks()
    {
        var broker = new RecordingBroker();
        await new RealTimeAuditSink(broker, innerSink: null).WriteAsync(EntryFor(typeof(LoudEntity)));

        Assert.Single(broker.Broadcast);
    }

    [Fact]
    public async Task WriteMany_ForwardsEveryEntryToTheInnerSink()
    {
        var inner = new RecordingSink();
        var broker = new RecordingBroker();
        var sink = new RealTimeAuditSink(broker, inner);

        await sink.WriteManyAsync([EntryFor(typeof(LoudEntity)), EntryFor(typeof(QuietEntity))]);

        Assert.Equal(2, inner.Written.Count);
        Assert.Single(broker.Broadcast);
    }
}
