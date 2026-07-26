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

    /// <summary>
    /// Options used to bind the <c>data</c> object onto the caller's response type.
    /// </summary>
    /// <remarks>
    /// Case-insensitive. GraphQL schemas conventionally use camelCase field names while C# response
    /// types are PascalCase, and this deserialisation previously used
    /// <see cref="JsonSerializer"/>'s strict defaults — so a perfectly good response bound to a
    /// response object with every property left at its default. The REST connector never hit this
    /// because <c>ReadFromJsonAsync</c> applies web defaults, which are case-insensitive.
    /// </remarks>
    private static readonly JsonSerializerOptions ResponseOptions = new(JsonSerializerDefaults.Web);

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
        await ConnectorResponse.EnsureSuccessAsync(response, Name, string.Empty, cancellationToken);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);

        // GraphQL reports failure with HTTP 200 and an "errors" array, so the status code says
        // nothing. This used to return default (null) whenever "data" was absent and drop "errors"
        // entirely: a rejected query was indistinguishable from a query that legitimately returned
        // nothing, and the server's explanation never reached the caller. That is the normal GraphQL
        // failure mode, not an edge case.
        if (json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty("errors", out var errorsProp)
            && errorsProp.ValueKind == JsonValueKind.Array
            && errorsProp.GetArrayLength() > 0)
        {
            var messages = new List<string>();
            foreach (var error in errorsProp.EnumerateArray())
            {
                messages.Add(error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var messageProp)
                    && messageProp.ValueKind == JsonValueKind.String
                        ? messageProp.GetString() ?? error.GetRawText()
                        : error.GetRawText());
            }

            throw new HttpRequestException(
                $"[{Name}] GraphQL query returned {messages.Count} error(s): {string.Join("; ", messages)}");
        }

        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("data", out var dataProp))
        {
            return JsonSerializer.Deserialize<TResponse>(dataProp.GetRawText(), ResponseOptions);
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
