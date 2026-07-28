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
using Foundry.Api.GraphQL;
using Foundry.Api.Security;
using Foundry.Api.Docs;
using Foundry.Api.Workflow;
using Foundry.Api.MediatR.Behaviors;

namespace Foundry.Api.Template;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add standard Web API services
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMemoryCache();

        // Bearer authentication. Generated endpoints call RequireAuthorization, so without a scheme
        // registered the application refuses to start rather than serving 500s.
        builder.Services.AddFoundryAuthentication(builder.Configuration);

        // Load ApiManifest configuration
        var manifestPath = Path.Combine(builder.Environment.ContentRootPath, "api-manifest.json");
        var manifestJson = await File.ReadAllTextAsync(manifestPath);
        var manifest = JsonSerializer.Deserialize<ApiManifest>(manifestJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Failed to deserialize api-manifest.json");

        builder.Services.AddSingleton(manifest);

        // Setup distributed in-memory cache for L2 caching tier
        builder.Services.AddDistributedMemoryCache();

        // Database settings resolution
        var mongoConnectionString = Environment.GetEnvironmentVariable("MONGODB_CONNECTION") 
            ?? builder.Configuration.GetConnectionString("MongoDb") 
            ?? "mongodb://localhost:27017";

        var mongoDatabaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE") 
            ?? builder.Configuration["MongoDbSettings:DatabaseName"] 
            ?? "FoundryTemplateDb";

        var encryptionKeyRaw = Environment.GetEnvironmentVariable("MONGODB_ENCRYPTION_KEY") 
            ?? builder.Configuration["MongoDbSettings:EncryptionKey"] 
            ?? "12345678901234567890123456789012"; // 32-character key for AES-256 fallback

        var encryptionKeyBytes = System.Text.Encoding.UTF8.GetBytes(encryptionKeyRaw.PadRight(32).Substring(0, 32));

        builder.Services.AddFoundryMongo(options =>
        {
            options.ConnectionString = mongoConnectionString;
            options.DatabaseName = mongoDatabaseName;
            options.EncryptionKey = Convert.ToBase64String(encryptionKeyBytes);
            options.EnableCaching = false; // Pipeline caching behavior handles this
        });

        // Register default console audit sink
        builder.Services.AddSingleton<IAuditSink, DefaultConsoleAuditSink>();

        // Register Current User claims resolver
        builder.Services.AddScoped<ICurrentUserContext, DefaultTemplateUserContext>();

        // Register validators from this assembly
        builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);

        // Register MediatR
        builder.Services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
            cfg.RegisterServicesFromAssembly(typeof(Foundry.Api.MediatR.InsertCommand<>).Assembly);
        });

        // Register compile-time generated Handlers
        builder.Services.AddGeneratedHandlers();

        // Register MediatR pipeline behaviors
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(AuditBehavior<,>));
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(SecurityBehavior<,>));
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(IdempotencyBehavior<,>));
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(CachingBehavior<,>));
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(BusinessRuleBehavior<,>));
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(OutboxDomainEventBehavior<,>));

        // Register GraphQL dynamic types builder
        builder.Services.AddDynamicGraphQL(manifest);

        // Register Exception Handler Middleware
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

        app.UseExceptionHandler();

        app.UseAuthentication();
        app.UseAuthorization();

        // Resolves the ambient tenant for the request, before anything reads or writes, and after
        // authentication so the caller's own token claim can take precedence over the header.
        //
        // Nothing in the framework, the templates or the scaffolder ever added this, so
        // ITenantContext.HasTenant was false on every request ever served. The repository's tenant
        // filter is written as `if (HasTenant)`, so it never applied: every multi-tenant read
        // returned every tenant's rows with a 200 and no sign that isolation had been skipped.
        app.UseMiddleware<Foundry.Api.Middleware.TenantContextMiddleware>();

        // Enable documentation endpoints for dev environment
        app.UseSwagger();
        app.UseSwaggerUI();

        // Map compile-time generated REST endpoints
        app.MapGeneratedEndpoints(manifest);

        // Map dynamic interactive docs spec
        app.MapDocsEndpoint(manifest);

        // GET {entity}/{id}/history for every entity with a workflow. The transition log was
        // written on every transition and nothing served it.
        app.MapWorkflowHistory(manifest);

        // Map Hot Chocolate GraphQL schema.
        //
        // Behind RequireAuthorization for the same reason the REST endpoints are: the GraphQL schema
        // exposes create, update and delete over every entity in the manifest, reaching the same
        // repositories. Left anonymous, it is a full CRUD surface beside an API that refuses
        // anonymous callers.
        app.MapGraphQL().RequireAuthorization();

        await app.RunAsync();
    }
}

public class DefaultConsoleAuditSink : IAuditSink
{
    public Task WriteAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        Console.WriteLine($"[Audit Log] Actor: {entry.OperatorId} | Action: {entry.Action} | Entity: {entry.EntityType} | Diffs: {entry.PropertyDiffs.Count}");
        return Task.CompletedTask;
    }

    public Task WriteManyAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken ct = default)
    {
        foreach (var entry in entries)
        {
            Console.WriteLine($"[Audit Log Batch] Actor: {entry.OperatorId} | Action: {entry.Action} | Entity: {entry.EntityType}");
        }
        return Task.CompletedTask;
    }
}

public class DefaultTemplateUserContext : ICurrentUserContext
{
    public string OperatorId => "SystemBootstrapper";
    public string? OperatorName => "System Bootstrapper";
    public System.Security.Claims.ClaimsPrincipal? User => null;
}
