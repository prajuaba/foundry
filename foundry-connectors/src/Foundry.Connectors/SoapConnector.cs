using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Microsoft.Extensions.Logging;

namespace Foundry.Connectors;

public class SoapConnector : IFoundryConnector
{
    private readonly HttpClient _http;
    private readonly ConnectorOptions _options;
    private readonly ILogger<SoapConnector> _logger;

    public string Name => _options.Name;
    public ConnectorType Type => ConnectorType.SOAP;

    public SoapConnector(HttpClient http, ConnectorOptions options, ILogger<SoapConnector> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (!string.IsNullOrEmpty(_options.BaseUrl))
        {
            _http.BaseAddress = new Uri(_options.BaseUrl);
        }
    }

    public async Task<TResponse?> ExecuteAsync<TRequest, TResponse>(string action, TRequest payload, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[SoapConnector:{Name}] Executing SOAP Action: {Action}", Name, action);

        var xmlPayload = SerializeToXml(payload);
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
  <soap:Body>
    {xmlPayload}
  </soap:Body>
</soap:Envelope>";

        var content = new StringContent(soapEnvelope, Encoding.UTF8, "text/xml");
        var soapActionHeader = !string.IsNullOrEmpty(_options.SoapAction) ? _options.SoapAction : action;
        content.Headers.Add("SOAPAction", $"\"{soapActionHeader}\"");

        var response = await _http.PostAsync("", content, cancellationToken);

        // A SOAP fault normally arrives as HTTP 500 with the fault detail in the body, so discarding
        // the body -- which EnsureSuccessStatusCode does -- threw away the entire description of what
        // went wrong and left only "500 Internal Server Error".
        await ConnectorResponse.EnsureSuccessAsync(response, Name, action, cancellationToken);

        var xmlResponse = await response.Content.ReadAsStringAsync(cancellationToken);
        return DeserializeFromSoapEnvelope<TResponse>(xmlResponse);
    }

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _http.GetAsync("?wsdl", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SoapConnector:{Name}] SOAP WSDL health check failed for {BaseUrl}", Name, _options.BaseUrl);
            return false;
        }
    }

    private static string SerializeToXml<T>(T obj)
    {
        if (obj == null) return string.Empty;
        var serializer = new XmlSerializer(typeof(T));
        using var writer = new StringWriter();
        serializer.Serialize(writer, obj);
        return writer.ToString();
    }

    private static TResponse? DeserializeFromSoapEnvelope<TResponse>(string soapXml)
    {
        try
        {
            var serializer = new XmlSerializer(typeof(TResponse));
            using var reader = new StringReader(soapXml);
            return (TResponse?)serializer.Deserialize(reader);
        }
        catch
        {
            return default;
        }
    }
}
