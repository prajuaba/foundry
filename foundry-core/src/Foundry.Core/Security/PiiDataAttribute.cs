using System;

namespace Foundry.Core.Security;

/// <summary>
/// Specifies the masking pattern for PII (Personally Identifiable Information) data fields.
/// </summary>
public enum PiiType
{
    Generic,
    Email,
    Phone,
    CreditCard,
    Ssn,
    Address
}

/// <summary>
/// Marks a property as containing PII data that must be dynamically masked during serialization unless authorized.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public class PiiDataAttribute : Attribute
{
    /// <summary>
    /// Gets the PII data classification type.
    /// </summary>
    public PiiType Type { get; }

    /// <summary>
    /// Gets or sets the custom mask replacement format string.
    /// </summary>
    public string Mask { get; set; } = "****";

    public PiiDataAttribute(PiiType type = PiiType.Generic)
    {
        Type = type;
    }
}
