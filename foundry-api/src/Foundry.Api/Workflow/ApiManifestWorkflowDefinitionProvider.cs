using System.Collections.Generic;
using Foundry.Api.Manifest;
using Foundry.Rules;

namespace Foundry.Api.Workflow;

/// <summary>
/// Supplies workflow definitions from the application's <see cref="ApiManifest"/>.
/// </summary>
/// <remarks>
/// This is the whole of what <c>WorkflowTransitionBehavior</c> used to do by reflection: scan every
/// loaded assembly for a type named <c>ApiManifest</c>, resolve it from the container, and read its
/// <c>Workflows</c> property by name. Expressed as a direct reference from the layer that actually
/// owns the manifest, it is four lines and cannot bind to the wrong type.
/// </remarks>
public sealed class ApiManifestWorkflowDefinitionProvider : IWorkflowDefinitionProvider
{
    private readonly ApiManifest _manifest;

    /// <summary>Initializes the provider with the application's manifest.</summary>
    public ApiManifestWorkflowDefinitionProvider(ApiManifest manifest)
    {
        _manifest = manifest ?? throw new System.ArgumentNullException(nameof(manifest));
    }

    /// <inheritdoc />
    public IReadOnlyList<WorkflowConfig> GetWorkflows() => _manifest.Workflows ?? [];
}
