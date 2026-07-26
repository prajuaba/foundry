# 🔌 Foundry.Connectors

`Foundry.Connectors` is an enterprise-grade external service connector library built on .NET 10. It provides unified client abstractions for integrating third-party APIs across REST, SOAP (1.1 & 1.2), and GraphQL protocols with built-in security, Polly v8 resilience, and ASP.NET Core health checks.

---

## 🌟 Key Features

1. **Multi-Protocol Connectors**:
   - **`RestConnector`**: HTTP client for REST APIs supporting `GET`, `POST`, `PUT`, `DELETE`, and `PATCH`.
   - **`SoapConnector`**: SOAP 1.1 / 1.2 Web Services client with automatic XML envelope serialization, `SOAPAction` header injection, and WSDL availability checks.
   - **`GraphQLConnector`**: External GraphQL client handling queries, mutations, variable map serialization, and error response extraction.

2. **Security & Authentication**:
   - Supports `None`, `Basic` (Username/Password), `ApiKey` (`X-API-Key` or custom header name), `Bearer` tokens, and `OAuth2` client credentials.

3. **Polly v8 Resilience Pipelines**:
   - Integrated `.AddStandardResilienceHandler()` providing automatic exponential backoff retries, request timeouts, and circuit breaker patterns.

4. **ASP.NET Core Health Monitoring**:
   - **`FoundryConnectorHealthCheck`**: Integrates external connector liveness and latency checks into standard ASP.NET Core `/health` endpoints.

---

## 🛠️ Usage Example

```csharp
// Register connectors in Dependency Injection
services.AddFoundryConnectors(new List<ConnectorOptions>
{
    new ConnectorOptions
    {
        Name = "PaymentGateway",
        Type = ConnectorType.REST,
        BaseUrl = "https://api.payments.com",
        AuthType = AuthenticationType.ApiKey,
        ApiKey = "secret_api_key_123",
        ApiKeyHeaderName = "X-API-Key",
        TimeoutSeconds = 15,
        MaxRetries = 3
    },
    new ConnectorOptions
    {
        Name = "LegacySapService",
        Type = ConnectorType.SOAP,
        BaseUrl = "https://sap.internal.company.com/ws/orders.wsdl",
        SoapAction = "http://tempuri.org/CreateOrder",
        TimeoutSeconds = 30
    }
});

// Inject and execute external calls
public class OrderService
{
    private readonly IFoundryConnector _connector;

    public OrderService(IEnumerable<IFoundryConnector> connectors)
    {
        _connector = connectors.First(c => c.Name == "PaymentGateway");
    }

    public async Task ProcessPaymentAsync()
    {
        var response = await _connector.ExecuteAsync<PaymentRequest, PaymentResponse>(
            new PaymentRequest { Amount = 100.00m },
            endpoint: "/v1/charge");
    }
}
```
