using System;
using System.Threading;

namespace Foundry.Core.Tenant;

/// <summary>
/// Default ambient AsyncLocal implementation of <see cref="ITenantContext"/>.
/// </summary>
public class TenantContext : ITenantContext
{
    private static readonly AsyncLocal<string?> CurrentTenantId = new();

    /// <inheritdoc/>
    public string? TenantId => CurrentTenantId.Value;

    /// <inheritdoc/>
    public bool HasTenant => !string.IsNullOrWhiteSpace(CurrentTenantId.Value);

    /// <inheritdoc/>
    public void SetTenantId(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentException("Tenant ID cannot be null or empty.", nameof(tenantId));
        }

        CurrentTenantId.Value = tenantId;
    }
}
