using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Foundry.Rules;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service collection extensions for registering the business rules engine.
/// </summary>
public static class RulesServiceCollectionExtensions
{
    /// <summary>
    /// Registers the business rules engine core services.
    /// </summary>
    public static IServiceCollection AddFoundryRules(this IServiceCollection services)
    {
        services.TryAddSingleton<IBusinessRuleEngine, BusinessRuleEngine>();
        services.TryAddSingleton<IWorkflowEngine, WorkflowEngine>();
        services.TryAddSingleton<IDynamicRuleStore, InMemoryDynamicRuleStore>();
        services.AddTransient(typeof(IBusinessRule<>), typeof(DynamicRulesEngineRule<>));

        // Two workflow clients, differing only in whether they retry.
        //
        // The standard resilience handler retries transient failures without regard to HTTP method,
        // and a timeout is a transient failure -- but a POST that timed out may well have been
        // received and acted on, with only the reply lost. Retrying it delivers the action twice:
        // two charges, two shipments, two notifications, from a workflow that recorded one
        // transition. The engine knows the method it is about to send, so it picks the client;
        // deciding here instead would mean inspecting a request from inside a retry predicate, which
        // this version of the resilience package does not expose for the failures that matter.
        services.AddHttpClient(WorkflowHttpLimits.RetryingClientName, ConfigureWorkflowClient)
            .ConfigurePrimaryHttpMessageHandler(WorkflowHandler)
            .AddStandardResilienceHandler();

        services.AddHttpClient(WorkflowHttpLimits.SingleAttemptClientName, ConfigureWorkflowClient)
            .ConfigurePrimaryHttpMessageHandler(WorkflowHandler)
            .AddStandardResilienceHandler(options =>
            {
                // Circuit breaker and timeout are kept; only the repeat is removed.
                options.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
            });

        return services;
    }

    private static void ConfigureWorkflowClient(HttpClient client)
    {
        // A hostile or broken endpoint should not be able to hand back an unbounded body. The
        // response is read into a string and then written into the activity log, so without a cap
        // one reply can exhaust memory and then exceed MongoDB's 16 MB document limit -- failing the
        // history write, and losing the record of a call that did happen. Exceeding this throws, and
        // the action is recorded as a failure like any other.
        client.MaxResponseContentBufferSize = WorkflowHttpLimits.MaxResponseBytes;
    }

    /// <summary>
    /// The primary handler for both workflow clients. Redirects are not followed.
    /// </summary>
    /// <remarks>
    /// The action URL comes from the schema, so a caller cannot choose the host -- but any service
    /// the workflow calls can answer 302 and send the request wherever it likes, including a
    /// link-local metadata endpoint or something reachable only from inside the network. The response
    /// body is then captured into the activity log. That turns "we call a third party" into "a third
    /// party chooses what we call", which is not a decision an external service should get. A
    /// redirect now surfaces as a 3xx, which is not a success status, so the action fails visibly.
    /// </remarks>
    private static HttpClientHandler WorkflowHandler() => new() { AllowAutoRedirect = false };
}
