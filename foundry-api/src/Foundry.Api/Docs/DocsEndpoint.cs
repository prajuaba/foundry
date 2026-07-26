#pragma warning disable IL2026, IL3050, IL2075, IL2090, IL2070, IL2060
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using System.Diagnostics.CodeAnalysis;
using Foundry.Core.Entities;
using Foundry.Api.Manifest;

namespace Foundry.Api.Docs;

public static class DocsEndpoint
{
    [RequiresUnreferencedCode("Uses runtime reflection for mapping api schema documentation.")]
    [RequiresDynamicCode("Uses runtime dynamic code or generics.")]
    public static IEndpointRouteBuilder MapDocsEndpoint(this IEndpointRouteBuilder endpoints, ApiManifest manifest)
    {
        endpoints.MapGet("/docs/spec", (HttpContext context) =>
        {
            var html = GenerateSpecHtml(manifest);
            context.Response.ContentType = "text/html; charset=utf-8";
            return context.Response.WriteAsync(html);
        });

        return endpoints;
    }

    private static string GenerateSpecHtml(ApiManifest manifest)
    {
        var sb = new StringBuilder();
        sb.Append(@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Foundry.API Dynamic Spec</title>
    <link href=""https://fonts.googleapis.com/css2?family=Inter:wght@300;400;500;600;700&family=Outfit:wght@400;500;600;700&display=swap"" rel=""stylesheet"">
    <style>
        :root {
            --bg-primary: #0b0f19;
            --bg-secondary: #151d30;
            --bg-tertiary: #1e293b;
            --text-primary: #f8fafc;
            --text-secondary: #94a3b8;
            --accent-blue: #3b82f6;
            --accent-purple: #8b5cf6;
            --accent-green: #10b981;
            --accent-red: #ef4444;
            --accent-orange: #f59e0b;
            --border-color: #334155;
        }

        * {
            box-sizing: border-box;
            margin: 0;
            padding: 0;
        }

        body {
            font-family: 'Inter', sans-serif;
            background-color: var(--bg-primary);
            color: var(--text-primary);
            padding: 2rem;
            line-height: 1.6;
        }

        header {
            max-width: 1200px;
            margin: 0 auto 3rem auto;
            border-bottom: 1px solid var(--border-color);
            padding-bottom: 1.5rem;
        }

        h1 {
            font-family: 'Outfit', sans-serif;
            font-size: 2.5rem;
            background: linear-gradient(135deg, #a78bfa, #3b82f6);
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
            margin-bottom: 0.5rem;
        }

        .subtitle {
            color: var(--text-secondary);
            font-size: 1.1rem;
        }

        main {
            max-width: 1200px;
            margin: 0 auto;
            display: flex;
            flex-direction: column;
            gap: 3rem;
        }

        .entity-card {
            background-color: var(--bg-secondary);
            border: 1px solid var(--border-color);
            border-radius: 0.75rem;
            padding: 2rem;
            box-shadow: 0 10px 15px -3px rgba(0, 0, 0, 0.3);
        }

        .entity-header {
            display: flex;
            align-items: center;
            justify-content: space-between;
            margin-bottom: 1.5rem;
            border-bottom: 1px dashed var(--border-color);
            padding-bottom: 1rem;
        }

        h2 {
            font-family: 'Outfit', sans-serif;
            font-size: 1.8rem;
            color: #f1f5f9;
        }

        .entity-badges {
            display: flex;
            gap: 0.5rem;
        }

        .badge {
            font-size: 0.75rem;
            font-weight: 600;
            padding: 0.25rem 0.6rem;
            border-radius: 0.25rem;
            text-transform: uppercase;
            letter-spacing: 0.05em;
        }

        .badge-softdelete { background-color: rgba(239, 68, 68, 0.15); color: var(--accent-red); border: 1px solid rgba(239, 68, 68, 0.3); }
        .badge-auditable { background-color: rgba(16, 185, 129, 0.15); color: var(--accent-green); border: 1px solid rgba(16, 185, 129, 0.3); }
        .badge-versionable { background-color: rgba(139, 92, 246, 0.15); color: var(--accent-purple); border: 1px solid rgba(139, 92, 246, 0.3); }

        .section-title {
            font-family: 'Outfit', sans-serif;
            font-size: 1.2rem;
            margin: 1.5rem 0 0.75rem 0;
            color: #cbd5e1;
            display: flex;
            align-items: center;
            gap: 0.5rem;
        }

        .section-title::after {
            content: '';
            flex-grow: 1;
            height: 1px;
            background-color: var(--border-color);
        }

        table {
            width: 100%;
            border-collapse: collapse;
            margin-bottom: 1.5rem;
            text-align: left;
        }

        th {
            background-color: var(--bg-tertiary);
            color: #e2e8f0;
            font-weight: 600;
            padding: 0.75rem 1rem;
            font-size: 0.875rem;
            border: 1px solid var(--border-color);
        }

        td {
            padding: 0.75rem 1rem;
            font-size: 0.875rem;
            border: 1px solid var(--border-color);
            color: var(--text-secondary);
        }

        tr:nth-child(even) {
            background-color: rgba(255, 255, 255, 0.02);
        }

        .prop-name {
            font-family: monospace;
            color: #38bdf8;
            font-weight: 500;
        }

        .prop-type {
            font-family: monospace;
            color: #cbd5e1;
        }

        .route-method {
            font-family: monospace;
            font-weight: 700;
            padding: 0.15rem 0.4rem;
            border-radius: 0.25rem;
            font-size: 0.75rem;
        }

        .method-get { background-color: rgba(59, 130, 246, 0.15); color: var(--accent-blue); }
        .method-post { background-color: rgba(16, 185, 129, 0.15); color: var(--accent-green); }
        .method-put { background-color: rgba(245, 158, 11, 0.15); color: var(--accent-orange); }
        .method-delete { background-color: rgba(239, 68, 68, 0.15); color: var(--accent-red); }

        .role-tag {
            background-color: var(--bg-tertiary);
            color: #cbd5e1;
            padding: 0.15rem 0.4rem;
            border-radius: 0.25rem;
            font-size: 0.75rem;
            margin-right: 0.25rem;
            border: 1px solid var(--border-color);
        }

        .cache-badge {
            font-weight: 600;
        }
        .cache-enabled { color: var(--accent-green); }
        .cache-disabled { color: var(--text-secondary); }
    </style>
</head>
<body>
    <header>
        <h1>Foundry.API Dynamic Specification</h1>
        <div class=""subtitle"">Dynamic Execution Orchestrator - Living Audit & Technical Architecture Package</div>
    </header>
    <main>");

        var allTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => {
                try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
            })
            .ToList();

        foreach (var config in manifest.Endpoints)
        {
            var entityTypeName = $"{manifest.Namespace}.{config.Entity}";
            var entityType = allTypes.FirstOrDefault(t => t.FullName?.Equals(entityTypeName, StringComparison.OrdinalIgnoreCase) == true);
            if (entityType == null)
            {
                entityType = allTypes.FirstOrDefault(t => t.Name.Equals(config.Entity, StringComparison.OrdinalIgnoreCase) == true 
                    && typeof(IEntity<ObjectId>).IsAssignableFrom(t));
            }

            if (entityType == null) continue;

            var isSoftDelete = typeof(ISoftDelete).IsAssignableFrom(entityType);
            var isAuditable = entityType.GetInterfaces().Any(i => i.Name.Contains("IAuditable") || i.Name.Contains("Auditable"));
            // Standard BaseEntity has versionable
            var isVersionable = typeof(IVersionable).IsAssignableFrom(entityType) || entityType.GetProperty("Version") != null;

            sb.Append($@"
        <div class=""entity-card"">
            <div class=""entity-header"">
                <h2>{entityType.Name}</h2>
                <div class=""entity-badges"">");
            
            if (isSoftDelete) sb.Append(@"<span class=""badge badge-softdelete"">SoftDelete</span>");
            sb.Append(@"<span class=""badge badge-auditable"">Auditable</span>"); // Default true since Foundry.Mongo Repo audits mutations
            if (isVersionable) sb.Append(@"<span class=""badge badge-versionable"">OCC Versionable</span>");

            sb.Append($@"
                </div>
            </div>

            <div class=""section-title"">Entity Attributes & Data Rules</div>
            <table>
                <thead>
                    <tr>
                        <th>Property</th>
                        <th>Type</th>
                        <th>BSON / Validation Constraints</th>
                        <th>PII Security Boundary</th>
                    </tr>
                </thead>
                <tbody>");

            var properties = entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in properties)
            {
                // Ignore standard soft delete / metadata properties in schema table for clean look
                if (prop.Name == "IsDeleted" || prop.Name == "DeletedAt" || prop.Name == "CreatedAtUtc" || prop.Name == "UpdatedAtUtc" || prop.Name == "Version")
                    continue;

                var typeName = prop.PropertyType.Name;
                if (prop.PropertyType.IsGenericType && prop.PropertyType.GetGenericTypeDefinition() == typeof(Nullable<>))
                {
                    typeName = $"{Nullable.GetUnderlyingType(prop.PropertyType)!.Name}?";
                }

                // Check annotations
                var constraints = new List<string>();
                var security = "Plaintext / Clear";

                if (prop.Name == "Id") constraints.Add("PRIMARY KEY");

                var indexedAttr = prop.GetCustomAttributes().FirstOrDefault(a => a.GetType().Name.Contains("IndexedAttribute") || a.GetType().Name.Contains("Indexed"));
                if (indexedAttr != null)
                {
                    var isUnique = false;
                    var uniqueProp = indexedAttr.GetType().GetProperty("Unique");
                    if (uniqueProp != null) isUnique = (bool)uniqueProp.GetValue(indexedAttr)!;
                    constraints.Add(isUnique ? "UNIQUE INDEX" : "INDEX");
                }

                var textIndexedAttr = prop.GetCustomAttributes().FirstOrDefault(a => a.GetType().Name.Contains("TextIndexedAttribute") || a.GetType().Name.Contains("TextIndexed"));
                if (textIndexedAttr != null) constraints.Add("TEXT INDEX");

                var sensitiveAttr = prop.GetCustomAttributes().FirstOrDefault(a => a.GetType().Name.Contains("SensitiveDataAttribute") || a.GetType().Name.Contains("SensitiveData"));
                if (sensitiveAttr != null)
                {
                    var protectionProp = sensitiveAttr.GetType().GetProperty("Protection");
                    var protection = protectionProp?.GetValue(sensitiveAttr)?.ToString() ?? "Encrypt";
                    if (protection.Contains("Encrypt"))
                    {
                        security = "🔒 AES-256-CBC Encrypted at Rest";
                    }
                    else if (protection.Contains("Mask"))
                    {
                        var maskingProp = sensitiveAttr.GetType().GetProperty("MaskingType");
                        var masking = maskingProp?.GetValue(sensitiveAttr)?.ToString() ?? "Default";
                        security = $"🎭 Masked Data ({masking})";
                    }
                }

                var constraintText = constraints.Count > 0 ? string.Join(", ", constraints) : "None";

                sb.Append($@"
                    <tr>
                        <td class=""prop-name"">{prop.Name}</td>
                        <td class=""prop-type"">{typeName}</td>
                        <td>{constraintText}</td>
                        <td>{security}</td>
                    </tr>");
            }

            sb.Append(@"
                </tbody>
            </table>

            <div class=""section-title"">Active API Endpoint Routing Footprints</div>
            <table>
                <thead>
                    <tr>
                        <th>HTTP Method</th>
                        <th>Semantic Route</th>
                        <th>Required Role Authorization</th>
                        <th>Idempotent Cache Rules</th>
                    </tr>
                </thead>
                <tbody>");

            foreach (var method in config.Methods)
            {
                var upperMethod = method.ToUpperInvariant();
                var badgeClass = upperMethod switch
                {
                    "GET" => "method-get",
                    "GET_BY_ID" => "method-get",
                    "POST" => "method-post",
                    "PUT" => "method-put",
                    "DELETE" => "method-delete",
                    _ => "method-get"
                };

                var methodLabel = upperMethod == "GET_BY_ID" ? "GET" : upperMethod;
                var routePath = upperMethod == "GET_BY_ID" || upperMethod == "PUT" || upperMethod == "DELETE" 
                    ? $"{config.Route}/{{id}}" 
                    : config.Route;

                // Role mapping
                var rolesList = new List<string>();
                if (config.Roles.TryGetValue(upperMethod, out var roles))
                {
                    rolesList = roles;
                }
                var rolesHtml = rolesList.Count > 0 
                    ? string.Join("", rolesList.Select(r => $"<span class=\"role-tag\">{r}</span>"))
                    : "<em>Anonymous</em>";

                // Caching mapping
                var cacheText = "<span class=\"cache-badge cache-disabled\">Disabled</span>";
                if (config.Caching.TryGetValue(upperMethod, out var cacheConfig) && cacheConfig.Enabled)
                {
                    cacheText = $"<span class=\"cache-badge cache-enabled\">Enabled ({cacheConfig.TtlSeconds}s TTL)</span>";
                }

                sb.Append($@"
                    <tr>
                        <td><span class=""route-method {badgeClass}"">{methodLabel}</span></td>
                        <td class=""prop-name"">{routePath}</td>
                        <td>{rolesHtml}</td>
                        <td>{cacheText}</td>
                    </tr>");
            }

            sb.Append(@"
                </tbody>
            </table>
        </div>");
        }

        sb.Append(@"
    </main>
</body>
</html>");

        return sb.ToString();
    }
}
