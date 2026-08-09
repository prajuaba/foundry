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
    /// <param name="configureCommands">
    /// Declares which command types are dispatched by workflow InternalApi actions. Required: the
    /// command type named in a workflow action has to be resolvable to a CLR type, and guessing it
    /// from loaded assemblies is what this replaces.
    /// </param>
    public static IServiceCollection AddFoundryWorkflows(
        this IServiceCollection services,
        Action<WorkflowEntityTypeRegistry> configureEntities,
        Action<WorkflowCommandTypeRegistry>? configureCommands = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureEntities);

        var entityRegistry = new WorkflowEntityTypeRegistry();
        configureEntities(entityRegistry);

        var commandRegistry = new WorkflowCommandTypeRegistry();
        configureCommands?.Invoke(commandRegistry);

        services.TryAddSingleton(entityRegistry);
        services.TryAddSingleton<IWorkflowCommandTypeResolver>(commandRegistry);
        services.TryAddSingleton<IWorkflowDefinitionProvider, ApiManifestWorkflowDefinitionProvider>();

        // Scoped, not singleton. The store resolves IRepository<T> from the IServiceProvider it was
        // constructed with; as a singleton that provider is the root, and IRepository<T> is scoped
        // because ICurrentUserContext is. The two failure modes were opposite and both bad: under
        // the scope validation Development turns on, every transition on every entity threw
        // "Cannot resolve ... from root provider" -- and Production, which does not validate,
        // resolved a root ICurrentUserContext instead, with no HttpContext behind it. That is the
        // wrong operator on the audit entry and no tenant on the write, silently, in the half of
        // the framework whose whole claim is that neither can happen.
        //
        // Nothing holds this across requests: WorkflowTransitionBehavior is transient in the MediatR
        // pipeline and WorkflowHistoryEndpoint takes it [FromServices]. Both are already per-request,
        // so scoped is the lifetime its dependencies always implied.
        services.TryAddScoped<IWorkflowStateStore, MongoWorkflowStateStore>();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(WorkflowTransitionBehavior<,>));

        return services;
    }
}
