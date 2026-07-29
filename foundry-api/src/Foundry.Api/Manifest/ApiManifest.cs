using System;
using System.Collections.Generic;
using Foundry.Rules;

namespace Foundry.Api.Manifest;

public class ApiManifest
{
    public string Namespace { get; set; } = string.Empty;
    public List<EndpointConfig> Endpoints { get; set; } = new();
    public List<CustomEndpointConfig> CustomEndpoints { get; set; } = new();
    public List<WorkflowConfig> Workflows { get; set; } = new();
}

public class EndpointConfig
{
    public string Route { get; set; } = string.Empty;
    public string Entity { get; set; } = string.Empty;
    public List<string> Methods { get; set; } = new();
    public Dictionary<string, List<string>> Roles { get; set; } = new();
    public Dictionary<string, CachingConfig> Caching { get; set; } = new();
    public Dictionary<string, List<string>> BusinessRules { get; set; } = new();

    /// <summary>
    /// Whether this entity is exposed over GraphQL as well as REST.
    /// </summary>
    /// <remarks>
    /// The schema has always had <c>enableGraphQL</c> per entity, and it reached nothing that runs:
    /// the manifest did not carry it, so <c>AddDynamicGraphQL</c> exposed every entity that declared
    /// a GET and an entity that opted <em>out</em> was served over GraphQL regardless. The flag's
    /// only effect was to make the compiler emit a second, rival GraphQL surface that did not
    /// compile. Carrying it here is what makes the declaration mean something.
    /// </remarks>
    public bool GraphQL { get; set; }
}

public class CachingConfig
{
    public bool Enabled { get; set; }
    public int TtlSeconds { get; set; }
}

public class CustomEndpointConfig
{
    public string Route { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string RequestType { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();
    public List<string> BusinessRules { get; set; } = new();
}
