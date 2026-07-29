using System.Text.Json;
using MediatR;
using FluentValidation;
using Foundry.Mongo.DependencyInjection;
using Foundry.Api.Manifest;
using Foundry.Api.Endpoints;
using Foundry.Api.Workflow;
using Foundry.Api.MediatR.Behaviors;
using Foundry.Core.Serialization;
using Foundry.Rules;
using Foundry.RealTime;
using Foundry.E2E.Showcase.Kafka;
using Foundry.E2E.Showcase.Rules;
using Foundry.E2E.Showcase.Services;
using Foundry.E2E.Showcase.RealTime;
using Foundry.E2E.Showcase.Runner;

// The showcase is a Foundry application built the way the framework says to build one: every
// entity, endpoint, command, rule stub, Kafka consumer, GraphQL type, real-time channel and
// workflow definition under Generated/ comes from e2e-schema.ir.json, and nothing under it is
// written by hand. What is written by hand is what a schema cannot state: the business logic in
// the scaffolds, and this host.
//
// It used to be the other way round. The schema sat beside a hand-written domain that had drifted
// from it -- an entity the code did not have, a Kafka topic it never published to, an endpoint the
// schema said was role-restricted and the code left anonymous -- because nothing compiled the one
// into the other.

var builder = WebApplication.CreateBuilder(args);

// The API surface is described by api-manifest.json, which the schema compiler derives from the
// domain schema. The analyser reads the same file at compile time to generate the endpoints.
var manifestPath = Path.Combine(builder.Environment.ContentRootPath, "api-manifest.json");
if (!File.Exists(manifestPath))
{
    throw new InvalidOperationException(
        $"api-manifest.json not found at {manifestPath}. Regenerate it with "
        + "'foundry schema build -i e2e-schema.ir.json -o Generated'; without it no entity "
        + "endpoints are served.");
}

var manifest = JsonSerializer.Deserialize<ApiManifest>(
    File.ReadAllText(manifestPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException("api-manifest.json could not be deserialized.");

builder.Services.AddSingleton(manifest);

// 1. MongoDB data access: tenant filtering, envelope encryption, OCC, auditing, masking.
builder.Services.AddFoundryMongo(options =>
{
    options.ConnectionString = Environment.GetEnvironmentVariable("MONGODB_CONNECTION")
        ?? builder.Configuration.GetConnectionString("MongoDb")
        ?? "mongodb://localhost:27017";

    options.DatabaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE")
        ?? builder.Configuration["MongoDbSettings:DatabaseName"]
        ?? "FoundryE2EShowcaseDb";

    // A base64 32-byte AES-256 key. Real deployments read this from a KMS; the showcase derives a
    // fixed development key so `dotnet run` works with no setup, and says so rather than looking
    // secure.
    options.EncryptionKey = Environment.GetEnvironmentVariable("MONGODB_ENCRYPTION_KEY")
        ?? Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("DEVELOPMENT-ONLY-showcase-key-32"));

    options.EnableCaching = true;
});

// 2. Real-time audit broker (SignalR, WebSockets, SSE).
builder.Services.AddFoundryRealTime();

// 3. Business rules engine, and the rules the schema names.
builder.Services.AddFoundryRules();

// 4. Authentication. The generated endpoints call RequireAuthorization, so without a scheme the
//    application refuses to start rather than serving 500s.
builder.Services.AddFoundryAuthentication(builder.Configuration);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Foundry.Core.User.ICurrentUserContext, Foundry.Api.Security.CurrentUserContext>();

builder.Services.AddMemoryCache();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);

builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
    cfg.RegisterServicesFromAssembly(typeof(Foundry.Api.MediatR.InsertCommand<>).Assembly);
});

// Generated request handlers, one per entity method in the manifest.
builder.Services.AddGeneratedHandlers();

// Every business rule the schema named, bound to the request it validates. Without this they are
// compiled into the application and never run.
builder.Services.AddGeneratedBusinessRules();

// Generated Kafka consumer handlers for Order and OrderSummary.
builder.Services.AddGeneratedKafkaHandlers();

// Generated FileIO services for the entities that opted in.
builder.Services.AddSingleton<ProductFileService>();
builder.Services.AddSingleton<OrderSummaryFileService>();

// Workflow transitions, registered by type so the engine resolves them without scanning
// loaded assemblies.
builder.Services.AddFoundryWorkflows(registry => registry
    .Register<Foundry.E2E.Showcase.Order>());

builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(SecurityBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(BusinessRuleBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(CachingBehavior<,>));

builder.Services.AddExceptionHandler<Foundry.Api.Middleware.GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// Entity ids are MongoDB ObjectIds, which System.Text.Json cannot round-trip unaided.
builder.Services.ConfigureHttpJsonOptions(options =>
    FoundryJsonDefaults.Apply(options.SerializerOptions));

// GraphQL for the entities whose schema set enableGraphQL -- Customer, Product and Order, but not
// CustomerNote or LedgerEntry. Built from the same manifest as REST, so the two protocols cannot
// disagree about which entities exist or who may read them.
builder.Services.AddDynamicGraphQL(manifest);

// The showcase runner, which drives the framework from the inside rather than over HTTP.
builder.Services.AddTransient<E2EShowcaseRunner>();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

// Resolves the ambient tenant before any endpoint runs, preferring the token claim over the
// X-Tenant-ID header. Customer and LedgerEntry are multi-tenant, and without this their tenant
// filter never applies.
app.UseMiddleware<Foundry.Api.Middleware.TenantContextMiddleware>();

// Generated REST endpoints for every entity in the manifest.
app.MapGeneratedEndpoints(manifest);

// GET {entity}/{id}/history for every entity with a workflow.
app.MapWorkflowHistory(manifest);

// Real-time channels, and the generated per-entity endpoints for the entities that opted in.
app.MapFoundryRealTime();
app.MapGeneratedRealTimeEndpoints();

// GraphQL over the same repositories the REST surface uses.
app.MapGraphQL();

app.MapGet("/api/v1/health", () => Results.Ok(new { Status = "Healthy", Framework = "Foundry .NET 10" }));

// `--run-e2e` drives the in-process scenario instead of serving. Serving is the default, because
// the interesting claim is that the generated API works, and that needs the app to be up.
if (args.Contains("--run-e2e"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<E2EShowcaseRunner>().RunFullScenarioAsync();
    return;
}

app.Run();

// Named so WebApplicationFactory<Program> can host this project in a test.
public partial class Program { }
