using System;
using System.IO;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MediatR;
using FluentValidation;
using Foundry.Mongo.DependencyInjection;
using Foundry.Core.Audit;
using Foundry.Core.User;
using Foundry.Rules;
using Foundry.E2E.Showcase.Entities;
using Foundry.E2E.Showcase.Commands;
using Foundry.E2E.Showcase.Services;
using Foundry.E2E.Showcase.Rules;
using Foundry.E2E.Showcase.Runner;

var builder = WebApplication.CreateBuilder(args);

// Add Web API & Swagger Services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();

// Standard 32-character AES-256 Encryption key for KMS envelope encryption
var encryptionKeyRaw = "FoundryE2EShowcaseSecretKey32B!";
var encryptionKeyBytes = Encoding.UTF8.GetBytes(encryptionKeyRaw.PadRight(32)[..32]);

// 1. Foundry.Mongo Layer Registration
builder.Services.AddFoundryMongo(options =>
{
    options.ConnectionString = builder.Configuration.GetConnectionString("MongoDb") ?? "mongodb://localhost:27017";
    options.DatabaseName = "FoundryE2EShowcaseDb";
    options.EncryptionKey = Convert.ToBase64String(encryptionKeyBytes);
    options.EnableCaching = true;
});

// 2. Foundry.RealTime Layer Registration
builder.Services.AddSingleton<IAuditSink, DefaultConsoleAuditSink>();
builder.Services.AddFoundryRealTime();

// 3. Foundry.Rules Layer Registration
builder.Services.AddFoundryRules();
builder.Services.AddTransient<IBusinessRule<SubmitOrderCommand>, OrderAmountValidationRule>();

// 4. MediatR Registration
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
});

// 5. Custom Domain Services & E2E Runner
builder.Services.AddSingleton<CatalogFileService>();
builder.Services.AddSingleton<OrderBusinessRulesService>();
builder.Services.AddTransient<E2EShowcaseRunner>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Map Web API Endpoints
app.MapPost("/api/v1/orders/submit", async (SubmitOrderCommand cmd, IMediator mediator) =>
{
    var result = await mediator.Send(cmd);
    return Results.Ok(result);
});

app.MapGet("/api/v1/health", () => Results.Ok(new { Status = "Healthy", Framework = "Foundry .NET 10" }));

// If executed with --run-e2e or directly in CLI mode, run the showcase scenario
if (args.Contains("--run-e2e") || !args.Any())
{
    using var scope = app.Services.CreateScope();
    var runner = scope.ServiceProvider.GetRequiredService<E2EShowcaseRunner>();
    await runner.RunFullScenarioAsync();
}
else
{
    app.Run();
}

public class DefaultConsoleAuditSink : IAuditSink
{
    public Task WriteAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[AUDIT-LOG] [{entry.TimestampUtc:u}] Action={entry.Action} Entity={entry.EntityType} Id={entry.EntityId} User={entry.OperatorId}");
        return Task.CompletedTask;
    }

    public Task WriteManyAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken cancellationToken = default)
    {
        foreach (var entry in entries)
        {
            Console.WriteLine($"[AUDIT-LOG-BATCH] [{entry.TimestampUtc:u}] Action={entry.Action} Entity={entry.EntityType} Id={entry.EntityId} User={entry.OperatorId}");
        }
        return Task.CompletedTask;
    }
}
