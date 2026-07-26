using System.Text;
using Foundry.Mongo.DependencyInjection;
using Foundry.Mongo.Diagnostics;
using Foundry.Core.Audit;
using Foundry.Core.User;
using Foundry.Core.Entities;
using Foundry.Core.Entities;
using Foundry.Core.Paging;
using Foundry.Mongo.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;

var builder = WebApplication.CreateBuilder(args);

// 1. Configure in-memory Console Audit Sink and Mock User Context
builder.Services.AddSingleton<IAuditSink, ConsoleAuditSink>();
builder.Services.AddSingleton<ICurrentUserContext, SampleUserContext>();

// 2. Register FoundryMongo with element camel-casing, transparent Caching, and AES Field-Level Encryption
var encryptionKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("sample-key-1234567890123456789012")); // 32 bytes
builder.Services.AddFoundryMongo(options =>
{
    // Bind to a local test Mongo instance (or in-memory mock URL)
    options.ConnectionString = builder.Configuration.GetConnectionString("MongoDb") ?? "mongodb://localhost:27017";
    options.DatabaseName = "FoundryMongoSampleDb";
    options.EncryptionKey = encryptionKey;
    options.EnableCaching = true; // Enables transparent CachedRepository decoration
    options.DefaultCacheTtl = TimeSpan.FromMinutes(2);
});

// 3. Register ASP.NET Core Health Checks using the registered MongoDbHealthCheck
builder.Services.AddHealthChecks()
    .AddCheck<MongoDbHealthCheck>("FoundryMongoDb", failureStatus: HealthStatus.Degraded);

var app = builder.Build();

// 4. Automatically create database indexes on startup
using (var scope = app.Services.CreateScope())
{
    var productRepo = scope.ServiceProvider.GetRequiredService<IRepository<SampleProduct>>();
    try
    {
        await productRepo.CreateIndexesAsync();
        app.Logger.LogInformation("Database indexes initialized successfully.");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Could not initialize indexes on startup. Ensure MongoDB is running.");
    }
}

// ─── Minimal API Routes ──────────────────────────────────────────────────

// Health Check
app.MapHealthChecks("/healthz");

// Create Product
app.MapPost("/products", async ([FromBody] CreateProductDto dto, IRepository<SampleProduct> repo) =>
{
    var product = new SampleProduct
    {
        Id = ObjectId.GenerateNewId(),
        Sku = dto.Sku,
        Title = dto.Title,
        CostPrice = dto.CostPrice,
        SecretSupplierNotes = dto.SecretSupplierNotes,
        VendorEmail = dto.VendorEmail
    };

    await repo.InsertAsync(product);
    return Results.Created($"/products/{product.Id}", product);
});

// Get Product (Uses transparent cache and transparent field-level decryption!)
app.MapGet("/products/{id}", async (string id, IRepository<SampleProduct> repo) =>
{
    if (!ObjectId.TryParse(id, out var objectId)) return Results.BadRequest("Invalid ObjectId format.");

    var product = await repo.GetByIdAsync(objectId);
    return product is null ? Results.NotFound() : Results.Ok(product);
});

// Get Masked Product (Clones and masks sensitive fields for external API response)
app.MapGet("/products/{id}/masked", async (string id, IRepository<SampleProduct> repo) =>
{
    if (!ObjectId.TryParse(id, out var objectId)) return Results.BadRequest("Invalid ObjectId format.");

    var product = await repo.GetByIdAsync(objectId);
    if (product is null) return Results.NotFound();

    // Transparent cloning and masking (original is untouched)
    var masked = repo.MaskSensitiveFields(product);
    return Results.Ok(masked);
});

// Update Product (OCC checks version match, increments version, and writes history)
app.MapPut("/products/{id}", async (string id, [FromBody] UpdateProductDto dto, IRepository<SampleProduct> repo) =>
{
    if (!ObjectId.TryParse(id, out var objectId)) return Results.BadRequest("Invalid ObjectId format.");

    try
    {
        // UpdateByObjectIdAsync handles loading pre-image, applying selector, running OCC match, and writing audit + history
        await repo.UpdateByObjectIdAsync(objectId, p =>
        {
            p.Title = dto.Title;
            p.CostPrice = dto.CostPrice;
            p.SecretSupplierNotes = dto.SecretSupplierNotes;
            p.Version = dto.ExpectedVersion; // Version match expectation
            return p;
        }, operatorId: "admin-user");

        var updated = await repo.GetByIdAsync(objectId);
        return Results.Ok(updated);
    }
    catch (ConcurrencyException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

// Get Revision History (Exposes BSON data revisions for audit trail)
app.MapGet("/products/{id}/history", async (string id, IRepository<SampleProduct> repo) =>
{
    if (!ObjectId.TryParse(id, out var objectId)) return Results.BadRequest("Invalid ObjectId format.");

    var revisions = await repo.GetRevisionsAsync(objectId);
    var list = revisions.Select(r => new
    {
        r.Version,
        r.ChangedAtUtc,
        r.ChangedBy,
        r.Action,
        RawEncryptedData = r.Data.ToString() // BSON snapshot showing encrypted values
    });

    return Results.Ok(list);
});

// Restore Product Version
app.MapPost("/products/{id}/restore/{version:int}", async (string id, int version, IRepository<SampleProduct> repo) =>
{
    if (!ObjectId.TryParse(id, out var objectId)) return Results.BadRequest("Invalid ObjectId format.");

    try
    {
        var restored = await repo.RestoreVersionAsync(objectId, version);
        return Results.Ok(restored);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

app.Run();

// ─── Sample Types ────────────────────────────────────────────────────────

public record SampleProduct : BaseEntity<ObjectId>, IVersionable, ISoftDelete
{
    [Indexed(Unique = true)]
    public string Sku { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    // Standard field (stored as plaintext)
    public decimal CostPrice { get; set; }

    // Sensitive field protected via Symmetric AES-256 encryption at rest
    [SensitiveData(Protection = ProtectionType.Encrypt)]
    public string SecretSupplierNotes { get; set; } = string.Empty;

    // Sensitive field protected via Masking (stored in plaintext, masked on read clone)
    [SensitiveData(Protection = ProtectionType.Mask, MaskingType = MaskingType.Email)]
    public string VendorEmail { get; set; } = string.Empty;

    public bool IsDeleted { get; init; }
    public DateTime? DeletedAt { get; init; }
}

public record CreateProductDto(string Sku, string Title, decimal CostPrice, string SecretSupplierNotes, string VendorEmail);
public record UpdateProductDto(string Title, decimal CostPrice, string SecretSupplierNotes, int ExpectedVersion);

// Console Audit Sink printing masked diff changes
public sealed class ConsoleAuditSink : IAuditSink
{
    public Task WriteAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n[AUDIT LOG] {entry.Action} on {entry.EntityType} (ID: {entry.EntityId}) by {entry.OperatorId}");
        Console.ResetColor();

        foreach (var diff in entry.PropertyDiffs)
        {
            Console.WriteLine($"   * {diff.PropertyName}: '{diff.OldValue}' => '{diff.NewValue}'");
        }
        return Task.CompletedTask;
    }

    public Task WriteManyAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken ct = default)
    {
        foreach (var entry in entries) WriteAsync(entry, ct);
        return Task.CompletedTask;
    }
}

// Mock User Context
public sealed class SampleUserContext : ICurrentUserContext
{
    public string OperatorId => "console-sample-operator";
    public string? OperatorName => "Console Operator";
    public string Email => "operator@domain.com";
    public System.Security.Claims.ClaimsPrincipal? User => null;
}
