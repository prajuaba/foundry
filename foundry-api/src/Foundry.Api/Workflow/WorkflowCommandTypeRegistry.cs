using System;
using System.Collections.Generic;
using System.Linq;
using Foundry.Rules;

namespace Foundry.Api.Workflow;

/// <summary>
/// Maps the command type names used in workflow action definitions to their CLR types.
/// </summary>
/// <remarks>
/// <para>
/// An explicit registration replaces a scan of every loaded assembly for a <em>simple name</em> match.
/// That scan bound to whichever assembly happened to be enumerated first, so two commands named
/// <c>ArchiveOrder</c> in different namespaces resolved non-deterministically — and silently, because either
/// result is a usable <see cref="Type"/>.
/// </para>
/// <para>
/// Lookup accepts both the simple name and the full name, because a workflow definition authored in
/// Studio carries the simple name while an audit record carries the assembly-qualified one.
/// </para>
/// </remarks>
public sealed class WorkflowCommandTypeRegistry : IWorkflowCommandTypeResolver
{
    private readonly Dictionary<string, Type> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers <typeparamref name="TCommand"/> as a workflow-dispatched command.</summary>
    public WorkflowCommandTypeRegistry Register<TCommand>() => Register(typeof(TCommand));

    /// <summary>Registers <paramref name="commandType"/> as a workflow-dispatched command.</summary>
    public WorkflowCommandTypeRegistry Register(Type commandType)
    {
        ArgumentNullException.ThrowIfNull(commandType);

        _byName[commandType.Name] = commandType;
        if (!string.IsNullOrEmpty(commandType.FullName)) _byName[commandType.FullName] = commandType;

        return this;
    }

    /// <summary>The command type names that have been registered.</summary>
    public IReadOnlyCollection<string> RegisteredNames => _byName.Keys.ToList();

    /// <summary>
    /// Resolves <paramref name="commandTypeName"/>, throwing with an actionable message when it is not
    /// registered.
    /// </summary>
    /// <remarks>
    /// Throws rather than returning <c>null</c>: "this application has no such command type" is a
    /// configuration error, and reporting it as a missing command would send someone looking for a
    /// handler that was never the problem.
    /// </remarks>
    public Type Resolve(string commandTypeName)
    {
        if (string.IsNullOrWhiteSpace(commandTypeName))
        {
            throw new ArgumentException("A command type name is required.", nameof(commandTypeName));
        }

        if (_byName.TryGetValue(commandTypeName, out var type)) return type;

        var known = _byName.Keys.Any() ? string.Join(", ", _byName.Keys.OrderBy(k => k)) : "(none)";
        throw new InvalidOperationException(
            $"Workflow command type '{commandTypeName}' is not registered. Register it with "
            + $"AddFoundryWorkflows(registry => registry.Register<{commandTypeName}>()). Registered: {known}.");
    }
}
