using Foundry.Rules;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Foundry.Rules.Tests;

/// <summary>
/// Container wiring for <c>AddFoundryRules</c>.
/// </summary>
/// <remarks>
/// A registration bug is invisible until something resolves the service, which for a library means
/// the first request in a host application rather than any build or unit test. Foundry.RealTime
/// shipped exactly that: its audit sink registration produced a circular dependency and killed
/// startup for every application that called it. These tests build the provider with validation on
/// and actually resolve each service, which is the only thing that catches it here.
/// </remarks>
public class RegistrationTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFoundryRules();

        // ValidateOnBuild surfaces unresolvable graphs at build time; ValidateScopes catches a
        // singleton capturing a scoped dependency, which would otherwise leak state across requests.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    [Fact]
    public void AddFoundryRules_BuildsAValidContainer()
    {
        using var provider = Build();
        Assert.NotNull(provider);
    }

    [Fact]
    public void BusinessRuleEngine_Resolves()
    {
        using var provider = Build();
        Assert.NotNull(provider.GetRequiredService<IBusinessRuleEngine>());
    }

    [Fact]
    public void WorkflowEngine_Resolves()
    {
        using var provider = Build();
        Assert.NotNull(provider.GetRequiredService<IWorkflowEngine>());
    }

    [Fact]
    public void DynamicRuleStore_Resolves()
    {
        using var provider = Build();
        Assert.NotNull(provider.GetRequiredService<IDynamicRuleStore>());
    }

    [Fact]
    public void WorkflowHttpClient_Resolves()
    {
        // AddFoundryRules configures a named client with a Polly resilience pipeline, and the
        // workflow engine's ExternalApi action asks for it by that exact name.
        using var provider = Build();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using var client = factory.CreateClient("FoundryWorkflow");

        Assert.NotNull(client);
    }

    [Fact]
    public void CallingItTwice_IsHarmless()
    {
        // Hosts wire modules independently, so double registration happens; TryAdd should make it
        // a no-op rather than producing duplicate engines.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFoundryRules();
        services.AddFoundryRules();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        Assert.Single(provider.GetServices<IWorkflowEngine>());
    }

    [Fact]
    public void ApplicationSuppliedEngine_IsNotOverridden()
    {
        // TryAdd semantics: an application that registers its own engine first keeps it.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkflowEngine>(new WorkflowEngine(new ServiceCollection().BuildServiceProvider()));
        services.AddFoundryRules();

        using var provider = services.BuildServiceProvider();

        Assert.Single(provider.GetServices<IWorkflowEngine>());
    }
}
