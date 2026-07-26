# FoundryMongo

`FoundryMongo` is a high-performance, commercial-grade, lightweight Data Access Layer (DAL) wrapper built on top of the native MongoDB C# Driver (v3.x). It extends MongoDB with robust relational-like features (optimistic locking, auditing, versioning, transparent caching, and field-level encryption/masking) while maintaining high throughput.

---

## 🗺️ Key Features

* **🛡️ Optimistic Concurrency Control (OCC)**: Automatic version matching on replacement mutations to prevent concurrency anomalies.
* **🔒 Field-Level Encryption (AES-256-CBC)**: Transparently encrypts marked properties before writing to the database and decrypts on retrieval.
* **🎭 Data Masking**: Presentation-layer cloning/masking (Full, Partial, Email) and automated PII masking in audit logs.
* **📝 Audit Diff Logging**: Dynamic entity pre-image/post-image diff calculations pushed to a configurable audit sink.
* **🔍 Unified Cross-Collection Search**: DB-side aggregation search querying multiple collections at once and returning unified results.
* **📜 Historical Versioning & Restores**: Log historical entity snapshots to shadow `{Collection}_History` tables and restore documents to any version.
* **🗑️ Soft-Delete & Restoration**: Active-only filtering conventions with complete un-soft-delete lifecycle restoration.
* **⚡ Transparent In-Memory Caching**: Cache-aside decorator invalidating cached objects automatically on mutations.
* **🚦 Health Probe Diagnostics**: Integrated `IHealthCheck` diagnostics to monitor active database connection pings.
* **🚀 Fluent DI registration**: Fluent builder setup including automatic camelCase elements naming conventions.

---

## 🛠️ Quick Start

### 1. Register in `Program.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;

builder.Services.AddFoundryMongo(options =>
{
    options.ConnectionString = "mongodb://localhost:27017";
    options.DatabaseName = "ApplicationDb";
    options.EncryptionKey = "base64-encoded-32-byte-key..."; // For AES-256
    options.EnableCaching = true; // Enables transparent CachedRepository wrapper
    options.DefaultCacheTtl = TimeSpan.FromMinutes(5);
});

builder.Services.AddHealthChecks()
    .AddCheck<MongoDbHealthCheck>("DatabaseHealth");
```

### 2. Define your Entity

```csharp
using MongoDB.Bson;
using FoundryMongo.Domain.Entities;
using FoundryMongo.Domain.Filters;

public record User : BaseEntity<ObjectId>, IVersionable, ISoftDelete
{
    public string Name { get; set; } = string.Empty;

    // Plaintext in DB. Masked in audit logs and presentation clones.
    [SensitiveData(Protection = ProtectionType.Mask, MaskingType = MaskingType.Email)]
    public string Email { get; set; } = string.Empty;

    // Encrypted via AES-256-CBC at rest in MongoDB. Decrypted on read.
    [SensitiveData(Protection = ProtectionType.Encrypt)]
    public string CreditCardNumber { get; set; } = string.Empty;

    // ISoftDelete properties
    public bool IsDeleted { get; init; }
    public DateTime? DeletedAt { get; init; }
}
```

### 3. Basic CRUD Operations

Inject `IRepository<T>` into your service layers:

```csharp
public class UserService
{
    private readonly IRepository<User> _repository;

    public UserService(IRepository<User> repository)
    {
        _repository = repository;
    }

    public async Task CreateUserAsync(User user)
    {
        await _repository.InsertAsync(user);
    }

    public async Task<User?> GetUserAsync(ObjectId id)
    {
        return await _repository.GetByIdAsync(id); // Automatic decryption and caching
    }

    public async Task UpdateUserAsync(User user)
    {
        try
        {
            await _repository.UpdateAsync(user); // Undergoes OCC version check
        }
        catch (ConcurrencyException ex)
        {
            // Handle edit collision
        }
    }
}
```

---

## 📈 Running Benchmarks & Tests

### Executing Unit Tests
Run the test suite covering OCC, auditing, encryption, caching, and soft-delete:
```bash
dotnet test tests/FoundryMongo.Tests
```

### Executing Latency Profiler
Run the built-in benchmark console application to measure read latency (direct vs cached vs decrypted):
```bash
dotnet run -c Release --project samples/FoundryMongo.Benchmark
```

---

## 📚 Developer Reference
For detailed documentation on transactions, custom audit sinks, seek pagination, and restorations, read the [Developer Reference Manual](./docs/developer_reference.md).
