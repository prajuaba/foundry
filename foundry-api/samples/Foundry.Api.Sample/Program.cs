using System;
using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MediatR;
using FluentValidation;
using Foundry.Mongo.DependencyInjection;
using Foundry.Core.Audit;
using Foundry.Core.User;
using Foundry.Api.Manifest;
using Foundry.Api.Endpoints;
using Foundry.Api.Security;
using Foundry.Api.Docs;
using Foundry.Api.Workflow;
using Foundry.Api.MediatR.Behaviors;
using Foundry.Api.Sample.Migrations;

var builder = WebApplication.CreateBuilder(args);

// Add standard Web API services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();

// Generated endpoints require an authenticated caller. The sample registers this the same way the
// template and the scaffolder do -- the three drifting apart is how the missing enforcement went
// unnoticed for as long as it did.
builder.Services.AddFoundryAuthentication(builder.Configuration);
builder.Services.AddMemoryCache();

// Load ApiManifest
var manifestPath = Path.Combine(builder.Environment.ContentRootPath, "api-manifest.json");
var manifestJson = File.ReadAllText(manifestPath);
var manifest = JsonSerializer.Deserialize<ApiManifest>(manifestJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException("Failed to deserialize api-manifest.json");

builder.Services.AddSingleton(manifest);

// Production Distributed Caching Setup (L2 cache tier)
builder.Services.AddDistributedMemoryCache();

// Environment & Secret Configuration Resolution
var mongoConnectionString = Environment.GetEnvironmentVariable("MONGODB_CONNECTION") 
    ?? builder.Configuration.GetConnectionString("MongoDb") 
    ?? "mongodb://localhost:27017";

var mongoDatabaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE") 
    ?? builder.Configuration["MongoDbSettings:DatabaseName"] 
    ?? "OrderingDb";

var encryptionKeyRaw = Environment.GetEnvironmentVariable("MONGODB_ENCRYPTION_KEY") 
    ?? builder.Configuration["MongoDbSettings:EncryptionKey"] 
    ?? throw new InvalidOperationException(
        "FOUNDRY_ENCRYPTION_KEY is required. Set the MONGODB_ENCRYPTION_KEY environment variable " +
        "or configure MongoDbSettings:EncryptionKey with a 32-character encryption key for AES-256.");

var encryptionKeyBytes = System.Text.Encoding.UTF8.GetBytes(encryptionKeyRaw.PadRight(32).Substring(0, 32));

builder.Services.AddFoundryMongo(options =>
{
    options.ConnectionString = mongoConnectionString;
    options.DatabaseName = mongoDatabaseName;
    options.EncryptionKey = Convert.ToBase64String(encryptionKeyBytes);
    options.EnableCaching = false; // Disable transparent cache in favor of our pipeline cache behavior
});

// Register structured production audit logger sink
builder.Services.AddSingleton<IAuditSink, ConsoleAuditSink>();

// Register real-time services (this decorates the registered ConsoleAuditSink)
builder.Services.AddFoundryRealTime();

// Register business rules engine
builder.Services.AddFoundryRules();

// Register Database Migrations
builder.Services.AddSingleton<DatabaseMigration, Migration_V1_Initial>();
builder.Services.AddSingleton<MigrationRunner>();

// Register current user context resolved from HTTP claims principal
builder.Services.AddScoped<ICurrentUserContext, CurrentUserContext>();

// Register FluentValidation validators
builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);

// Register MediatR
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
    cfg.RegisterServicesFromAssembly(typeof(Foundry.Api.MediatR.InsertCommand<>).Assembly);
});

// Register dynamic MediatR handlers for manifest entities (compile-time generated)
builder.Services.AddGeneratedHandlers();

// Register MediatR pipeline behaviors
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(AuditBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(SecurityBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(IdempotencyBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(CachingBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(BusinessRuleBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(OutboxDomainEventBehavior<,>));

// Register dynamic GraphQL
builder.Services.AddDynamicGraphQL(manifest);

// Register Global Exception Handler
builder.Services.AddExceptionHandler<Foundry.Api.Middleware.GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// Foundry entity ids are MongoDB ObjectIds, which System.Text.Json cannot round-trip on its
// own: it writes the struct's members and decodes them back to ObjectId.Empty. The driver then
// treats an empty id as unset and assigns a new one at insert, so a POSTed entity is stored
// under an id the caller never saw. Registering the converter fixes request binding; generated
// endpoints serialize responses through the same FoundryJsonDefaults options.
builder.Services.ConfigureHttpJsonOptions(options =>
    Foundry.Core.Serialization.FoundryJsonDefaults.Apply(options.SerializerOptions));

var app = builder.Build();

// Run schema migrations on startup
using (var scope = app.Services.CreateScope())
{
    var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
    await runner.RunPendingAsync(CancellationToken.None);

    // Skip dynamic index creation if running within test execution context
    var isTesting = AppDomain.CurrentDomain.GetAssemblies().Any(a => 
        a.FullName?.Contains("xunit", StringComparison.OrdinalIgnoreCase) == true ||
        a.FullName?.Contains("Microsoft.TestPlatform", StringComparison.OrdinalIgnoreCase) == true);

    if (!isTesting)
    {
        // Auto-create MongoDB indexes for manifest entities
        var allTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => {
                try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
            })
            .ToList();

        foreach (var ep in manifest.Endpoints)
        {
            var entityTypeName = $"{manifest.Namespace}.{ep.Entity}";
            var entityType = allTypes.FirstOrDefault(t => t.FullName?.Equals(entityTypeName, StringComparison.OrdinalIgnoreCase) == true);
            if (entityType != null)
            {
                var repoType = typeof(Foundry.Mongo.Repositories.IRepository<>).MakeGenericType(entityType);
                var repo = scope.ServiceProvider.GetService(repoType);
                if (repo != null)
                {
                    var createIndexesMethod = repoType.GetMethod(nameof(Foundry.Mongo.Repositories.IRepository<Foundry.Core.Entities.IEntity<MongoDB.Bson.ObjectId>>.CreateIndexesAsync));
                    if (createIndexesMethod != null)
                    {
                        Console.WriteLine($"[Startup] Provisioning MongoDB indexes for: {ep.Entity}...");
                        await (Task)createIndexesMethod.Invoke(repo, new object[] { CancellationToken.None })!;
                    }
                }
            }
        }
    }
}

app.UseExceptionHandler();

app.UseAuthentication();
app.UseAuthorization();

// Enable Swagger UI
app.UseSwagger();
app.UseSwaggerUI();

// Map dynamic endpoints registered from the manifest configuration (compile-time generated)
app.MapGeneratedEndpoints(manifest);

// Map dynamic docs spec endpoint
app.MapDocsEndpoint(manifest);

// GET {entity}/{id}/history for every entity with a workflow. The transition log was written on
// every transition and nothing served it.
app.MapWorkflowHistory(manifest);

// Map Hot Chocolate GraphQL endpoint.
//
// Behind RequireAuthorization for the same reason the REST endpoints are: the GraphQL schema exposes
// create, update and delete over every entity in the manifest, reaching the same repositories. Left
// anonymous, it is a full CRUD surface beside an API that refuses anonymous callers.
app.MapGraphQL().RequireAuthorization();

// Enable raw WebSockets and map real-time routes (/realtime/hub, /realtime/sse, /realtime/ws)
app.UseWebSockets();
app.MapFoundryRealTime();

app.Run();

// Declare public class Program so it can be referenced in WebApplicationFactory integration tests
public partial class Program { }

public class ConsoleAuditSink : IAuditSink
{
    private readonly ILogger<ConsoleAuditSink> _logger;

    public ConsoleAuditSink(ILogger<ConsoleAuditSink> logger)
    {
        _logger = logger;
    }

    public Task WriteAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "AuditLog: {Timestamp} | Actor: {Actor} | Action: {Action} | Entity: {EntityName} | Key: {EntityId} | Collection: {CollectionName} | Diffs Count: {Diffs}",
            entry.TimestampUtc,
            entry.OperatorId,
            entry.Action,
            entry.EntityType,
            entry.EntityId,
            entry.CollectionName,
            entry.PropertyDiffs.Count
        );
        return Task.CompletedTask;
    }

    public Task WriteManyAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken ct = default)
    {
        foreach (var entry in entries)
        {
            _logger.LogInformation(
                "AuditLog Batch: {Timestamp} | Actor: {Actor} | Action: {Action} | Entity: {EntityName} | Key: {EntityId} | Collection: {CollectionName} | Diffs Count: {Diffs}",
                entry.TimestampUtc,
                entry.OperatorId,
                entry.Action,
                entry.EntityType,
                entry.EntityId,
                entry.CollectionName,
                entry.PropertyDiffs.Count
            );
        }
        return Task.CompletedTask;
    }
}
