using System.Collections.Generic;

namespace Foundry.Rules;

/// <summary>
/// Supplies the workflow definitions in force for this application.
/// </summary>
/// <remarks>
/// <para>
/// Exists so <see cref="WorkflowTransitionBehavior{TRequest, TResponse}"/> does not have to find the
/// API manifest by reflection. It used to scan every loaded assembly for a type named
/// <c>ApiManifest</c>, resolve that from the container, and read its <c>Workflows</c> property by
/// name — a chain in which a rename, a second type with the same name, or an assembly that failed to
/// load produced either the wrong definitions or a runtime exception, and none of it could be tested
/// without loading the real API assembly.
/// </para>
/// <para>
/// The reflection existed to avoid a project reference from <c>Foundry.Rules</c> to
/// <c>Foundry.Api</c>, which would be a genuine layering violation. An interface here, implemented in
/// the API layer, achieves the same decoupling with a compile-time contract.
/// </para>
/// </remarks>
public interface IWorkflowDefinitionProvider
{
    /// <summary>
    /// Returns every configured workflow definition. Never <c>null</c>.
    /// </summary>
    IReadOnlyList<WorkflowConfig> GetWorkflows();
}
