using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Foundry.Api.SourceGenerators
{
    /// <summary>
    /// Generates MediatR registrations, minimal-API endpoint mappings and filter builders from
    /// an <c>api-manifest.json</c> supplied as an AdditionalFile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implemented as an <see cref="IIncrementalGenerator"/> so the manifest is re-parsed only
    /// when it actually changes. The previous <c>ISourceGenerator</c> re-ran the whole pipeline on
    /// every compilation, which in an IDE means on every keystroke.
    /// </para>
    /// <para>
    /// The manifest is parsed with <see cref="JsonDocument"/>. It was previously scanned with
    /// <c>IndexOf</c> plus manual brace counting, which quietly mis-parses any manifest containing
    /// a brace or bracket inside a string value — a route template such as <c>/orders/{id}</c> is
    /// enough to desynchronise the scanner.
    /// </para>
    /// </remarks>
    [Generator]
    public class ApiRouteGenerator : IIncrementalGenerator
    {
        private const string ManifestFileName = "api-manifest.json";

        private static readonly DiagnosticDescriptor GeneratorFailure = new(
            "FNDRYGEN001",
            "Source generator failure",
            "Failed to generate routes from manifest: {0}",
            "Design",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor ManifestParseFailure = new(
            "FNDRYGEN002",
            "Malformed API manifest",
            "api-manifest.json is not valid JSON and no endpoints were generated: {0}",
            "Design",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        /// <inheritdoc />
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Only the manifest's text participates in the pipeline, so edits to ordinary source
            // files do not invalidate the generated output.
            var manifest = context.AdditionalTextsProvider
                .Where(static file => file.Path.EndsWith(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                .Select(static (file, ct) => file.GetText(ct)?.ToString())
                .Where(static text => !string.IsNullOrWhiteSpace(text))
                .Collect();

            var source = context.CompilationProvider.Combine(manifest);

            context.RegisterSourceOutput(source, static (spc, pair) =>
            {
                var (compilation, manifests) = pair;
                var json = manifests.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(json)) return;

                Emit(spc, compilation, json!);
            });
        }

        private static void Emit(SourceProductionContext context, Compilation compilation, string json)
        {
            string ns;
            List<GeneratedEndpoint> endpoints;
            List<GeneratedCustomEndpoint> customEndpoints;

            try
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

                var root = document.RootElement;
                ns = GetString(root, "Namespace") ?? "Domain";
                endpoints = ReadEndpoints(root);
                customEndpoints = ReadCustomEndpoints(root);
            }
            catch (JsonException ex)
            {
                context.ReportDiagnostic(Diagnostic.Create(ManifestParseFailure, Location.None, ex.Message));
                return;
            }

            try
            {
                context.AddSource("GeneratedServices.g.cs",
                    SourceText.From(GenerateServicesCode(ns, endpoints), Encoding.UTF8));

                context.AddSource("GeneratedEndpoints.g.cs",
                    SourceText.From(GenerateEndpointsCode(ns, endpoints, customEndpoints), Encoding.UTF8));

                context.AddSource("GeneratedFilterBuilders.g.cs",
                    SourceText.From(GenerateFilterBuildersCode(compilation, ns, endpoints), Encoding.UTF8));
            }
            catch (Exception ex)
            {
                context.ReportDiagnostic(Diagnostic.Create(GeneratorFailure, Location.None, ex.Message));
            }
        }

        private static List<GeneratedEndpoint> ReadEndpoints(JsonElement root)
        {
            var results = new List<GeneratedEndpoint>();
            if (!TryGetArray(root, "Endpoints", out var array)) return results;

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var entity = GetString(item, "Entity");
                var route = GetString(item, "Route");
                if (string.IsNullOrEmpty(entity) || string.IsNullOrEmpty(route)) continue;

                results.Add(new GeneratedEndpoint
                {
                    Entity = entity!,
                    Route = route!,
                    Methods = GetStringArray(item, "Methods")
                });
            }

            return results;
        }

        private static List<GeneratedCustomEndpoint> ReadCustomEndpoints(JsonElement root)
        {
            var results = new List<GeneratedCustomEndpoint>();
            if (!TryGetArray(root, "CustomEndpoints", out var array)) return results;

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var route = GetString(item, "Route");
                var method = GetString(item, "Method");
                var requestType = GetString(item, "RequestType");

                if (string.IsNullOrEmpty(route) || string.IsNullOrEmpty(method) || string.IsNullOrEmpty(requestType))
                    continue;

                results.Add(new GeneratedCustomEndpoint
                {
                    Route = route!,
                    Method = method!,
                    RequestType = requestType!,
                    Roles = GetStringArray(item, "Roles")
                });
            }

            return results;
        }

        private static bool TryGetArray(JsonElement element, string name, out JsonElement array)
        {
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out array)
                && array.ValueKind == JsonValueKind.Array)
            {
                return true;
            }

            array = default;
            return false;
        }

        private static string? GetString(JsonElement element, string name)
            => element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        /// <summary>
        /// Reads a string array, tolerating the object form used by manifest fields such as
        /// <c>Roles</c>, where roles are keyed by HTTP method.
        /// </summary>
        private static List<string> GetStringArray(JsonElement element, string name)
        {
            var results = new List<string>();
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
                return results;

            switch (value.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in value.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String)
                            results.Add(item.GetString()!);
                    break;

                case JsonValueKind.Object:
                    foreach (var property in value.EnumerateObject())
                        foreach (var item in property.Value.EnumerateArray())
                            if (item.ValueKind == JsonValueKind.String)
                                results.Add(item.GetString()!);
                    break;
            }

            return results;
        }


        private static string GenerateServicesCode(string ns, List<GeneratedEndpoint> endpoints)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#nullable enable");
            sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
            sb.AppendLine("using MediatR;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using MongoDB.Bson;");
            sb.AppendLine("using Foundry.Core.Paging;");
            sb.AppendLine("using Foundry.Api.MediatR;");
            sb.AppendLine();
            sb.AppendLine("namespace Foundry.Api.Endpoints;");
            sb.AppendLine();
            sb.AppendLine("public static class GeneratedServices");
            sb.AppendLine("{");
            sb.AppendLine("    public static IServiceCollection AddGeneratedHandlers(this IServiceCollection services)");
            sb.AppendLine("    {");

            foreach (var ep in endpoints)
            {
                var fullEntityType = $"{ns}.{ep.Entity}";
                sb.AppendLine($"        // DI Registrations for {ep.Entity}");
                sb.AppendLine($"        services.AddTransient<IRequestHandler<InsertCommand<{fullEntityType}>, {fullEntityType}>, InsertCommandHandler<{fullEntityType}>>();");
                sb.AppendLine($"        services.AddTransient<IRequestHandler<UpdateCommand<{fullEntityType}>, {fullEntityType}>, UpdateCommandHandler<{fullEntityType}>>();");
                sb.AppendLine($"        services.AddTransient<IRequestHandler<DeleteCommand<{fullEntityType}>, bool>, DeleteCommandHandler<{fullEntityType}>>();");
                sb.AppendLine($"        services.AddTransient<IRequestHandler<GetByIdQuery<{fullEntityType}>, {fullEntityType}?>, GetByIdQueryHandler<{fullEntityType}>>();");
                sb.AppendLine($"        services.AddTransient<IRequestHandler<FindManyQuery<{fullEntityType}>, IReadOnlyList<{fullEntityType}>>, FindManyQueryHandler<{fullEntityType}>>();");
                sb.AppendLine($"        services.AddTransient<IRequestHandler<SearchPagedQuery<{fullEntityType}>, PagedResult<{fullEntityType}>>, SearchPagedQueryHandler<{fullEntityType}>>();");
                sb.AppendLine();
            }

            sb.AppendLine("        return services;");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static string GenerateEndpointsCode(string ns, List<GeneratedEndpoint> endpoints, List<GeneratedCustomEndpoint> customEndpoints)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#nullable enable");
            sb.AppendLine("using Microsoft.AspNetCore.Builder;");
            sb.AppendLine("using Microsoft.AspNetCore.Http;");
            sb.AppendLine("using Microsoft.AspNetCore.Routing;");
            sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
            sb.AppendLine("using System.Text.Json;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using MediatR;");
            sb.AppendLine("using MongoDB.Bson;");
            sb.AppendLine("using Foundry.Api.Manifest;");
            sb.AppendLine("using Foundry.Api.MediatR;");
            sb.AppendLine("using Foundry.Core.Search;");
            sb.AppendLine();
            sb.AppendLine("namespace Foundry.Api.Endpoints;");
            sb.AppendLine();
            sb.AppendLine("public static class GeneratedEndpoints");
            sb.AppendLine("{");
            sb.AppendLine("    public static IEndpointRouteBuilder MapGeneratedEndpoints(this IEndpointRouteBuilder endpoints, ApiManifest manifest)");
            sb.AppendLine("    {");
            sb.AppendLine("        // Every endpoint below requires an authenticated caller. Fail at startup if nothing");
            sb.AppendLine("        // can authenticate one, rather than with a 500 from inside the framework on the");
            sb.AppendLine("        // first request to an endpoint whose source looks correct.");
            sb.AppendLine("        Foundry.Api.Security.GeneratedEndpointSecurityGuard.EnsureAuthenticationIsConfigured(endpoints.ServiceProvider);");
            sb.AppendLine();

            foreach (var ep in endpoints)
            {
                var fullEntityType = $"{ns}.{ep.Entity}";
                sb.AppendLine($"        // Endpoint Config for {ep.Entity}");

                // Each entity's registrations go in their own block. The per-method locals below
                // (builderGet, builderPost, ...) are not named per entity, so a manifest with more
                // than one entity previously emitted duplicate declarations in a single scope and
                // failed to compile with CS0128. Every shipped manifest and test fixture had
                // exactly one entity, so the generated REST layer had never been built with two.
                sb.AppendLine("        {");
                sb.AppendLine($"            var config_{ep.Entity} = manifest.Endpoints.Find(e => e.Entity == \"{ep.Entity}\")!;");
                sb.AppendLine();

                foreach (var method in ep.Methods)
                {
                    if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"            var builderPost = endpoints.MapPost(\"{ep.Route}\", async ({fullEntityType} entity, HttpContext context, ISender sender) =>");
                        sb.AppendLine("            {");
                        sb.AppendLine($"                var command = new InsertCommand<{fullEntityType}>(entity);");
                        sb.AppendLine("                var result = await sender.Send(command, context.RequestAborted);");
                        sb.AppendLine($"                context.Response.Headers.Location = \"{ep.Route}/\" + ((dynamic)result).Id;");
                        sb.AppendLine($"                return Results.Text(JsonSerializer.Serialize(result, Foundry.Core.Serialization.FoundryJsonDefaults.Options), \"application/json\", statusCode: 201);");
                        sb.AppendLine("            });");
                        sb.AppendLine($"            ConfigureMetadata(builderPost, config_{ep.Entity}, \"POST\", typeof({fullEntityType}), 201);");
                    }
                    else if (method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"            var builderPut = endpoints.MapPut(\"{ep.Route}/{{id}}\", async (string id, {fullEntityType} entity, HttpContext context, ISender sender) =>");
                        sb.AppendLine("            {");
                        sb.AppendLine("                if (!ObjectId.TryParse(id, out var objectId)) return Results.BadRequest(\"Invalid ObjectId.\");");
                        sb.AppendLine("                var updatedEntity = entity with { Id = objectId };");
                        sb.AppendLine($"                var command = new UpdateCommand<{fullEntityType}>(updatedEntity);");
                        sb.AppendLine("                var result = await sender.Send(command, context.RequestAborted);");
                        sb.AppendLine($"                return Results.Text(JsonSerializer.Serialize(result, Foundry.Core.Serialization.FoundryJsonDefaults.Options), \"application/json\", statusCode: 200);");
                        sb.AppendLine("            });");
                        sb.AppendLine($"            ConfigureMetadata(builderPut, config_{ep.Entity}, \"PUT\", typeof({fullEntityType}), 200);");
                    }
                    else if (method.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"            var builderDelete = endpoints.MapDelete(\"{ep.Route}/{{id}}\", async (string id, HttpContext context, ISender sender, Foundry.Core.User.ICurrentUserContext userContext) =>");
                        sb.AppendLine("            {");
                        sb.AppendLine("                if (!ObjectId.TryParse(id, out var objectId)) return Results.BadRequest(\"Invalid ObjectId.\");");
                        sb.AppendLine($"                var command = new DeleteCommand<{fullEntityType}>(objectId, userContext.OperatorId ?? string.Empty);");
                        sb.AppendLine("                await sender.Send(command, context.RequestAborted);");
                        sb.AppendLine("                return Results.NoContent();");
                        sb.AppendLine("            });");
                        sb.AppendLine($"            ConfigureMetadata(builderDelete, config_{ep.Entity}, \"DELETE\", typeof({fullEntityType}), 204);");
                    }
                    else if (method.Equals("GET_BY_ID", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"            var builderGetId = endpoints.MapGet(\"{ep.Route}/{{id}}\", async (string id, HttpContext context, ISender sender) =>");
                        sb.AppendLine("            {");
                        sb.AppendLine("                if (!ObjectId.TryParse(id, out var objectId)) return Results.BadRequest(\"Invalid ObjectId.\");");
                        sb.AppendLine($"                var query = new GetByIdQuery<{fullEntityType}>(objectId);");
                        sb.AppendLine("                var result = await sender.Send(query, context.RequestAborted);");
                        sb.AppendLine("                return result != null ? Results.Text(JsonSerializer.Serialize(result, Foundry.Core.Serialization.FoundryJsonDefaults.Options), \"application/json\") : Results.NotFound();");
                        sb.AppendLine("            });");
                        sb.AppendLine($"            ConfigureMetadata(builderGetId, config_{ep.Entity}, \"GET_BY_ID\", typeof({fullEntityType}), 200);");
                    }
                    else if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"            var builderGet = endpoints.MapGet(\"{ep.Route}\", async (HttpContext context, ISender sender) =>");
                        sb.AppendLine("            {");
                        sb.AppendLine("                var sortBy = context.Request.Query[\"sortBy\"].ToString();");
                        sb.AppendLine("                var limitStr = context.Request.Query[\"limit\"].ToString();");
                        sb.AppendLine("                var limit = int.TryParse(limitStr, out var parsedLimit) ? parsedLimit : 100;");
                        sb.AppendLine("                var sortOrder = string.Equals(context.Request.Query[\"sortOrder\"].ToString(), \"asc\", System.StringComparison.OrdinalIgnoreCase) || string.Equals(context.Request.Query[\"sortOrder\"].ToString(), \"ascending\", System.StringComparison.OrdinalIgnoreCase) ? Foundry.Core.Paging.SortOrder.Ascending : Foundry.Core.Paging.SortOrder.Descending;");
                        sb.AppendLine();
                        sb.AppendLine("                // Advanced Criteria Support");
                        sb.AppendLine("                var criteriaJson = context.Request.Query[\"criteria\"].ToString();");
                        sb.AppendLine("                SearchCriterion[]? criteria = null;");
                        sb.AppendLine("                if (!string.IsNullOrEmpty(criteriaJson))");
                        sb.AppendLine("                {");
                        // A malformed `criteria` is rejected, not ignored.
                        //
                        // This emitted `} catch {}` into every generated GET endpoint, so unparseable
                        // criteria left `criteria` null and the query ran *without the caller's
                        // filter* -- returning a 200 with the full, unfiltered result set and no
                        // indication the filter had been dropped. Silently widening a result set is
                        // the worst direction to fail in a multi-tenant framework.
                        sb.AppendLine("                    try {");
                        sb.AppendLine("                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };");
                        sb.AppendLine("                        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());");
                        sb.AppendLine("                        criteria = JsonSerializer.Deserialize<SearchCriterion[]>(criteriaJson, options);");
                        sb.AppendLine("                    }");
                        sb.AppendLine("                    catch (JsonException ex)");
                        sb.AppendLine("                    {");
                        sb.AppendLine("                        return Results.Problem(");
                        sb.AppendLine("                            detail: \"The 'criteria' query parameter is not valid JSON: \" + ex.Message,");
                        sb.AppendLine("                            statusCode: StatusCodes.Status400BadRequest,");
                        sb.AppendLine("                            title: \"Invalid search criteria\");");
                        sb.AppendLine("                    }");
                        sb.AppendLine("                }");
                        sb.AppendLine();
                        sb.AppendLine($"                var filterExpr = GeneratedFilterBuilders.BuildFilterExpression<{fullEntityType}>(context) ?? DynamicEndpointRouteBuilder.BuildFilterExpression<{fullEntityType}>(context);");
                        sb.AppendLine($"                var query = new FindManyQuery<{fullEntityType}>(filterExpr, sortBy, sortOrder, limit, criteria);");
                        sb.AppendLine("                var result = await sender.Send(query, context.RequestAborted);");
                        sb.AppendLine("                return Results.Text(JsonSerializer.Serialize(result, Foundry.Core.Serialization.FoundryJsonDefaults.Options), \"application/json\");");
                        sb.AppendLine("            });");
                        sb.AppendLine($"            ConfigureMetadata(builderGet, config_{ep.Entity}, \"GET\", typeof({fullEntityType}), 200);");
                    }
                }

                sb.AppendLine("        }");
                sb.AppendLine();
            }

            // Map custom endpoints at compile-time
            foreach (var customEp in customEndpoints)
            {
                var method = customEp.Method.ToUpperInvariant();
                sb.AppendLine($"            // Custom Endpoint: {customEp.Route} -> {customEp.RequestType}");
                if (method == "GET" || method == "DELETE")
                {
                    sb.AppendLine($"            var builder_{customEp.RequestType} = endpoints.MapMethods(\"{customEp.Route}\", new[] {{ \"{method}\" }}, async (HttpContext context, ISender sender) =>");
                    sb.AppendLine("            {");
                    sb.AppendLine($"                var command = new {ns}.{customEp.RequestType}();");
                    sb.AppendLine("                var result = await sender.Send(command, context.RequestAborted);");
                    sb.AppendLine("                if (result == null) return Results.NoContent();");
                    sb.AppendLine("                return Results.Text(JsonSerializer.Serialize(result, Foundry.Core.Serialization.FoundryJsonDefaults.Options), \"application/json\");");
                    sb.AppendLine("            });");
                }
                else
                {
                    sb.AppendLine($"            var builder_{customEp.RequestType} = endpoints.MapMethods(\"{customEp.Route}\", new[] {{ \"{method}\" }}, async ({ns}.{customEp.RequestType} command, HttpContext context, ISender sender) =>");
                    sb.AppendLine("            {");
                    sb.AppendLine("                var result = await sender.Send(command, context.RequestAborted);");
                    sb.AppendLine("                if (result == null) return Results.NoContent();");
                    sb.AppendLine("                return Results.Text(JsonSerializer.Serialize(result, Foundry.Core.Serialization.FoundryJsonDefaults.Options), \"application/json\");");
                    sb.AppendLine("            });");
                }

                sb.AppendLine($"            var config_{customEp.RequestType} = new EndpointConfig");
                sb.AppendLine("            {");
                sb.AppendLine($"                Route = \"{customEp.Route}\",");
                sb.AppendLine($"                Entity = \"{customEp.RequestType}\",");
                sb.AppendLine($"                Methods = new List<string> {{ \"{method}\" }},");
                sb.AppendLine($"                Roles = new Dictionary<string, List<string>> {{ {{ \"{method}\", new List<string> {{ {string.Join(", ", customEp.Roles.Select(r => $"\"{r}\""))} }} }} }}");
                sb.AppendLine("            };");
                sb.AppendLine($"            RequireDeclaredRoles(builder_{customEp.RequestType}, config_{customEp.RequestType}.Roles[\"{method}\"]);");
                sb.AppendLine($"            builder_{customEp.RequestType}.WithMetadata(config_{customEp.RequestType})");
                sb.AppendLine($"                         .WithName(\"{method}_{customEp.RequestType}\")");
                sb.AppendLine($"                         .WithTags(\"{customEp.RequestType}\")");
                sb.AppendLine("                         .Produces(200)");
                sb.AppendLine("                         .Produces(400, typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))");
                sb.AppendLine("                         .Produces(401)");
                sb.AppendLine("                         .Produces(403, typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))");
                sb.AppendLine("                         .Produces(500, typeof(Microsoft.AspNetCore.Mvc.ProblemDetails));");
            }

            sb.AppendLine("        return endpoints;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>Applies the access the schema declared for an endpoint.</summary>");
            sb.AppendLine("    /// <remarks>");
            sb.AppendLine("    /// These roles were previously interpolated into the endpoint's OpenAPI description and");
            sb.AppendLine("    /// nowhere else -- and the description defaulted to \"Requires roles: Admin\" when the schema");
            sb.AppendLine("    /// declared none. Every generated endpoint was anonymous while documenting the roles it");
            sb.AppendLine("    /// required and advertising the 401 and 403 responses it could never return.");
            sb.AppendLine("    ///");
            sb.AppendLine("    /// An endpoint with no declared roles requires an authenticated caller rather than a");
            sb.AppendLine("    /// particular role: the schema author expressed no policy, and anonymous is not a policy.");
            sb.AppendLine("    /// </remarks>");
            sb.AppendLine("    private static void RequireDeclaredRoles(RouteHandlerBuilder builder, List<string>? declaredRoles)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (declaredRoles is { Count: > 0 })");
            sb.AppendLine("        {");
            sb.AppendLine("            builder.RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute");
            sb.AppendLine("            {");
            sb.AppendLine("                Roles = string.Join(\",\", declaredRoles)");
            sb.AppendLine("            });");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        builder.RequireAuthorization();");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    private static void ConfigureMetadata(RouteHandlerBuilder builder, EndpointConfig config, string method, Type entityType, int successStatusCode)");
            sb.AppendLine("    {");
            sb.AppendLine("        var declaredRoles = config.Roles != null && config.Roles.TryGetValue(method, out var roles) && roles.Count > 0");
            sb.AppendLine("            ? roles");
            sb.AppendLine("            : null;");
            sb.AppendLine();
            sb.AppendLine("        RequireDeclaredRoles(builder, declaredRoles);");
            sb.AppendLine();
            sb.AppendLine("        var rolesStr = declaredRoles != null");
            sb.AppendLine("            ? string.Join(\", \", declaredRoles)");
            sb.AppendLine("            : \"any authenticated caller\";");
            sb.AppendLine("        string summary = $\"{(method == \"GET_BY_ID\" ? \"Fetch by ID\" : method == \"GET\" ? \"List and Search\" : method == \"POST\" ? \"Insert new\" : method == \"PUT\" ? \"Update existing\" : \"Delete\")} endpoint for {entityType.Name} collection\";");
            sb.AppendLine("        builder.WithMetadata(config)");
            sb.AppendLine("               .WithName($\"{method}_{entityType.Name}\")");
            sb.AppendLine("               .WithTags(entityType.Name)");
            sb.AppendLine("               .WithSummary(summary)");
            sb.AppendLine("               .WithDescription($\"Access {entityType.Name} documents. Requires roles: {rolesStr}\")");
            sb.AppendLine("               .Produces(successStatusCode, entityType)");
            sb.AppendLine("               .Produces(400, typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))");
            sb.AppendLine("               .Produces(401)");
            sb.AppendLine("               .Produces(403, typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))");
            sb.AppendLine("               .Produces(500, typeof(Microsoft.AspNetCore.Mvc.ProblemDetails));");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static int FindClosingBracket(string text, int startIndex, char openChar, char closeChar)
        {
            int depth = 0;
            for (int i = startIndex; i < text.Length; i++)
            {
                if (text[i] == openChar)
                {
                    depth++;
                }
                else if (text[i] == closeChar)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }
            return -1;
        }

        private static string ExtractValue(string json, string key)
        {
            var search = $"\"{key}\"";
            var idx = json.IndexOf(search);
            if (idx == -1) return string.Empty;
            var colonIdx = json.IndexOf(":", idx + search.Length);
            if (colonIdx == -1) return string.Empty;
            var startQuote = json.IndexOf("\"", colonIdx + 1);
            if (startQuote == -1) return string.Empty;
            var endQuote = json.IndexOf("\"", startQuote + 1);
            if (endQuote == -1) return string.Empty;
            return json.Substring(startQuote + 1, endQuote - startQuote - 1);
        }

        private static List<string> ExtractArrayValues(string json, string key)
        {
            var list = new List<string>();
            var search = $"\"{key}\"";
            var idx = json.IndexOf(search);
            if (idx == -1) return list;
            var colonIdx = json.IndexOf(":", idx + search.Length);
            if (colonIdx == -1) return list;
            var startBracket = json.IndexOf("[", colonIdx + 1);
            if (startBracket == -1) return list;
            var endBracket = FindClosingBracket(json, startBracket, '[', ']');
            if (endBracket == -1) return list;

            var arrayContent = json.Substring(startBracket + 1, endBracket - startBracket - 1);
            var items = arrayContent.Split(',');
            foreach (var item in items)
            {
                var trimmed = item.Trim().Trim('"');
                if (!string.IsNullOrEmpty(trimmed))
                {
                    list.Add(trimmed);
                }
            }
            return list;
        }

    private static string GenerateFilterBuildersCode(Compilation compilation, string ns, List<GeneratedEndpoint> endpoints)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Linq.Expressions;");
        sb.AppendLine("using Microsoft.AspNetCore.Http;");
        sb.AppendLine("using MongoDB.Bson;");
        sb.AppendLine();
        sb.AppendLine("namespace Foundry.Api.Endpoints;");
        sb.AppendLine();
        sb.AppendLine("public static class GeneratedFilterBuilders");
        sb.AppendLine("{");
        sb.AppendLine("    public static Expression<Func<T, bool>>? BuildFilterExpression<T>(HttpContext context) where T : class");
        sb.AppendLine("    {");
        foreach (var ep in endpoints)
        {
            var fullEntityType = $"{ns}.{ep.Entity}";
            sb.AppendLine($"        if (typeof(T) == typeof({fullEntityType}))");
            sb.AppendLine("        {");
            sb.AppendLine($"            return (Expression<Func<T, bool>>?)(object?)Build_{ep.Entity}_Filter(context);");
            sb.AppendLine("        }");
        }
        sb.AppendLine("        return null;");
        sb.AppendLine("    }");
        sb.AppendLine();

        foreach (var ep in endpoints)
        {
            var fullEntityType = $"{ns}.{ep.Entity}";
            sb.AppendLine($"    private static Expression<Func<{fullEntityType}, bool>>? Build_{ep.Entity}_Filter(HttpContext context)");
            sb.AppendLine("    {");
            sb.AppendLine("        var query = context.Request.Query;");
            sb.AppendLine("        if (query.Count == 0) return null;");
            sb.AppendLine();
            sb.AppendLine($"        var parameter = Expression.Parameter(typeof({fullEntityType}), \"x\");");
            sb.AppendLine("        Expression? body = null;");
            sb.AppendLine();

            var typeSymbol = compilation.GetTypeByMetadataName(fullEntityType);
            if (typeSymbol != null)
            {
                var properties = typeSymbol.GetMembers()
                    .OfType<IPropertySymbol>()
                    .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic && !p.IsWriteOnly);

                foreach (var prop in properties)
                {
                    var propName = prop.Name;
                    var propTypeStr = prop.Type.ToDisplayString();

                    if (propName == "Id" || propName == "CreatedAtUtc" || propName == "UpdatedAtUtc" || propName == "Version" || propName == "IsDeleted")
                        continue;

                    sb.AppendLine($"        if (query.TryGetValue(\"{propName}\", out var val_{propName}))");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            var stringVal = val_{propName}.ToString();");

                    if (propTypeStr == "string" || propTypeStr == "System.String")
                    {
                        sb.AppendLine($"            var propExpr = Expression.Property(parameter, \"{propName}\");");
                        sb.AppendLine($"            var valExpr = Expression.Constant(stringVal, typeof(string));");
                        sb.AppendLine("            var eqExpr = Expression.Equal(propExpr, valExpr);");
                        sb.AppendLine("            body = body == null ? eqExpr : Expression.AndAlso(body, eqExpr);");
                    }
                    else if (propTypeStr == "MongoDB.Bson.ObjectId" || propTypeStr == "ObjectId")
                    {
                        sb.AppendLine($"            if (MongoDB.Bson.ObjectId.TryParse(stringVal, out var parsed_{propName}))");
                        sb.AppendLine("            {");
                        sb.AppendLine($"                var propExpr = Expression.Property(parameter, \"{propName}\");");
                        sb.AppendLine($"                var valExpr = Expression.Constant(parsed_{propName}, typeof(MongoDB.Bson.ObjectId));");
                        sb.AppendLine("                var eqExpr = Expression.Equal(propExpr, valExpr);");
                        sb.AppendLine("                body = body == null ? eqExpr : Expression.AndAlso(body, eqExpr);");
                        sb.AppendLine("            }");
                    }
                    else if (propTypeStr == "int" || propTypeStr == "System.Int32")
                    {
                        sb.AppendLine($"            if (int.TryParse(stringVal, out var parsed_{propName}))");
                        sb.AppendLine("            {");
                        sb.AppendLine($"                var propExpr = Expression.Property(parameter, \"{propName}\");");
                        sb.AppendLine($"                var valExpr = Expression.Constant(parsed_{propName}, typeof(int));");
                        sb.AppendLine("                var eqExpr = Expression.Equal(propExpr, valExpr);");
                        sb.AppendLine("                body = body == null ? eqExpr : Expression.AndAlso(body, eqExpr);");
                        sb.AppendLine("            }");
                    }
                    else if (propTypeStr == "decimal" || propTypeStr == "System.Decimal")
                    {
                        sb.AppendLine($"            if (decimal.TryParse(stringVal, out var parsed_{propName}))");
                        sb.AppendLine("            {");
                        sb.AppendLine($"                var propExpr = Expression.Property(parameter, \"{propName}\");");
                        sb.AppendLine($"                var valExpr = Expression.Constant(parsed_{propName}, typeof(decimal));");
                        sb.AppendLine("                var eqExpr = Expression.Equal(propExpr, valExpr);");
                        sb.AppendLine("                body = body == null ? eqExpr : Expression.AndAlso(body, eqExpr);");
                        sb.AppendLine("            }");
                    }
                    else if (propTypeStr == "bool" || propTypeStr == "System.Boolean")
                    {
                        sb.AppendLine($"            if (bool.TryParse(stringVal, out var parsed_{propName}))");
                        sb.AppendLine("            {");
                        sb.AppendLine($"                var propExpr = Expression.Property(parameter, \"{propName}\");");
                        sb.AppendLine($"                var valExpr = Expression.Constant(parsed_{propName}, typeof(bool));");
                        sb.AppendLine("                var eqExpr = Expression.Equal(propExpr, valExpr);");
                        sb.AppendLine("                body = body == null ? eqExpr : Expression.AndAlso(body, eqExpr);");
                        sb.AppendLine("            }");
                    }
                    else if (prop.Type.TypeKind == TypeKind.Enum)
                    {
                        sb.AppendLine($"            if (Enum.TryParse<{propTypeStr}>(stringVal, true, out var parsed_{propName}))");
                        sb.AppendLine("            {");
                        sb.AppendLine($"                var propExpr = Expression.Property(parameter, \"{propName}\");");
                        sb.AppendLine($"                var valExpr = Expression.Constant(parsed_{propName}, typeof({propTypeStr}));");
                        sb.AppendLine("                var eqExpr = Expression.Equal(propExpr, valExpr);");
                        sb.AppendLine("                body = body == null ? eqExpr : Expression.AndAlso(body, eqExpr);");
                        sb.AppendLine("            }");
                    }
                    sb.AppendLine("        }");
                }
            }

            sb.AppendLine();
            sb.AppendLine("        if (body == null) return null;");
            sb.AppendLine($"        return Expression.Lambda<Func<{fullEntityType}, bool>>(body, parameter);");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    }

    internal class GeneratedEndpoint
    {
        public string Entity { get; set; } = string.Empty;
        public string Route { get; set; } = string.Empty;
        public List<string> Methods { get; set; } = new();
    }

    internal class GeneratedCustomEndpoint
    {
        public string Route { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public string RequestType { get; set; } = string.Empty;
        public List<string> Roles { get; set; } = new();
    }
}
