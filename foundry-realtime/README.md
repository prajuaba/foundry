# ⚡ Foundry.RealTime

`Foundry.RealTime` is a high-performance event-streaming and realtime communication broker built on C# (.NET 10). It provides pluggable channels (SignalR, raw WebSockets, and Server-Sent Events) and intercepts domain mutations automatically at the repository audit sink level to publish updates instantly to connected clients.

---

## 🗺️ Key Features

*   **🎙️ Multi-Channel Streaming**:
    *   **SignalR (`NotificationHub`)**: Standard connection hub supporting selective group-based subscriptions (`entity:[Name]` or `record:[Id]`) to restrict data traffic.
    *   **Server-Sent Events (SSE)**: Ultra lightweight HTTP text-stream (`text/event-stream`) client manager supporting automatic connection tracking and heartbeat keep-alives.
    *   **WebSockets**: Direct raw WebSockets connection broker implementing low-overhead JSON frame broadcasting.
*   **🛠️ Transparent Data Access Layer Interception**:
    *   Decorates the registered MongoDB data access layer `IAuditSink` (e.g. `ConsoleAuditSink`).
    *   Whenever an entity is inserted, updated, soft-deleted, or restored, the mutation details (operator, timestamp, diffs, and keys) are automatically intercepted and published in real-time without modifying any handlers or repository calls.
*   **🔒 Thread-Safe Write Locks**: Writes to client streams are coordinated using thread-safe semaphores and concurrency wrappers to prevent corruption under heavy server-side event bursts.

---

## 🛠️ Usage & Integration

### 1. DI Registration (`Program.cs`)
Register the real-time broker and connection managers in your dependency injection container:

```csharp
using Microsoft.Extensions.DependencyInjection;

// 1. Add MongoDB DAL
builder.Services.AddFoundry.Mongo(options => {
    options.ConnectionString = "mongodb://localhost:27017";
    options.DatabaseName = "AppDb";
});

// 2. Add RealTime Broker & Channels (automatically decorates IAuditSink)
builder.Services.AddFoundryRealTime();
```

### 2. Map Endpoints
Expose real-time transport routes inside the ASP.NET Core endpoint routing builder:

```csharp
var app = builder.Build();

app.UseWebSockets(); // Required for raw WebSocket connections

// Maps SignalR (/realtime/hub), SSE (/realtime/sse), and WebSockets (/realtime/ws)
app.MapFoundryRealTime();

app.Run();
```

---

## 📡 Client Subscription Protocols

### A. SignalR Hub Subscription
Clients connect to `/realtime/hub` and subscribe to targeted events using groups:

```javascript
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/realtime/hub")
    .build();

connection.on("OnEntityMutationReceived", (auditEntry) => {
    console.log(`Entity ${auditEntry.entityType} changed:`, auditEntry);
});

await connection.start();

// Join target subscription groups
await connection.invoke("SubscribeToEntity", "Invoice");
await connection.invoke("SubscribeToRecord", "64b1f48ed2c1a3b5c9000104");
```

### B. Server-Sent Events (SSE) Stream
Lightweight browsers or clients can connect using standard `EventSource`:

```javascript
const eventSource = new EventSource("/realtime/sse");

eventSource.addEventListener("mutation", (event) => {
    const auditEntry = JSON.parse(event.data);
    console.log("Real-time DB Mutation:", auditEntry);
});
```

### C. Raw WebSockets
Connect a client using the standard WebSocket protocol:

```javascript
const socket = new WebSocket("ws://localhost:5000/realtime/ws");

socket.onmessage = (event) => {
    const wsMessage = JSON.parse(event.data);
    if (wsMessage.type === "MutationEvent") {
        console.log("WebSocket Mutation:", wsMessage.payload);
    }
};
```
