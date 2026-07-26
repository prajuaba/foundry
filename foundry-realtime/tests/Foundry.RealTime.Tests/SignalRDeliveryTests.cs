using Foundry.Core.Attributes;
using Foundry.Core.Audit;
using Foundry.RealTime.SignalR;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace Foundry.RealTime.Tests;

/// <summary>
/// Who a mutation is delivered to over SignalR.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="AuditLogEntry"/> carries <c>PropertyDiffs</c> — the actual old and new values of
/// every changed field. Delivering one to the wrong connection is a data disclosure, not a cosmetic
/// bug, and the send always succeeds so nothing reports it.
/// </para>
/// <para>
/// <see cref="NotificationHub"/> implements RBAC on subscription via <c>[RealTime(roles: ...)]</c>.
/// These tests exist to keep delivery consistent with that model: an authorisation check that the
/// delivery path bypasses is not an authorisation check.
/// </para>
/// </remarks>
public class SignalRDeliveryTests
{
    [RealTime(roles: ["Finance"])]
    private sealed class Invoice
    {
        public string Id { get; init; } = string.Empty;
    }

    // ---- fakes recording what would be sent where ----

    private sealed class RecordingClientProxy(string target, List<(string Target, string Method)> log)
        : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
        {
            log.Add((target, method));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHubClients(List<(string Target, string Method)> log) : IHubClients
    {
        public IClientProxy All => new RecordingClientProxy("ALL", log);
        public IClientProxy AllExcept(IReadOnlyList<string> excluded) => new RecordingClientProxy("ALL_EXCEPT", log);
        public IClientProxy Client(string connectionId) => new RecordingClientProxy($"client:{connectionId}", log);
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new RecordingClientProxy("clients", log);
        public IClientProxy Group(string groupName) => new RecordingClientProxy($"group:{groupName}", log);
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => new RecordingClientProxy("groups", log);
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excluded)
            => new RecordingClientProxy($"group:{groupName}", log);
        public IClientProxy User(string userId) => new RecordingClientProxy($"user:{userId}", log);
        public IClientProxy Users(IReadOnlyList<string> userIds) => new RecordingClientProxy("users", log);
    }

    private sealed class FakeHubContext(List<(string Target, string Method)> log) : IHubContext<NotificationHub>
    {
        public IHubClients Clients { get; } = new RecordingHubClients(log);
        public IGroupManager Groups { get; } = new NoOpGroupManager();
    }

    private sealed class NoOpGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static async Task<List<(string Target, string Method)>> Deliver(AuditLogEntry entry)
    {
        var log = new List<(string Target, string Method)>();
        await new SignalRNotificationService(new FakeHubContext(log)).SendMutationAsync(entry);
        return log;
    }

    private static AuditLogEntry InvoiceMutation() => AuditLogEntry.ForUpdate(
        "ada", typeof(Invoice).FullName!, "6a65ba09986eed749ed7e968", "Invoices",
        [new PropertyDiff("Total", 100m, 250m)]);

    // ---- the disclosure ----

    [Fact]
    public async Task AMutationIsNotBroadcastToEveryConnectedClient()
    {
        // Every mutation went to Clients.All in addition to the subscription groups, so any connected
        // client received every entity's changes regardless of the roles NotificationHub validates on
        // subscribe -- and, in a multi-tenant deployment, regardless of tenant. The audit entry
        // includes the changed values, so this disclosed data rather than just metadata.
        var log = await Deliver(InvoiceMutation());

        Assert.DoesNotContain(log, sent => sent.Target == "ALL");
    }

    [Fact]
    public async Task AMutationReachesItsEntitySubscriptionGroup()
    {
        var log = await Deliver(InvoiceMutation());
        Assert.Contains(log, sent => sent.Target.StartsWith("group:entity:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMutationReachesItsRecordSubscriptionGroup()
    {
        var log = await Deliver(InvoiceMutation());

        Assert.Contains(
            log,
            sent => sent.Target.Contains("6a65ba09986eed749ed7e968", StringComparison.Ordinal));
    }

    // ---- the group name has to match what subscribers join ----

    [Fact]
    public async Task TheEntityGroupUsesTheSameNameASubscriberWouldJoin()
    {
        // NotificationHub.SubscribeToEntity joins "entity:{name}" using the name the client supplied
        // -- "Invoice" -- while delivery used entry.EntityType, which is the full name
        // "Foundry.RealTime.Tests.SignalRDeliveryTests+Invoice". The two never matched, so entity
        // subscriptions received nothing at all. It went unnoticed because the Clients.All firehose
        // above delivered everything anyway.
        var log = await Deliver(InvoiceMutation());

        Assert.Contains(log, sent => sent.Target == $"group:entity:{nameof(Invoice)}");
    }

    [Fact]
    public async Task AMutationWithNoEntityId_SkipsTheRecordGroup()
    {
        var entry = AuditLogEntry.ForUpdate("ada", typeof(Invoice).FullName!, "", "Invoices", []);

        var log = await Deliver(entry);

        Assert.DoesNotContain(log, sent => sent.Target.StartsWith("group:record:", StringComparison.Ordinal));
    }
}
