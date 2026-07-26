using System.Security.Claims;

/// <summary>
/// Ambient context interface for resolving the current user's identity across request scopes.
/// Used by the audit trail engine to stamp Who (operator) on every mutating operation without polluting business code layers.
/// Implementations may read from HTTP Context, ClaimsPrincipal, ambient logical call context, thread-local storage, or dependency injection.
/// </summary>
namespace Foundry.Core.User;

public interface ICurrentUserContext
{
    /// <summary>User-provided operator identifier string. E.g., "user-12345" or system service name.</summary>
    public string OperatorId { get; }

    /// <summary>Display name of the current operator for human-readable audit logs.</summary>
    public string? OperatorName { get; }

    /// <summary>The fully authenticated user's claim principal if available (e.g., JWT claims in HTTP context).</summary>
    public ClaimsPrincipal? User { get; }
}

/// <summary>
/// Minimal ambient operator context implementation that reads from an optional delegate.
/// Suitable for DI registration with scoped or singleton lifetime depending on hosting model.
/// </summary>
public sealed class AmbientUserContext : ICurrentUserContext
{
    private readonly Func<ClaimsPrincipal?>? _userProvider;

    /// <summary>
    /// Creates an ambient user context from a delegate that provides the current ClaimsPrincipal.
    /// The delegate is called on each OperatorId/OperatorName resolution for fresh scope resolution.
    /// </summary>
    public AmbientUserContext(Func<ClaimsPrincipal?>? userProvider = null)
    {
        _userProvider = userProvider;
    }

    public string OperatorId => ExtractClaimValue("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier") ?? 
                                  "anonymous";

    public string? OperatorName => _userProvider?.Invoke()?.Identity?.Name;

    /// <inheritdoc />
    public ClaimsPrincipal? User => _userProvider?.Invoke();

    private string? ExtractClaimValue(string claimType)
    {
        var principal = _userProvider?.Invoke();
        return principal?.FindFirst(claimType)?.Value ?? 
               principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }
}
