using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Foundry.Schema.Compiler.Exporters;

/// <summary>
/// Exporter that generates a Postman Collection v2.1.0 JSON file for schema endpoints.
/// </summary>
public static class PostmanExporter
{
    public static string ExportJson(SchemaModel schema)
    {
        var items = new List<object>();

        if (schema.Entities != null)
        {
            foreach (var entity in schema.Entities)
            {
                var entityName = entity.Name;
                var routePath = $"/api/v1/{entityName.ToLowerInvariant()}";

                var entityRequests = new List<object>
                {
                    new
                    {
                        name = $"Get All {entityName}s",
                        request = new
                        {
                            method = "GET",
                            header = new[] { new { key = "X-Tenant-ID", value = "{{tenantId}}" } },
                            url = new
                            {
                                raw = $"{{{{baseUrl}}}}{routePath}",
                                host = new[] { "{{baseUrl}}" },
                                path = routePath.Split('/').Where(s => !string.IsNullOrEmpty(s)).ToArray()
                            }
                        }
                    },
                    new
                    {
                        name = $"Create {entityName}",
                        request = new
                        {
                            method = "POST",
                            header = new[]
                            {
                                new { key = "Content-Type", value = "application/json" },
                                new { key = "X-Tenant-ID", value = "{{tenantId}}" }
                            },
                            body = new
                            {
                                mode = "raw",
                                raw = "{\n  \"sampleField\": \"value\"\n}"
                            },
                            url = new
                            {
                                raw = $"{{{{baseUrl}}}}{routePath}",
                                host = new[] { "{{baseUrl}}" },
                                path = routePath.Split('/').Where(s => !string.IsNullOrEmpty(s)).ToArray()
                            }
                        }
                    }
                };

                items.Add(new
                {
                    name = entityName,
                    item = entityRequests
                });
            }
        }

        var collection = new
        {
            info = new
            {
                name = $"{schema.Namespace} Postman Collection",
                schema = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
            },
            item = items,
            variable = new[]
            {
                new { key = "baseUrl", value = "http://localhost:5000" },
                new { key = "tenantId", value = "tenant-demo" }
            }
        };

        return JsonSerializer.Serialize(collection, new JsonSerializerOptions { WriteIndented = true });
    }
}
