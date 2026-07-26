using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Foundry.Connectors;

public class GraphQLConnector : IFoundryConnector
{
    private readonly HttpClient _http;
    private readonly ConnectorOptions _options;
    private readonly ILogger<GraphQLConnector> _logger;

    public string Name => _options.Name;
    public ConnectorType Type => ConnectorType.GraphQL;

    public GraphQLConnector(HttpClient http, ConnectorOptions options, ILogger<GraphQLConnector> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (!string.IsNullOrEmpty(_options.BaseUrl))
        {
            _http.BaseAddress = new Uri(_options.BaseUrl);
        }

        if (!string.IsNullOrEmpty(_options.Token))
        {
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.Token);
        }
    }

    public async Task<TResponse?> ExecuteAsync<TRequest, TResponse>(string query, TRequest variables, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[GraphQLConnector:{Name}] Executing GraphQL Query", Name);

        var requestBody = new
        {
            query = query,
            variables = variables
        };

        var response = await _http.PostAsJsonAsync("", requestBody, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        if (json.TryGetProperty("data", out var dataProp))
        {
            return JsonSerializer.Deserialize<TResponse>(dataProp.GetRawText());
        }

        return default;
    }

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var healthQuery = new { query = "{ __typename }" };
            var response = await _http.PostAsJsonAsync("", healthQuery, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GraphQLConnector:{Name}] GraphQL health check failed for {BaseUrl}", Name, _options.BaseUrl);
            return false;
        }
    }
}
