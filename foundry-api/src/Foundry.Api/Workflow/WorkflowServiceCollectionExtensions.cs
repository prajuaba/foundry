using System;
using Foundry.Api.Workflow;
using Foundry.Rules;
using MediatR;
using Microsoft.Extensions.DependencyInjection.Extensions;

// Matches the convention the other Foundry modules use (AddFoundryMongo, AddFoundryRules,
// AddFoundryKafka), so the method is discoverable without an extra using directive.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration for the workflow transition pipeline.
/// </summary>
/// <remarks>
/// The behaviour previously needed no registration beyond itself, because it discovered every
/// collaborator by reflection at runtime. Requiring explicit registration is the point: an application
/// that has not declared its workflow entities now fails at startup with a message naming what to add,
/// rather than at the first transition with a null reference from inside an assembly scan.
/// </remarks>
public static class WorkflowServiceCollectionExtensions
{
    /// <summary>
    /// Registers the workflow transition behaviour and its collaborators.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureEntities">
    /// Declares which entity types take part in workflows. Required: the entity type named in a
    /// workflow definition has to be resolvable to a CLR type, and guessing it from loaded assemblies
    /// is what this replaces.
    /// </param>
    public static IServiceCollection AddFoundryWorkflows(
        this IServiceCollection services,
        Action<WorkflowEntityTypeRegistry> configureEntities)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureEntities);

        var registry = new WorkflowEntityTypeRegistry();
        configureEntities(registry);

        services.TryAddSingleton(registry);
        services.TryAddSingleton<IWorkflowDefinitionProvider, ApiManifestWorkflowDefinitionProvider>();
        services.TryAddSingleton<IWorkflowStateStore, MongoWorkflowStateStore>();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(WorkflowTransitionBehavior<,>));

        return services;
    }
}
