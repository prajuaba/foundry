namespace Foundry.Core.Tenant;

/// <summary>
/// Provides ambient access to the current tenant context for multi-tenant isolation.
/// </summary>
public interface ITenantContext
{
    /// <summary>
    /// Gets the current tenant identifier.
    /// </summary>
    string? TenantId { get; }

    /// <summary>
    /// Gets a value indicating whether a tenant context is currently active.
    /// </summary>
    bool HasTenant { get; }

    /// <summary>
    /// Sets the current tenant identifier for the ambient execution scope.
    /// </summary>
    void SetTenantId(string tenantId);
}
