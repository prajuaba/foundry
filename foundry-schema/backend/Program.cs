using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Foundry.Schema.Compiler;

var builder = WebApplication.CreateBuilder(args);

// Enable CORS for frontend Vite server
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://localhost:5174", "http://127.0.0.1:5173")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.AddHttpClient();

var app = builder.Build();

app.UseCors("AllowAll");

// API endpoint to compile JSON schema to C# POCO classes
app.MapPost("/api/compile", (SchemaModel schema) =>
{
    try
    {
        if (schema == null || string.IsNullOrEmpty(schema.Namespace))
        {
            return Results.BadRequest(new { error = "Invalid schema. Namespace is required." });
        }

        var files = PocoGenerator.Generate(schema);
        return Results.Ok(new { files });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Compilation failed: {ex.Message}");
    }
});

// Derive api-manifest.json from an IR document.
//
// Exists so the Studio UI can obtain a manifest without computing one itself. Studio used to derive it
// in TypeScript by walking its canvas, which made it a second producer of a contract this compiler
// owns -- and the two disagreed on the route prefix and on whether an entity with no declared methods
// receives a full CRUD surface. One producer means a manifest downloaded from Studio is identical to
// one written by `foundry compile`.
app.MapPost("/api/manifest", (SchemaModel schema) =>
{
    try
    {
        if (schema == null || string.IsNullOrEmpty(schema.Namespace))
        {
            return Results.BadRequest(new { error = "Invalid schema. Namespace is required." });
        }

        return Results.Text(ApiManifestGenerator.Generate(schema), "application/json");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Manifest generation failed: {ex.Message}");
    }
});

// Generate IR from a natural-language instruction using a local Ollama model.
//
// The prompt, the vocabulary and the output grammar all come from the compiler via
// AiSpecBundle / IrSchemaGenerator, so they cannot drift from what the compiler accepts.
// The response is grammar-constrained to the IR JSON Schema and then validated, with
// failures fed back to the model for repair.
app.MapPost("/api/ai/prompt", async (AiRequest request, IHttpClientFactory httpClientFactory) =>
{
    try
    {
        if (string.IsNullOrEmpty(request.Prompt))
        {
            return Results.BadRequest(new { error = "Prompt is required." });
        }

        var options = AiGenerationOptions.Resolve(request.OllamaHost, request.OllamaModel);
        var generator = new AiSchemaGenerator(httpClientFactory.CreateClient(), options);

        var result = await generator.GenerateAsync(request.Prompt, request.CurrentSchema);

        if (!result.Success)
        {
            return Results.Problem(
                title: result.Error ?? "The model could not produce a valid IR document.",
                detail: string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        }

        return Results.Ok(result.Schema);
    }
    catch (JsonException ex)
    {
        return Results.Problem($"Failed to parse AI-generated schema: {ex.Message}");
    }
    catch (Exception ex)
    {
        return Results.Problem($"AI request failed: {ex.Message}");
    }
});

app.MapGet("/api/ai/models", async (string host, IHttpClientFactory httpClientFactory) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return Results.BadRequest(new { error = "Host is required." });
        }

        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5); // Fast timeout for validation

        var ollamaUrl = $"{host.TrimEnd('/')}/api/tags";
        var response = await client.GetAsync(ollamaUrl);

        if (!response.IsSuccessStatusCode)
        {
            return Results.Problem($"Failed to query Ollama: {response.ReasonPhrase}");
        }

        var tagsResponse = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>();
        if (tagsResponse == null || tagsResponse.Models == null)
        {
            return Results.Ok(new List<string>());
        }

        var modelNames = tagsResponse.Models.Select(m => m.Name).ToList();
        return Results.Ok(modelNames);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Could not connect to Ollama host: {ex.Message}");
    }
});

app.MapPost("/api/save-pocos", (SaveRequest request) =>
{
    try
    {
        if (request == null || string.IsNullOrEmpty(request.OutputPath))
        {
            return Results.BadRequest(new { error = "Invalid request. OutputPath is required." });
        }

        // Validate the output path is within an allowed directory to prevent path traversal
        var resolvedPath = Path.GetFullPath(request.OutputPath);
        var allowedRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", ".."));
        if (!resolvedPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = $"Output path must be within the workspace: {allowedRoot}" });
        }

        Directory.CreateDirectory(resolvedPath);

        foreach (var file in request.Files)
        {
            var filePath = Path.Combine(resolvedPath, file.Key);
            File.WriteAllText(filePath, file.Value);
        }

        return Results.Ok(new { message = $"Successfully saved {request.Files.Count} classes to: {resolvedPath}" });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to save files locally: {ex.Message}");
    }
});

app.MapPost("/api/save-manifest", (SaveManifestRequest request) =>
{
    try
    {
        if (request == null || string.IsNullOrEmpty(request.OutputPath))
        {
            return Results.BadRequest(new { error = "Invalid request. OutputPath is required." });
        }

        if (request.Schema == null || string.IsNullOrEmpty(request.Schema.Namespace))
        {
            return Results.BadRequest(new { error = "Invalid request. A schema with a namespace is required." });
        }

        // Validate the output path is within an allowed directory to prevent path traversal
        var targetPath = Path.GetFullPath(request.OutputPath);
        var allowedRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", ".."));
        if (!targetPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = $"Output path must be within the workspace: {allowedRoot}" });
        }

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Derived here, from the IR, rather than accepted from the client. Studio previously computed
        // the manifest itself and posted the result, making it a second producer of a contract the
        // compiler owns; the two disagreed on the route prefix and on whether an entity with no
        // declared methods receives full CRUD.
        var manifestJson = ApiManifestGenerator.Generate(request.Schema);
        File.WriteAllText(targetPath, manifestJson);

        return Results.Ok(new { message = $"Successfully saved api-manifest.json to: {targetPath}" });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to save manifest file locally: {ex.Message}");
    }
});

app.Run();

public record AiRequest
{
    public string Prompt { get; init; } = string.Empty;
    public SchemaModel? CurrentSchema { get; init; }
    public string? OllamaHost { get; init; }
    public string? OllamaModel { get; init; }
}

public record SaveRequest
{
    public Dictionary<string, string> Files { get; init; } = new();
    public string OutputPath { get; init; } = string.Empty;
}

/// <summary>
/// Request to derive and persist <c>api-manifest.json</c> from an IR document.
/// </summary>
/// <remarks>
/// Carries the <see cref="SchemaModel"/> rather than a pre-built manifest. The client used to compute
/// the manifest itself and post the result, which made Studio a second producer of a contract the
/// compiler already owns -- and the two disagreed on route prefixes and on whether an entity with no
/// declared methods gets full CRUD. Accepting only the IR makes <c>ApiManifestGenerator</c> the single
/// producer, so a manifest written from Studio is byte-identical to one written by
/// <c>foundry compile</c>.
/// </remarks>
public record SaveManifestRequest
{
    public SchemaModel Schema { get; init; } = null!;
    public string OutputPath { get; init; } = string.Empty;
}

public record OllamaResponse
{
    [JsonPropertyName("response")]
    public string Response { get; init; } = string.Empty;
}

public record OllamaTagsResponse
{
    [JsonPropertyName("models")]
    public List<OllamaTagModel>? Models { get; init; }
}

public record OllamaTagModel
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}
