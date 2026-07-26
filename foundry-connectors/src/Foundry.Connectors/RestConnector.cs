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

    /// <summary>
    /// The base address this connector will send to, or <c>null</c> when none is configured.
    /// </summary>
    /// <remarks>
    /// Exposed for diagnostics: with several connectors registered, "which host is this one actually
    /// pointing at?" is the first question when a call goes somewhere unexpected. Deliberately does
    /// not expose <see cref="ConnectorOptions"/>, which holds credentials.
    /// </remarks>
    public Uri? BaseAddress => _http.BaseAddress;

    /// <summary>
    /// The API-key header values currently configured on this connector's client.
    /// </summary>
    /// <remarks>
    /// Internal, for tests that assert credentials are not shared between connectors. Not public:
    /// this returns secret material.
    /// </remarks>
    internal IEnumerable<string> ConfiguredApiKeyValues()
    {
        var headerName = _options.ApiKeyHeaderName ?? "X-API-Key";
        return _http.DefaultRequestHeaders.TryGetValues(headerName, out var values)
            ? values
            : [];
    }

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

        // Only a genuinely absent payload becomes a GET.
        //
        // This used to also treat payload.Equals(default(TRequest)) as absent, so a legitimate value
        // that happens to equal its type's default -- 0 for an int, false for a bool, a zeroed
        // struct -- was silently downgraded to a GET and its body dropped. The remote service
        // received a different request from the one the caller wrote, and nothing reported it.
        HttpResponseMessage response;
        if (payload is null)
        {
            response = await _http.GetAsync(endpoint, cancellationToken);
        }
        else
        {
            response = await _http.PostAsJsonAsync(endpoint, payload, cancellationToken);
        }

        await ConnectorResponse.EnsureSuccessAsync(response, Name, endpoint, cancellationToken);

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
