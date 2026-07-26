using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Foundry.Connectors;

public class RestConnector : IFoundryConnector
{
    private readonly HttpClient _http;
    private readonly ConnectorOptions _options;
    private readonly ILogger<RestConnector> _logger;

    public string Name => _options.Name;
    public ConnectorType Type => ConnectorType.REST;

    public RestConnector(HttpClient http, ConnectorOptions options, ILogger<RestConnector> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (!string.IsNullOrEmpty(_options.BaseUrl))
        {
            _http.BaseAddress = new Uri(_options.BaseUrl);
        }

        ApplySecurityHeaders();
    }

    private void ApplySecurityHeaders()
    {
        switch (_options.AuthType)
        {
            case AuthenticationType.Basic:
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}"));
                _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
                break;

            case AuthenticationType.Bearer:
                if (!string.IsNullOrEmpty(_options.Token))
                    _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.Token);
                break;

            case AuthenticationType.ApiKey:
                if (!string.IsNullOrEmpty(_options.ApiKey))
                    _http.DefaultRequestHeaders.Add(_options.ApiKeyHeaderName ?? "X-API-Key", _options.ApiKey);
                break;
        }

        foreach (var header in _options.Headers)
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    public async Task<TResponse?> ExecuteAsync<TRequest, TResponse>(string endpoint, TRequest payload, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[RestConnector:{Name}] Executing request to {Endpoint}", Name, endpoint);

        HttpResponseMessage response;
        if (payload == null || payload.Equals(default(TRequest)))
        {
            response = await _http.GetAsync(endpoint, cancellationToken);
        }
        else
        {
            response = await _http.PostAsJsonAsync(endpoint, payload, cancellationToken);
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: cancellationToken);
    }

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _http.GetAsync("", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RestConnector:{Name}] Health check failed for endpoint {BaseUrl}", Name, _options.BaseUrl);
            return false;
        }
    }
}
