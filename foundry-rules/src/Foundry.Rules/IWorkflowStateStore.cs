using System.Threading;
using System.Threading.Tasks;

namespace Foundry.Rules;

/// <summary>
/// Loads and persists the workflow-bearing entity, and records transition history.
/// </summary>
/// <remarks>
/// <para>
/// Exists so <see cref="WorkflowTransitionBehavior{TRequest, TResponse}"/> does not reach into the
/// data layer by reflection. It used to locate the entity's CLR type by scanning every loaded
/// assembly for a <em>simple name</em> match, construct <c>IRepository&lt;T&gt;</c> with
/// <c>MakeGenericType</c>, and invoke <c>GetByIdAsync</c>, <c>UpdateAsync</c> and <c>InsertAsync</c>
/// through <c>MethodInfo</c>. Consequences of that approach:
/// </para>
/// <list type="bullet">
/// <item><description>Two entities with the same simple name in different namespaces resolved to
/// whichever assembly happened to be enumerated first — silently the wrong type.</description></item>
/// <item><description>The key type was guessed from the id string's length (24 characters meant
/// <c>ObjectId</c>, anything else <c>string</c>), so a malformed id was quietly treated as a
/// different key type and failed deep inside the driver.</description></item>
/// <item><description>Renaming a repository method broke the workflow at runtime with no compiler
/// error.</description></item>
/// <item><description>None of the orchestration could be tested without a real MongoDB repository and
/// the API assembly loaded in-process.</description></item>
/// </list>
/// <para>
/// <c>Foundry.Rules</c> deliberately does not reference a data provider, which is what the reflection
/// was working around. An interface here, implemented in the layer that owns
/// <c>IRepository&lt;T&gt;</c>, keeps that independence and makes the behaviour testable.
/// </para>
/// </remarks>
public interface IWorkflowStateStore
{
    /// <summary>
    /// Loads the entity identified by <paramref name="entityTypeName"/> and
    /// <paramref name="entityId"/>, or <c>null</c> when it does not exist.
    /// </summary>
    /// <remarks>
    /// Implementations must throw when <paramref name="entityTypeName"/> is not a type they can
    /// resolve, rather than returning <c>null</c>: "the entity does not exist" and "this application
    /// has no such entity type" call for different responses, and conflating them turns a
    /// configuration error into an apparently missing record.
    /// </remarks>
    Task<IWorkflowStateful?> LoadAsync(string entityTypeName, string entityId, CancellationToken ct = default);

    /// <summary>Persists state changes made to a previously loaded entity.</summary>
    Task SaveAsync(string entityTypeName, IWorkflowStateful entity, CancellationToken ct = default);

    /// <summary>Appends a transition record to the workflow activity history.</summary>
    Task AppendActivityLogAsync(WorkflowActivityLog log, CancellationToken ct = default);
}
