using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using Foundry.Core.Attributes;
using Foundry.Core.Audit;
using Foundry.RealTime;
using Foundry.RealTime.SSE;
using Foundry.RealTime.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Foundry.RealTime.Tests;

/// <summary>
/// <c>realTimeRoles</c> is enforced on every transport, not just the one that had the check.
/// </summary>
/// <remarks>
/// <para>
/// A schema says who may watch an entity's events, and the framework ships three ways to watch:
/// SignalR, Server-Sent Events and raw WebSockets. <c>NotificationHub</c> checked the roles when a
/// client subscribed. SSE and WebSockets had no subscriptions and no principal, and their
/// <c>SendMutationAsync</c> handed every mutation to every connected client — the same firehose that
/// was removed from SignalR precisely because it bypassed this check.
/// </para>
/// <para>
/// An <c>AuditLogEntry</c> carries <c>PropertyDiffs</c>, the before and after values. So any
/// authenticated caller could read the contents of every write to every entity, whatever roles the
/// schema demanded, by connecting to one of the other two URLs. These tests cover both, and the
/// decision they share.
/// </para>
/// </remarks>
public class RealTimeRoleGatingTests
{
    // ── Entities under test, in this assembly so the policy can resolve them ──

    /// <summary>Declares no roles: any authenticated caller may watch it.</summary>
    public sealed record OpenLedger;

    /// <summary>Only the warehouse may watch it.</summary>
    [RealTime(true, new[] { "Warehouse" })]
    public sealed record StockItem;

    /// <summary>Real-time is switched off entirely.</summary>
    [RealTime(false)]
    public sealed record QuietRecord;

    private static ClaimsPrincipal User(params string[] roles)
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "ada"), .. roles.Select(r => new Claim(ClaimTypes.Role, r))],
            authenticationType: "test"));

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static AuditLogEntry EntryFor(Type entity)
        => AuditLogEntry.ForInsert("ada", entity.FullName!, "abc", entity.Name + "s");

    // ── The shared decision ─────────────────────────────────────────────────

    [Fact]
    public void AnEntityDeclaringNoRolesIsOpenToAnyAuthenticatedCaller()
    {
        Assert.True(RealTimeAccessPolicy.MayObserve(User(), typeof(OpenLedger).FullName!));
    }

    [Fact]
    public void AnEntityDeclaringRolesRequiresOneOfThem()
    {
        Assert.False(RealTimeAccessPolicy.MayObserve(User("Sales"), typeof(StockItem).FullName!));
        Assert.True(RealTimeAccessPolicy.MayObserve(User("Warehouse"), typeof(StockItem).FullName!));
    }

    [Fact]
    public void AnAnonymousCallerCannotObserveARoleRestrictedEntity()
    {
        // The channels require authentication, so this should be unreachable. Asserted anyway: it is
        // the case where an empty role list on an unauthenticated principal reads as "no roles
        // required" if the check is written the obvious way round.
        Assert.False(RealTimeAccessPolicy.MayObserve(Anonymous(), typeof(StockItem).FullName!));
        Assert.False(RealTimeAccessPolicy.MayObserve(null, typeof(StockItem).FullName!));
    }

    [Fact]
    public void AnEntityWithRealTimeDisabledIsObservableByNobody()
    {
        Assert.False(RealTimeAccessPolicy.MayObserve(User("Warehouse"), typeof(QuietRecord).FullName!));
    }

    [Fact]
    public void AnUnresolvableEntityIsRefused()
    {
        // Fails closed: a type this process cannot see has access rules nobody can read, and
        // treating "no attribute found" as "no restriction" is how an unknown entity came to be
        // authorised in the first place.
        Assert.False(RealTimeAccessPolicy.MayObserve(User("Warehouse"), "Nowhere.Domain.Ghost"));
        Assert.False(RealTimeAccessPolicy.IsKnownEntity("Nowhere.Domain.Ghost"));
    }

    [Fact]
    public void TheSimpleNameAndTheFullNameResolveToTheSameDecision()
    {
        // An audit entry carries the full name; a subscriber names the short one. If the two
        // resolved differently, the transport that used one would enforce and the other would not.
        Assert.True(RealTimeAccessPolicy.MayObserve(User("Warehouse"), nameof(StockItem)));
        Assert.True(RealTimeAccessPolicy.MayObserve(User("Warehouse"), typeof(StockItem).FullName!));
        Assert.False(RealTimeAccessPolicy.MayObserve(User("Sales"), nameof(StockItem)));
    }

    // ── Server-Sent Events ──────────────────────────────────────────────────

    /// <summary>Captures what was written to the response body, since SSE writes text to a stream.</summary>
    private static (HttpResponse Response, MemoryStream Body) Sse()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        return (context.Response, body);
    }

    private static SseNotificationService SseService()
        => new(NullLogger<SseNotificationService>.Instance);

    [Fact]
    public async Task SseWithholdsAnEntityTheCallerMayNotSee()
    {
        var service = SseService();
        var (response, body) = Sse();
        service.RegisterClient(response, User("Sales"));

        await service.SendMutationAsync(EntryFor(typeof(StockItem)));

        Assert.DoesNotContain("StockItem", Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task SseDeliversToACallerHoldingTheRole()
    {
        var service = SseService();
        var (response, body) = Sse();
        service.RegisterClient(response, User("Warehouse"));

        await service.SendMutationAsync(EntryFor(typeof(StockItem)));

        Assert.Contains("StockItem", Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task SseDeliversAnUnrestrictedEntityToEveryone()
    {
        // The control. Without it, the test above passes just as well against a channel that has
        // stopped delivering anything at all.
        var service = SseService();
        var (response, body) = Sse();
        service.RegisterClient(response, User("Sales"));

        await service.SendMutationAsync(EntryFor(typeof(OpenLedger)));

        Assert.Contains("OpenLedger", Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task SseSendsOneEntityToOneClientAndNotTheOther()
    {
        // Two clients on one channel, one entitled and one not, from a single broadcast: the shape
        // the old code could not express at all, because it had no idea who either of them was.
        var service = SseService();
        var (entitled, entitledBody) = Sse();
        var (outsider, outsiderBody) = Sse();

        service.RegisterClient(entitled, User("Warehouse"));
        service.RegisterClient(outsider, User("Sales"));

        await service.SendMutationAsync(EntryFor(typeof(StockItem)));

        Assert.Contains("StockItem", Encoding.UTF8.GetString(entitledBody.ToArray()));
        Assert.DoesNotContain("StockItem", Encoding.UTF8.GetString(outsiderBody.ToArray()));
    }

    // ── Raw WebSockets ──────────────────────────────────────────────────────

    private sealed class CountingSocket : WebSocket
    {
        public int SendCount { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override void Dispose() { }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
            => throw new NotSupportedException();

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            SendCount++;
            return Task.CompletedTask;
        }
    }

    private static WebSocketConnectionManager Manager()
        => new(NullLogger<WebSocketConnectionManager>.Instance);

    [Fact]
    public async Task WebSocketsWithholdAnEntityTheCallerMayNotSee()
    {
        var manager = Manager();
        var socket = new CountingSocket();
        manager.AddSocket(socket, User("Sales"));

        await new WebSocketNotificationService(manager).SendMutationAsync(EntryFor(typeof(StockItem)));

        Assert.Equal(0, socket.SendCount);
    }

    [Fact]
    public async Task WebSocketsDeliverToACallerHoldingTheRole()
    {
        var manager = Manager();
        var socket = new CountingSocket();
        manager.AddSocket(socket, User("Warehouse"));

        await new WebSocketNotificationService(manager).SendMutationAsync(EntryFor(typeof(StockItem)));

        Assert.Equal(1, socket.SendCount);
    }

    [Fact]
    public async Task WebSocketsSendOneEntityToOneSocketAndNotTheOther()
    {
        var manager = Manager();
        var entitled = new CountingSocket();
        var outsider = new CountingSocket();
        manager.AddSocket(entitled, User("Warehouse"));
        manager.AddSocket(outsider, User("Sales"));

        await new WebSocketNotificationService(manager).SendMutationAsync(EntryFor(typeof(StockItem)));

        Assert.Equal(1, entitled.SendCount);
        Assert.Equal(0, outsider.SendCount);
    }

    [Fact]
    public async Task AWebSocketMessageThatNamesNoEntityStillReachesEveryone()
    {
        // The handshake frame is not about an entity and has no access rule to apply. Filtering it
        // by an entity nobody named would silence connection setup for every client.
        var manager = Manager();
        var socket = new CountingSocket();
        manager.AddSocket(socket, User("Sales"));

        await manager.BroadcastMessageAsync(new { Type = "Connected" });

        Assert.Equal(1, socket.SendCount);
    }
}
