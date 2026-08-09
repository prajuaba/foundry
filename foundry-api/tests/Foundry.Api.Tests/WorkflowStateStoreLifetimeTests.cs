using System;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Foundry.Api.Workflow;
using Foundry.Rules;

namespace Foundry.Api.Tests;

/// <summary>
/// The workflow state store must be resolvable per request, not captured by the root container.
/// </summary>
/// <remarks>
/// <para>
/// It was registered <c>TryAddSingleton</c>, so the <see cref="IServiceProvider"/> it captured was
/// the root one. It resolves <c>IRepository&lt;T&gt;</c> out of that provider, and the repository is
/// scoped because <c>ICurrentUserContext</c> is. The two environments failed in opposite directions
/// and only one of them was loud: under the scope validation Development enables, every transition
/// on every entity threw <i>"Cannot resolve ... from root provider"</i>; Production does not
/// validate, so it resolved a root <c>ICurrentUserContext</c> instead — no HttpContext, therefore
/// the wrong operator on the audit entry and no tenant on the write.
/// </para>
/// <para>
/// The showcase's runtime smoke test drives workflow transitions and never caught it, because it
/// runs the application unvalidated. <see cref="ResolvingTheStoreFromTheRootProviderIsRejected"/> is
/// the assertion that would have: it builds the container the way Development does and asks for the
/// store outside a scope.
/// </para>
/// </remarks>
public class WorkflowStateStoreLifetimeTests
{
    private static ServiceProvider BuildValidatingProvider()
    {
        var services = new ServiceCollection();
        services.AddFoundryWorkflows(registry => { });

        // What WebApplicationBuilder does in Development, and the only reason this defect was ever
        // visible. Validating on build as well would fail construction rather than resolution.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = false,
        });
    }

    [Fact]
    public void TheStoreIsRegisteredScoped()
    {
        var services = new ServiceCollection();
        services.AddFoundryWorkflows(registry => { });

        var descriptor = Assert.Single(
            services, d => d.ServiceType == typeof(IWorkflowStateStore));

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void TheStoreResolvesInsideARequestScope()
    {
        using var provider = BuildValidatingProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<IWorkflowStateStore>());
    }

    [Fact]
    public void ResolvingTheStoreFromTheRootProviderIsRejected()
    {
        // The captive dependency, stated as a rule rather than as a symptom: a scoped service is
        // not available from the root, and the store must be one. Were it registered singleton
        // again this resolves happily, and this test is what says so.
        using var provider = BuildValidatingProvider();

        Assert.Throws<InvalidOperationException>(
            () => provider.GetService<IWorkflowStateStore>());
    }
}
