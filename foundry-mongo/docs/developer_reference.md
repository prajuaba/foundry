# FoundryMongo: Developer Reference Manual

`FoundryMongo` is a high-performance, commercial-grade, lightweight Data Access Layer (DAL) wrapper built on top of the native MongoDB C# Driver (v3.x). It extends MongoDB with robust relational-like features (transactions, auditing, versioning, optimistic locking, transparent encryption/masking) while maintaining MongoDB's high throughput.

---

## 1. Setup & DI Initialization

To integrate `FoundryMongo` into your ASP.NET Core application, use the fluent DI extension method in `Program.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

builder.Services.AddFoundryMongo(options =>
{
    // 1. Connection string & database binding
    options.ConnectionString = "mongodb://localhost:27017";
    options.DatabaseName = "CommercialAppDb";

    // 2. Base64-encoded 256-bit (32 bytes) symmetric key for AES encryption at rest
    options.EncryptionKey = "base64-encoded-key-here...";

    // 3. Performance tuning: Enable transparent cache-aside decorator
    options.EnableCaching = true; 
    options.DefaultCacheTtl = TimeSpan.FromMinutes(5);
});

// Register ASP.NET Core Health Checks
builder.Services.AddHealthChecks()
    .AddCheck<MongoDbHealthCheck>("DatabaseHealth");
```

> [!NOTE]
> DI registration automatically registers camelCase element conventions, Enum-to-String storage, ignores extra element mappings on read, and registers a standard `GuidSerializer` in the BSON engine.

---

## 2. Defining Entities

All database entities must inherit from `BaseEntity<TId>` (usually `BaseEntity<ObjectId>`).

```csharp
using MongoDB.Bson;
using FoundryMongo.Domain.Entities;
using FoundryMongo.Domain.Filters;

public record Customer : BaseEntity<ObjectId>, IVersionable, ISoftDelete
{
    [Indexed(Unique = true)]
    public string Email { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    // Soft delete status fields (init-only, required for ISoftDelete)
    public bool IsDeleted { get; init; }
    public DateTime? DeletedAt { get; init; }
}
```

### Marker Interfaces Reference
| Interface | Behavior |
| :--- | :--- |
| `ISoftDelete` | Intercepts deletes to set `IsDeleted = true`. Automatically filters deleted documents from all read/query APIs. |
| `IVersionable` | Automatically saves a copy of every insert, update, soft-delete, and restore action into a shadow `{CollectionName}_History` collection. |

---

## 3. Core CRUD & Transactions

Inject `IRepository<T>` directly into your services or controllers.

```csharp
public class OrderService
{
    private readonly IRepository<Order> _orderRepo;
    private readonly IMongoClient _mongoClient; // Injected for transactions

    public OrderService(IRepository<Order> orderRepo, IMongoClient mongoClient)
    {
        _orderRepo = orderRepo;
        _mongoClient = mongoClient;
    }

    public async Task ProcessOrderAsync(Order order)
    {
        // 1. Single write
        await _orderRepo.InsertAsync(order);

        // 2. ACID Transaction across multiple writes
        using var session = await _mongoClient.StartSessionAsync();
        session.StartTransaction();
        try
        {
            await _orderRepo.InsertAsync(order, session);
            // ... perform other writes sharing the session context
            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    }
}
```

---

## 4. Optimistic Concurrency Control (OCC)

Every document mutation (`UpdateAsync`, `UpdateByObjectIdAsync`, `BulkUpdateAsync`, `BulkUpdateManyAsync`) compares the document's `Version` property in the database before completing replacement.

```csharp
try
{
    // Try to update order assuming version is 2
    await orderRepo.UpdateByObjectIdAsync(orderId, order =>
    {
        order.Status = "Completed";
        order.Version = 2; // Expected version in database
        return order;
    }, operatorId: "user-123");
}
catch (ConcurrencyException ex)
{
    // Thrown if the version in the database is not 2 (modified by another client)
    logger.LogWarning($"Lock conflict on document {ex.EntityId}: {ex.Message}");
}
```

---

## 5. Audit Diff Logging

When mutations occur, the repository calculates property-level deltas and pushes them to the registered `IAuditSink`.

### Custom Audit Sink Implementation
```csharp
public class MyAuditSink : IAuditSink
{
    public Task WriteAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        Console.WriteLine($"Action: {entry.Action} by {entry.OperatorId}");
        foreach (var diff in entry.PropertyDiffs)
        {
            Console.WriteLine($"  * {diff.PropertyName}: '{diff.OldValue}' => '{diff.NewValue}'");
        }
        return Task.CompletedTask;
    }

    public Task WriteManyAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken ct = default) => Task.CompletedTask;
}
```

---

## 6. Field-Level Protection & Encryption

Protect sensitive fields using the `[SensitiveData]` attribute:

```csharp
public record User : BaseEntity<ObjectId>
{
    // Plaintext in DB. Old/New values are automatically masked in the audit log.
    [SensitiveData(Protection = ProtectionType.Mask, MaskingType = MaskingType.Email)]
    public string Email { get; set; } = string.Empty;

    // Encrypted with AES-256-CBC at rest. Decrypted on read. Diffs are masked.
    [SensitiveData(Protection = ProtectionType.Encrypt)]
    public string CreditCard { get; set; } = string.Empty;
}
```

### Presentation Masking API
If you need to return an entity to an API response or log it locally, call `MaskSensitiveFields` to get a clone with PII fields replaced by `*` characters:
```csharp
var user = await userRepo.GetByIdAsync(userId);
var safeUser = userRepo.MaskSensitiveFields(user);
return Ok(safeUser); // Safe to serialize - PII fields are masked!
```

---

## 7. Pagination Engine

`FoundryMongo` supports fast offset pagination and O(1) compound key seek (cursor) pagination.

```csharp
// 1. Cursor-Based Seek Pagination (Recommended for large datasets)
var seekRequest = new PagedRequest
{
    PageSize = 25,
    CursorInfo = new CursorSeekInfo("Sku", "VP-100", SortOrder.Ascending)
};
PagedResult<Product> page = await productRepo.GetPagedAsync(seekRequest);
var nextCursor = page.NextCursor; // Pass to next page API call

// 2. Offset-Based Pagination (For shallow search pages)
var offsetRequest = new PagedRequest
{
    PageNumber = 3,
    PageSize = 20,
    MaxDepthCap = 1000 // Throws if skip depth is too deep, encouraging cursor swap
};
PagedResult<Product> offsetPage = await productRepo.GetPagedAsync(offsetRequest);
```

---

## 8. Heterogeneous Unified Search

Query multiple collections simultaneously and project heterogeneous documents into a single unified search result.

```csharp
var searchRequest = new CrossCollectionSearchRequest
{
    EntityTypes = [typeof(Customer), typeof(Vendor)],
    Criteria = [new SearchCriterion("Email", SearchOperator.Contains, "@domain.com")],
    Pagination = new PagedRequest { PageSize = 10 }
};

PagedResult<UnifiedSearchResult> results = await customerRepo.CrossCollectionSearchAsync(searchRequest);
```

---

## 9. Version History & Recovery

If an entity implements `IVersionable`, past snapshots can be queried and restored easily.

```csharp
// 1. Get all past revisions of a document
IReadOnlyList<EntityRevision> revisions = await productRepo.GetRevisionsAsync(productId);

// 2. Restore to version 3
Product restoredProduct = await productRepo.RestoreVersionAsync(productId, version: 3);
```

---

## 10. Soft-Delete Recovery

Soft-deleted items can be restored back to active state, clearing delete flags and logging the action.

```csharp
// Restore soft-deleted record back to active state
await customerRepo.RestoreDeletedAsync(customerId);
```

---

## 11. Hot/Cold Partitioning & Dynamic Archiving

Optimize performance for massive collections (over 100M records) by storing active records in a hot collection and older records in year-based archive partitions.

### Partitioning Attribute
Apply the `[Partitioned]` attribute to the domain entity and specify the age threshold in years:

```csharp
[Partitioned(YearsArchiveThreshold = 2)]
public record Invoice : BaseEntity<ObjectId>, IEntity<ObjectId>
{
    public string InvoiceNumber { get; set; } = string.Empty;
    public double TotalAmount { get; set; }
}
```

### Partitioned Repository Operations
Registering domain types automatically configures `PartitionedRepository<T>` under the hood. ID-based routing automatically inspects `ObjectId.CreationTime` to identify and route queries to the correct partition (hot active collection vs. `Invoice_YYYY` archive collection).

### Cross-Partition Aggregations
Heavy analytics (e.g. sums or counts) are routed across all historical partitions transparently using `$unionWith` stages:

```csharp
var pipeline = PipelineDefinition<Invoice, BsonDocument>.Create(new[]
{
    new BsonDocument("$group", new BsonDocument
    {
        { "_id", "$Status" },
        { "TotalRevenue", new BsonDocument("$sum", "$TotalAmount") }
    })
});

// Queries all year collections dynamically and aggregates in a single database roundtrip
IReadOnlyList<BsonDocument> report = await invoiceRepo.AggregateAsync(pipeline);
```

### Data Archival Hosted Worker
An ambient background worker (`DataArchivalWorker`) periodically sweeps active collections, identifies records older than the threshold, and moves them to their year-specific cold partitions using multi-document transactional migrations.

---

## 12. Resilience, Pooling & Session Unit of Work

### Connection Pool Configuration
Pass connection pool size parameters inside `AddFoundryMongo`:

```csharp
builder.Services.AddFoundryMongo(options =>
{
    options.ConnectionString = "mongodb://localhost:27017";
    options.DatabaseName = "CommercialAppDb";
    
    // Performance: Tune pools for high-scale microservices
    options.MinConnectionPoolSize = 20;
    options.MaxConnectionPoolSize = 300;
});
```

### Session Unit of Work
Coordinate atomic mutations across multiple repositories using the `IUnitOfWorkFactory` without passing session objects manually in handler parameters:

```csharp
var uowFactory = serviceProvider.GetRequiredService<IUnitOfWorkFactory>();

using (var uow = uowFactory.Create())
{
    await _orderRepo.InsertAsync(newOrder, uow.Session);
    await _customerRepo.UpdateByObjectIdAsync(customerId, c => { c.LastPurchaseDate = DateTime.UtcNow; return c; }, uow.Session);
    
    await uow.CommitAsync();
}
```

### Exponential Backoff Retry Policy
All database queries run through `RetryPolicyHelper` which filters for transient exceptions (sockets dropouts, timeouts, failover elections) and retries operations with exponential delays.

---

## 13. Compliance & Read Auditing

### Read Auditing (Access Logging)
Protect sensitive tables by registering read event auditing. Simply mark your entity class with the `[ReadAudited]` attribute:

```csharp
[ReadAudited]
public record PatientRecord : BaseEntity<ObjectId>, IEntity<ObjectId>
{
    public string MedicalNotes { get; set; } = string.Empty;
}
```
Any read operations (GetById, FindMany, seek paging, historical revision lookups) will push a `Read` action audit entry to the registered `IAuditSink`.

### Attribute-Based Access Control PII Masking
PII fields marked with `[SensitiveData]` are automatically masked when returning the entity unless the current caller context (`ICurrentUserContext.User`) contains the explicit `scope` claim containing `view:pii`.
