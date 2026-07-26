using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Foundry.Rules;

/// <summary>
/// Default implementation of the Foundry Workflow transition and actions engine.
/// </summary>
public class WorkflowEngine : IWorkflowEngine
{
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkflowEngine"/> class.
    /// </summary>
    /// <param name="serviceProvider">The application's service provider container.</param>
    public WorkflowEngine(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public void ValidatePermission(
        string transitionId,
        string currentState,
        List<string> transitionRoles,
        List<string> stateRoles,
        IEnumerable<string> userRoles)
    {
        var userRoleList = userRoles.ToList();

        // 1. Validate if the current state gates user access
        if (stateRoles.Any() && !stateRoles.Any(r => userRoleList.Contains(r, StringComparer.OrdinalIgnoreCase)))
        {
            throw new WorkflowException(
                $"Access denied. Current state '{currentState}' requires roles: {string.Join(", ", stateRoles)}.");
        }

        // 2. Validate if the transition gates user access
        if (transitionRoles.Any() && !transitionRoles.Any(r => userRoleList.Contains(r, StringComparer.OrdinalIgnoreCase)))
        {
            throw new WorkflowException(
                $"Access denied. Transition '{transitionId}' requires roles: {string.Join(", ", transitionRoles)}.");
        }
    }

    /// <inheritdoc />
    public bool EvaluateCondition(string propertyName, string op, string expectedValue, object requestPayload)
    {
        if (requestPayload == null) return false;
        var prop = requestPayload.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop == null) return false;

        var val = prop.GetValue(requestPayload);
        if (val == null) return expectedValue == "null" || string.IsNullOrEmpty(expectedValue);

        var valType = val.GetType();
        if (IsNumericType(valType))
        {
            if (!decimal.TryParse(expectedValue, out var expectedNum)) return false;
            var currentNum = Convert.ToDecimal(val);
            return op.ToLower() switch
            {
                "equal" => currentNum == expectedNum,
                "notequal" => currentNum != expectedNum,
                "lessthan" => currentNum < expectedNum,
                "lessthanorequal" => currentNum <= expectedNum,
                "greaterthan" => currentNum > expectedNum,
                "greaterthanorequal" => currentNum >= expectedNum,
                _ => false
            };
        }

        var currentStr = val.ToString() ?? "";
        return op.ToLower() switch
        {
            "equal" => currentStr.Equals(expectedValue, StringComparison.OrdinalIgnoreCase),
            "notequal" => !currentStr.Equals(expectedValue, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    /// <inheritdoc />
    public async Task<ActionExecutionDetail> ExecuteActionAsync(
        string actionType,
        string? requestType,
        string? payloadTemplate,
        string? method,
        string? url,
        Dictionary<string, string>? headers,
        string? bodyTemplate,
        object requestPayload,
        CancellationToken ct)
    {
        if (actionType.Equals("InternalApi", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(requestType))
            {
                return new ActionExecutionDetail
                {
                    ActionType = "InternalApi",
                    ActionName = "Unknown",
                    Success = false,
                    StatusCode = 400,
                    ResponseBody = "RequestType parameter is empty."
                };
            }

            try
            {
                var payload = SubstituteTokens(payloadTemplate ?? "{}", requestPayload);
                var resolvedType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => {
                        try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
                    })
                    .FirstOrDefault(t => t.Name.Equals(requestType, StringComparison.OrdinalIgnoreCase) || t.FullName?.Equals(requestType, StringComparison.OrdinalIgnoreCase) == true);

                if (resolvedType == null)
                {
                    return new ActionExecutionDetail
                    {
                        ActionType = "InternalApi",
                        ActionName = requestType,
                        Success = false,
                        StatusCode = 404,
                        ResponseBody = $"Internal MediatR command type '{requestType}' could not be resolved."
                    };
                }

                var command = JsonSerializer.Deserialize(payload, resolvedType, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (command == null)
                {
                    return new ActionExecutionDetail
                    {
                        ActionType = "InternalApi",
                        ActionName = requestType,
                        Success = false,
                        StatusCode = 400,
                        ResponseBody = "Failed to deserialize command payload."
                    };
                }

                var sender = _serviceProvider.GetRequiredService<ISender>();
                await sender.Send(command, ct);

                return new ActionExecutionDetail
                {
                    ActionType = "InternalApi",
                    ActionName = requestType,
                    Success = true,
                    StatusCode = 200,
                    ResponseBody = "Successfully sent internal command."
                };
            }
            catch (Exception ex)
            {
                return new ActionExecutionDetail
                {
                    ActionType = "InternalApi",
                    ActionName = requestType,
                    Success = false,
                    StatusCode = 500,
                    ResponseBody = $"Execution failed: {ex.Message}"
                };
            }
        }
        else if (actionType.Equals("ExternalApi", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return new ActionExecutionDetail
                {
                    ActionType = "ExternalApi",
                    ActionName = "Unknown",
                    Success = false,
                    StatusCode = 400,
                    ResponseBody = "External URL parameter is empty."
                };
            }

            var actionName = $"{method ?? "POST"} {url}";
            try
            {
                var targetUrl = SubstituteTokens(url, requestPayload);
                var body = SubstituteTokens(bodyTemplate ?? "{}", requestPayload);

                var httpClientFactory = _serviceProvider.GetService<IHttpClientFactory>();
                using var client = httpClientFactory != null ? httpClientFactory.CreateClient("FoundryWorkflow") : new HttpClient();
                using var requestMsg = new HttpRequestMessage(new HttpMethod(method ?? "POST"), targetUrl);

                if (headers != null)
                {
                    foreach (var header in headers)
                    {
                        requestMsg.Headers.TryAddWithoutValidation(header.Key, SubstituteTokens(header.Value, requestPayload));
                    }
                }

                if (!string.IsNullOrWhiteSpace(body) && body != "{}")
                {
                    requestMsg.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                }

                var response = await client.SendAsync(requestMsg, ct);
                var responseBody = await response.Content.ReadAsStringAsync(ct);

                return new ActionExecutionDetail
                {
                    ActionType = "ExternalApi",
                    ActionName = actionName,
                    Success = response.IsSuccessStatusCode,
                    StatusCode = (int)response.StatusCode,
                    ResponseBody = responseBody
                };
            }
            catch (Exception ex)
            {
                return new ActionExecutionDetail
                {
                    ActionType = "ExternalApi",
                    ActionName = actionName,
                    Success = false,
                    StatusCode = 500,
                    ResponseBody = $"HTTP call failed: {ex.Message}"
                };
            }
        }

        return new ActionExecutionDetail
        {
            ActionType = actionType,
            ActionName = "Unsupported",
            Success = false,
            StatusCode = 400,
            ResponseBody = $"Unsupported action type: {actionType}"
        };
    }

    private string SubstituteTokens(string template, object source)
    {
        if (string.IsNullOrEmpty(template) || source == null) return template;
        
        var result = template;
        var properties = source.GetType().GetProperties();
        foreach (var prop in properties)
        {
            var token = $"{{{{{prop.Name}}}}}";
            if (result.Contains(token))
            {
                var val = prop.GetValue(source)?.ToString() ?? "";
                result = result.Replace(token, val);
            }
        }
        return result;
    }

    private static bool IsNumericType(Type type)
    {
        return Type.GetTypeCode(type) switch
        {
            TypeCode.Byte or TypeCode.SByte or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64 or
            TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 or TypeCode.Decimal or TypeCode.Double or TypeCode.Single => true,
            _ => false
        };
    }
}
