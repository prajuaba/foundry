using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Kafka.Configuration;
using Foundry.Kafka.Consumer;
using Foundry.Kafka.Producer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Foundry.Kafka.Bridge;

/// <summary>
/// Implementation of <see cref="IKafkaMessageHandler"/> that bridges Kafka messages to HTTP APIs
/// with transient HTTP retries and a Dead-Letter Queue (DLQ) fallback.
/// </summary>
public class KafkaToApiBridgeHandler : IKafkaMessageHandler
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IKafkaProducer _kafkaProducer;
    private readonly KafkaOptions _options;
    private readonly ILogger<KafkaToApiBridgeHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="KafkaToApiBridgeHandler"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="kafkaProducer">The Kafka producer for DLQ routing.</param>
    /// <param name="options">The Kafka options.</param>
    /// <param name="logger">The logger.</param>
    public KafkaToApiBridgeHandler(
        IHttpClientFactory httpClientFactory,
        IKafkaProducer kafkaProducer,
        IOptions<KafkaOptions> options,
        ILogger<KafkaToApiBridgeHandler> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _kafkaProducer = kafkaProducer ?? throw new ArgumentNullException(nameof(kafkaProducer));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task HandleAsync(string topic, string key, string value, IDictionary<string, string> headers, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(topic))
            throw new ArgumentException("Topic cannot be null or empty.", nameof(topic));

        if (!_options.ConsumerOptions.TopicApiMappings.TryGetValue(topic, out var apiUrl))
        {
            _logger.LogWarning("No API mapping found for topic: {Topic}", topic);
            return;
        }

        var httpClient = _httpClientFactory.CreateClient("KafkaBridge");
        
        int maxRetries = 3;
        int delayMs = 1000;
        HttpResponseMessage? response = null;
        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var request = CreateRequestMessage(apiUrl, value, headers);
                response = await httpClient.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Successfully sent message from topic {Topic} to API endpoint {Url}", topic, apiUrl);
                    return;
                }

                // Retry on transient status codes (5xx Server Errors or 408 Request Timeout)
                if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                {
                    _logger.LogWarning("HTTP request failed with status code {StatusCode}. Attempt {Attempt} of {MaxRetries}. Retrying in {Delay}ms...", response.StatusCode, attempt, maxRetries, delayMs);
                }
                else
                {
                    // Non-transient error (e.g. 400 Bad Request, 404, 401, etc.) -> fail-fast to DLQ
                    _logger.LogError("Non-transient HTTP error {StatusCode} calling API {Url}. Routing to DLQ.", response.StatusCode, apiUrl);
                    break;
                }
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogWarning(ex, "HTTP connection error calling API {Url}. Attempt {Attempt} of {MaxRetries}. Retrying in {Delay}ms...", apiUrl, attempt, maxRetries, delayMs);
            }

            if (attempt < maxRetries)
            {
                await Task.Delay(delayMs, ct);
                delayMs *= 2; // Exponential backoff
            }
        }

        // If we reached here, the retries failed or we had a non-transient error.
        // Route to DLQ!
        var dlqTopic = $"{topic}-dlq";
        _logger.LogWarning("Failed to forward message from topic {Topic} to API {Url}. Routing to DLQ topic {DlqTopic}...", topic, apiUrl, dlqTopic);

        try
        {
            await _kafkaProducer.ProduceAsync(dlqTopic, key, value, null, ct);
            _logger.LogInformation("Successfully routed message key {Key} to DLQ topic {DlqTopic}.", key, dlqTopic);
        }
        catch (Exception dlqEx)
        {
            _logger.LogError(dlqEx, "CRITICAL: Failed to route message to DLQ topic {DlqTopic}. Consumer partition will block.", dlqTopic);
            
            // Re-throw so that the offset is NOT committed, preventing message loss
            if (response != null && lastException == null)
            {
                throw new HttpRequestException($"HTTP forwarding failed (Status: {response.StatusCode}) and DLQ publishing failed.", inner: null, statusCode: response.StatusCode);
            }
            throw new InvalidOperationException("HTTP forwarding failed and DLQ publishing failed.", lastException ?? dlqEx);
        }
    }

    private HttpRequestMessage CreateRequestMessage(string apiUrl, string value, IDictionary<string, string> headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };

        foreach (var header in headers)
        {
            if (header.Key.StartsWith("X-Kafka-", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            else
            {
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return request;
    }
}
