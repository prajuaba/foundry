using System;

namespace Foundry.Core.Tenant;

/// <summary>
/// Contract interface for entities participating in multi-tenant data isolation.
/// </summary>
public interface IMultiTenant
{
    /// <summary>
    /// Gets or sets the tenant identifier for data segregation.
    /// </summary>
    string TenantId { get; set; }
}

/// <summary>
/// Identifies a property as the multi-tenant segregation key.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public class TenantKeyAttribute : Attribute
{
}
