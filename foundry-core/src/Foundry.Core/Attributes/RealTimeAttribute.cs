using System;

namespace Foundry.Core.Attributes;

/// <summary>
/// Configures real-time event broadcasting and RBAC subscription access for this domain entity class.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public class RealTimeAttribute : Attribute
{
    /// <summary>
    /// Gets whether real-time mutation broadcasting is enabled for this entity.
    /// </summary>
    public bool Enabled { get; }

    /// <summary>
    /// Gets the list of authorized roles allowed to subscribe to real-time events for this entity.
    /// </summary>
    public string[] Roles { get; }

    /// <summary>
    /// Initializes a new instance of the RealTimeAttribute.
    /// </summary>
    public RealTimeAttribute(bool enabled = true, string[]? roles = null)
    {
        Enabled = enabled;
        Roles = roles ?? Array.Empty<string>();
    }
}
