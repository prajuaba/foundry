using System.Net.WebSockets;
using Foundry.Core.Audit;
using Foundry.RealTime;
using Foundry.RealTime.SSE;
using Foundry.RealTime.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Foundry.RealTime.Tests;

/// <summary>
/// WebSocket connection bookkeeping and container wiring.
/// </summary>
public class WebSocketAndRegistrationTests
{
    /// <summary>A WebSocket that can be told how to behave, since a real one needs a live peer.</summary>
    private sealed class FakeWebSocket(WebSocketState state = WebSocketState.Open, bool failOnSend = false)
        : WebSocket
    {
        private WebSocketState _state = state;

        public int SendCount { get; private set; }
        public bool Disposed { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct)
            => Task.CompletedTask;

        public override void Dispose() => Disposed = true;

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken ct)
            => throw new NotSupportedException();

        public override Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken ct)
        {
            if (failOnSend) throw new WebSocketException("peer went away");
            SendCount++;
            return Task.CompletedTask;
        }
    }

    private static WebSocketConnectionManager Manager()
        => new(NullLogger<WebSocketConnectionManager>.Instance);

    /// <summary>An entity this process can resolve, declaring no real-time roles.</summary>
    /// <remarks>
    /// It used to name "MyApp.Domain.Thing", a type nothing declares. That was harmless while every
    /// socket received every message regardless; now that delivery is filtered by the entity's
    /// declared roles, an unresolvable name fails closed — so the fixture has to name something real
    /// or it tests the refusal path while claiming to test forwarding.
    /// </remarks>
    public sealed record ForwardedThing;

    private static AuditLogEntry Entry() =>
        AuditLogEntry.ForInsert("ada", typeof(ForwardedThing).FullName!, "abc", "Things");

    // ---- bookkeeping ----

    [Fact]
    public void AddSocket_ReturnsDistinctIds()
    {
        var manager = Manager();

        var first = manager.AddSocket(new FakeWebSocket());
        var second = manager.AddSocket(new FakeWebSocket());

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task RemoveSocket_ClosesAndDisposesIt()
    {
        var manager = Manager();
        var socket = new FakeWebSocket();
        var id = manager.AddSocket(socket);

        await manager.RemoveSocketAsync(id, "test");

        Assert.True(socket.Disposed);
        Assert.Empty(manager.GetAllSockets());
    }

    [Fact]
    public async Task RemovingAnUnknownSocket_IsHarmless()
    {
        await Manager().RemoveSocketAsync("no-such-id", "test");
    }

    // ---- broadcasting ----

    [Fact]
    public async Task BroadcastReachesEveryOpenSocket()
    {
        var manager = Manager();
        var a = new FakeWebSocket();
        var b = new FakeWebSocket();
        manager.AddSocket(a);
        manager.AddSocket(b);

        await manager.BroadcastMessageAsync(new { Hello = "world" });

        Assert.Equal(1, a.SendCount);
        Assert.Equal(1, b.SendCount);
    }

    [Fact]
    public async Task ClosedSocketsArePrunedDuringBroadcast()
    {
        var manager = Manager();
        manager.AddSocket(new FakeWebSocket(WebSocketState.Closed));
        var healthy = new FakeWebSocket();
        manager.AddSocket(healthy);

        await manager.BroadcastMessageAsync(new { Hello = "world" });

        Assert.Single(manager.GetAllSockets());
        Assert.Equal(1, healthy.SendCount);
    }

    [Fact]
    public async Task ASocketThatFailsMidSendDoesNotAbortTheBroadcast()
    {
        // A socket can report Open and still fail on write -- the peer may have vanished between the
        // state check and the send. Without per-socket isolation the exception escapes Task.WhenAll,
        // and one dead client suppresses the broadcast for every other client on this transport. The
        // SSE channel already isolates per client; this one did not.
        var manager = Manager();
        manager.AddSocket(new FakeWebSocket(failOnSend: true));
        var healthy = new FakeWebSocket();
        manager.AddSocket(healthy);

        await manager.BroadcastMessageAsync(new { Hello = "world" });

        Assert.Equal(1, healthy.SendCount);
    }

    [Fact]
    public async Task ASocketThatFailsMidSendIsRemoved()
    {
        var manager = Manager();
        manager.AddSocket(new FakeWebSocket(failOnSend: true));

        await manager.BroadcastMessageAsync(new { Hello = "world" });

        Assert.Empty(manager.GetAllSockets());
    }

    [Fact]
    public async Task TheNotificationServiceForwardsThroughTheManager()
    {
        var manager = Manager();
        var socket = new FakeWebSocket();
        manager.AddSocket(socket);

        await new WebSocketNotificationService(manager).SendMutationAsync(Entry());

        Assert.Equal(1, socket.SendCount);
    }

    // ---- container wiring ----

    /// <summary>
    /// Stands in for the host-provided lifetime that SignalR's internals depend on.
    /// </summary>
    /// <remarks>
    /// AddSignalR registers HttpConnectionManager, which requires IHostApplicationLifetime. A real
    /// web host always supplies it; a bare ServiceCollection does not, so it is registered here
    /// rather than treated as a defect in AddFoundryRealTime.
    /// </remarks>
    private sealed class StubHostLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static ServiceProvider BuildContainer(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostApplicationLifetime, StubHostLifetime>();
        configure?.Invoke(services);
        services.AddFoundryRealTime();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    [Fact]
    public void AddFoundryRealTime_BuildsAValidContainer()
    {
        // The regression guard for the defect that killed startup in every application calling this:
        // RealTimeAuditSink was registered as IAuditSink while taking IAuditSink as a constructor
        // parameter, so resolving it reported a circular dependency.
        using var provider = BuildContainer();
        Assert.NotNull(provider);
    }

    [Fact]
    public void TheAuditSinkResolves()
    {
        using var provider = BuildContainer();
        Assert.IsType<Foundry.RealTime.Pipeline.RealTimeAuditSink>(
            provider.GetRequiredService<IAuditSink>());
    }

    [Fact]
    public void AnExistingAuditSinkIsDecoratedRatherThanReplaced()
    {
        // The application's own sink must keep receiving entries; real-time wraps it.
        using var provider = BuildContainer(services => services.AddSingleton<IAuditSink, CountingSink>());

        Assert.IsType<Foundry.RealTime.Pipeline.RealTimeAuditSink>(
            provider.GetRequiredService<IAuditSink>());
    }

    private sealed class CountingSink : IAuditSink
    {
        public Task WriteAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteManyAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    [Fact]
    public void TheBrokerAndItsChannelsResolve()
    {
        using var provider = BuildContainer();

        Assert.NotNull(provider.GetRequiredService<IRealTimeNotificationBroker>());
        Assert.NotEmpty(provider.GetServices<INotificationService>());
        Assert.NotNull(provider.GetRequiredService<SseNotificationService>());
        Assert.NotNull(provider.GetRequiredService<WebSocketConnectionManager>());
    }
}
