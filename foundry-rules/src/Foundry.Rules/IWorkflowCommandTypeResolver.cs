using System;

namespace Foundry.Rules;

/// <summary>
/// Resolves command type names used in workflow action definitions to their CLR types.
/// </summary>
/// <remarks>
/// <para>
/// An explicit resolution replaces a scan of every loaded assembly for a <em>simple name</em> match.
/// That scan bound to whichever assembly happened to be enumerated first, so two commands named
/// <c>ArchiveOrder</c> in different namespaces resolved non-deterministically — and silently, because either
/// result is a usable <see cref="Type"/>.
/// </para>
/// <para>
/// Lookup accepts both the simple name and the full name, because a workflow definition authored in
/// Studio carries the simple name while an audit record carries the assembly-qualified one.
/// </para>
/// </remarks>
public interface IWorkflowCommandTypeResolver
{
    /// <summary>
    /// Resolves <paramref name="commandTypeName"/>, throwing with an actionable message when it is not
    /// registered.
    /// </summary>
    /// <remarks>
    /// Throws rather than returning <c>null</c>: "this application has no such command type" is a
    /// configuration error, and reporting it as a missing command would send someone looking for a
    /// handler that was never the problem.
    /// </remarks>
    Type Resolve(string commandTypeName);
}
