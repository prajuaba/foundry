using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Foundry.Schema.Compiler.Exporters;

/// <summary>
/// Exporter that generates a Postman Collection v2.1.0 JSON file for schema endpoints.
/// </summary>
/// <remarks>
/// <para>
/// Routes come from <see cref="ApiManifestGenerator"/>. This composed
/// <c>/api/v1/{lowercase-singular}</c> while the application serves <c>/api/{plural}</c>, so every
/// request in the collection 404'd — the collection was, in practice, a list of URLs that did not
/// exist.
/// </para>
/// <para>
/// The <c>POST</c> body was the literal <c>{"sampleField": "value"}</c> for every entity, which no
/// endpoint could accept. A body derived from the entity's own properties is the difference between
/// a collection someone can run and one they have to rewrite before it does anything.
/// </para>
/// </remarks>
public static class PostmanExporter
{
    public static string ExportJson(SchemaModel schema)
    {
        var items = new JsonArray();

        foreach (var entity in schema.Entities ?? new List<Entity>())
        {
            var methods = ApiManifestGenerator.EnabledMethods(entity);
            if (methods.Count == 0) continue;

            var route = ApiManifestGenerator.RouteFor(entity.Name);
            var itemRoute = $"{route}/{{{{{entity.Name.ToLowerInvariant()}Id}}}}";
            var requests = new JsonArray();

            if (methods.Contains("GET"))
            {
                requests.Add(Request($"List {ApiManifestGenerator.Pluralize(entity.Name)}", "GET", route));
            }

            if (methods.Contains("POST"))
            {
                requests.Add(Request($"Create {entity.Name}", "POST", route, BodyFor(entity)));
            }

            if (methods.Contains("GET_BY_ID"))
            {
                requests.Add(Request($"Get {entity.Name} by id", "GET", itemRoute));
            }

            if (methods.Contains("PUT"))
            {
                requests.Add(Request($"Replace {entity.Name}", "PUT", itemRoute, BodyFor(entity)));
            }

            if (methods.Contains("DELETE"))
            {
                requests.Add(Request($"Delete {entity.Name}", "DELETE", itemRoute));
            }

            items.Add(new JsonObject
            {
                ["name"] = entity.Name,
                ["item"] = requests
            });
        }

        var collection = new JsonObject
        {
            ["info"] = new JsonObject
            {
                ["name"] = $"{schema.Namespace} Postman Collection",
                ["schema"] = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
            },
            ["item"] = items,
            ["variable"] = new JsonArray(
                Variable("baseUrl", "http://localhost:5000"),
                Variable("tenantId", "tenant-demo"),
                Variable("token", ""))
        };

        return collection.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject Request(string name, string method, string route, JsonNode? body = null)
    {
        var headers = new JsonArray(
            Header("X-Tenant-ID", "{{tenantId}}"),
            // The generated endpoints refuse anonymous callers, so a collection with no
            // Authorization header is a collection where every request comes back 401.
            Header("Authorization", "Bearer {{token}}"));

        if (body is not null) headers.Add(Header("Content-Type", "application/json"));

        var request = new JsonObject
        {
            ["method"] = method,
            ["header"] = headers,
            ["url"] = Url(route)
        };

        if (body is not null)
        {
            request["body"] = new JsonObject
            {
                ["mode"] = "raw",
                ["raw"] = body.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
            };
        }

        return new JsonObject { ["name"] = name, ["request"] = request };
    }

    private static JsonObject Url(string route) => new()
    {
        ["raw"] = $"{{{{baseUrl}}}}{route}",
        ["host"] = new JsonArray("{{baseUrl}}"),
        ["path"] = new JsonArray(route
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => (JsonNode)segment)
            .ToArray())
    };

    /// <summary>A request body shaped like the entity, with a placeholder value per property.</summary>
    private static JsonObject BodyFor(Entity entity)
    {
        var body = new JsonObject();

        foreach (var property in entity.Properties ?? new List<Property>())
        {
            // The key is server-assigned; sending one is at best ignored.
            if (property.IsKey) continue;

            body[property.Name] = SampleValue(property);
        }

        return body;
    }

    private static JsonNode SampleValue(Property property) => property.Type.ToLowerInvariant() switch
    {
        "int" or "int32" or "long" or "int64" => JsonValue.Create(0),
        "decimal" or "double" or "float" => JsonValue.Create(0.0),
        "bool" or "boolean" => JsonValue.Create(false),
        "datetime" or "datetimeoffset" => JsonValue.Create("1970-01-01T00:00:00Z"),
        _ => JsonValue.Create($"<{property.Name}>")
    };

    private static JsonObject Header(string key, string value)
        => new() { ["key"] = key, ["value"] = value };

    private static JsonObject Variable(string key, string value)
        => new() { ["key"] = key, ["value"] = value };
}
