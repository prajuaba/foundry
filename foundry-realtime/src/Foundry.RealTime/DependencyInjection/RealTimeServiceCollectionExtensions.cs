using System;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Foundry.Core.Audit;
using Foundry.RealTime;
using Foundry.RealTime.Pipeline;
using Foundry.RealTime.SignalR;
using Foundry.RealTime.SSE;
using Foundry.RealTime.WebSockets;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service collection extension methods to register Foundry.RealTime services.
/// </summary>
public static class RealTimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers all Foundry real-time notification brokers, channels, managers, and mutation decorators.
    /// </summary>
    public static IServiceCollection AddFoundryRealTime(this IServiceCollection services, string? redisConnectionString = null)
    {
        // Register SignalR backend prerequisites
        var signalRBuilder = services.AddSignalR();
        if (!string.IsNullOrWhiteSpace(redisConnectionString))
        {
            // Redis backplane for multi-node SignalR horizontal scaling
            signalRBuilder.AddStackExchangeRedis(redisConnectionString);
        }

        // Register WebSockets Manager
        services.AddSingleton<WebSocketConnectionManager>();

        // Register SSE Notification Service
        services.AddSingleton<SseNotificationService>();

        // Register individual channel notification services
        services.AddSingleton<INotificationService, SignalRNotificationService>();
        services.AddSingleton<INotificationService, WebSocketNotificationService>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<SseNotificationService>());

        // Register unified realtime broker
        services.AddSingleton<IRealTimeNotificationBroker, RealTimeNotificationBroker>();

        // Decorate the existing IAuditSink to transparently intercept mutations
        var auditSinkDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IAuditSink));
        if (auditSinkDescriptor != null)
        {
            var originalFactory = auditSinkDescriptor.ImplementationFactory;
            var originalInstance = auditSinkDescriptor.ImplementationInstance;
            var originalType = auditSinkDescriptor.ImplementationType;

            services.Remove(auditSinkDescriptor);

            services.AddSingleton<IAuditSink>(sp =>
            {
                IAuditSink? innerSink = null;
                if (originalInstance != null)
                {
                    innerSink = (IAuditSink)originalInstance;
                }
                else if (originalFactory != null)
                {
                    innerSink = (IAuditSink)originalFactory(sp);
                }
                else if (originalType != null)
                {
                    innerSink = (IAuditSink)ActivatorUtilities.GetServiceOrCreateInstance(sp, originalType);
                }

                var broker = sp.GetRequiredService<IRealTimeNotificationBroker>();
                return new RealTimeAuditSink(broker, innerSink);
            });
        }
        else
        {
            // No sink to decorate, so construct it with no inner sink explicitly.
            //
            // This was AddSingleton<IAuditSink, RealTimeAuditSink>(), which looks equivalent but is
            // not: RealTimeAuditSink's second constructor parameter is IAuditSink, and the container
            // tries to satisfy it from the registration being defined rather than using the
            // parameter's default. Every application that called AddFoundryRealTime() without
            // having registered an IAuditSink first therefore failed at startup with "A circular
            // dependency was detected for the service of type 'Foundry.Core.Audit.IAuditSink'" --
            // including the project produced by 'foundry new'.
            services.AddSingleton<IAuditSink>(sp =>
                new RealTimeAuditSink(sp.GetRequiredService<IRealTimeNotificationBroker>(), innerSink: null));
        }

        return services;
    }

    /// <summary>
    /// Maps real-time communications endpoints: SignalR hub, WebSockets accept pipeline, and Server-Sent Events (SSE) route.
    /// </summary>
    public static IEndpointRouteBuilder MapFoundryRealTime(this IEndpointRouteBuilder endpoints)
    {
        // 1. Map SignalR Hub
        endpoints.MapHub<NotificationHub>("/realtime/hub");

        // 2. Map Server-Sent Events Endpoint
        endpoints.MapGet("/realtime/sse", async (HttpContext context, SseNotificationService sseService, CancellationToken ct) =>
        {
            var response = context.Response;
            var client = sseService.RegisterClient(response);

            // Keep the connection open indefinitely until client aborts
            try
            {
                // Send an initial handshake verification
                await client.SendEventAsync("connected", new { ConnectionId = client.Id }, ct);

                while (!context.RequestAborted.IsCancellationRequested)
                {
                    // Heartbeat ping every 15 seconds to prevent intermediate proxy timeouts
                    await Task.Delay(TimeSpan.FromSeconds(15), context.RequestAborted);
                    await client.SendEventAsync("ping", new { Timestamp = DateTime.UtcNow }, context.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation when client disconnects
            }
            finally
            {
                sseService.UnregisterClient(client.Id);
            }
        });

        // 3. Map WebSockets Middleware Endpoint
        endpoints.MapGet("/realtime/ws", async (HttpContext context, WebSocketConnectionManager connectionManager) =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                string id = connectionManager.AddSocket(webSocket);
                var logger = context.RequestServices.GetRequiredService<ILogger<NotificationHub>>();

                try
                {
                    // Send connection handshake frame
                    await connectionManager.SendMessageAsync(webSocket, new { Type = "Connected", ConnectionId = id });

                    var buffer = new byte[1024 * 4];
                    while (webSocket.State == WebSocketState.Open)
                    {
                        // Active receive loop to capture client close frames or keep-alives
                        var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await connectionManager.RemoveSocketAsync(id, "Client closed connection");
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "WebSocket connection error on socket: {Id}", id);
                    await connectionManager.RemoveSocketAsync(id, $"Error: {ex.Message}");
                }
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Not a WebSocket request.");
            }
        });

        return endpoints;
    }
}
