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
