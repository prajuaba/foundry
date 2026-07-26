using System;
using System.Collections.Generic;

namespace Foundry.Connectors;

public enum ConnectorType
{
    REST,
    SOAP,
    GraphQL
}

public enum AuthenticationType
{
    None,
    Basic,
    ApiKey,
    Bearer,
    OAuth2
}

public class ConnectorOptions
{
    public string Name { get; set; } = string.Empty;
    public ConnectorType Type { get; set; } = ConnectorType.REST;
    public string BaseUrl { get; set; } = string.Empty;
    public AuthenticationType AuthType { get; set; } = AuthenticationType.None;

    // Credentials
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? ApiKey { get; set; }
    public string? ApiKeyHeaderName { get; set; } = "X-API-Key";
    public string? Token { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    // SOAP specific
    public string? SoapAction { get; set; }
    public string? TargetNamespace { get; set; }

    // Resilience
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; } = 3;

    // Custom Headers
    public Dictionary<string, string> Headers { get; set; } = new();
}
