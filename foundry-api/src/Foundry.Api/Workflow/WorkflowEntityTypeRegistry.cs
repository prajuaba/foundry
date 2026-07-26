using System;
using System.Collections.Generic;
using System.Linq;

namespace Foundry.Api.Workflow;

/// <summary>
/// Maps the entity type names used in workflow definitions to their CLR types.
/// </summary>
/// <remarks>
/// <para>
/// An explicit registration replaces a scan of every loaded assembly for a <em>simple name</em> match.
/// That scan bound to whichever assembly happened to be enumerated first, so two entities named
/// <c>Order</c> in different namespaces resolved non-deterministically — and silently, because either
/// result is a usable <see cref="Type"/>.
/// </para>
/// <para>
/// Lookup accepts both the simple name and the full name, because a workflow definition authored in
/// Studio carries the simple name while an audit record carries the assembly-qualified one.
/// </para>
/// </remarks>
public sealed class WorkflowEntityTypeRegistry
{
    private readonly Dictionary<string, Type> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers <typeparamref name="TEntity"/> as a workflow-bearing entity.</summary>
    public WorkflowEntityTypeRegistry Register<TEntity>() => Register(typeof(TEntity));

    /// <summary>Registers <paramref name="entityType"/> as a workflow-bearing entity.</summary>
    public WorkflowEntityTypeRegistry Register(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        _byName[entityType.Name] = entityType;
        if (!string.IsNullOrEmpty(entityType.FullName)) _byName[entityType.FullName] = entityType;

        return this;
    }

    /// <summary>The entity type names that have been registered.</summary>
    public IReadOnlyCollection<string> RegisteredNames => _byName.Keys.ToList();

    /// <summary>
    /// Resolves <paramref name="entityTypeName"/>, throwing with an actionable message when it is not
    /// registered.
    /// </summary>
    /// <remarks>
    /// Throws rather than returning <c>null</c>: "this application has no such entity type" is a
    /// configuration error, and reporting it as a missing entity would send someone looking for a
    /// record that was never the problem.
    /// </remarks>
    public Type Resolve(string entityTypeName)
    {
        if (string.IsNullOrWhiteSpace(entityTypeName))
        {
            throw new ArgumentException("An entity type name is required.", nameof(entityTypeName));
        }

        if (_byName.TryGetValue(entityTypeName, out var type)) return type;

        var known = _byName.Keys.Any() ? string.Join(", ", _byName.Keys.OrderBy(k => k)) : "(none)";
        throw new InvalidOperationException(
            $"Workflow entity type '{entityTypeName}' is not registered. Register it with "
            + $"AddFoundryWorkflows(registry => registry.Register<{entityTypeName}>()). Registered: {known}.");
    }
}
