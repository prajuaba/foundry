using System;

namespace Foundry.Core.Entities;

/// <summary>
/// Declares a MongoDB index spanning one or more properties of the decorated entity.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IndexedAttribute"/> is per-property and therefore cannot express a compound index,
/// where field order is what makes the index usable for a given query. This attribute is applied
/// at the type level and may be repeated.
/// </para>
/// <para>
/// The schema compiler emits one of these per entry in the IR's entity-level <c>indexes</c> array.
/// Before this existed those declarations were validated, shown in Studio, and then silently
/// dropped on emit — the index was believed to exist and never created, which is the quietest and
/// most expensive way for a MongoDB application to be slow.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class CompoundIndexAttribute : Attribute
{
    /// <summary>
    /// Property names in index order. Order is significant: MongoDB can use a compound index for
    /// queries on a prefix of these fields, so the most selective or most frequently filtered
    /// field belongs first.
    /// </summary>
    public string[] Fields { get; }

    /// <summary>Enforces a unique constraint across the combined fields.</summary>
    public bool Unique { get; set; }

    /// <summary>
    /// Optional index name. When omitted the DAL derives a stable one from the field names, so a
    /// rebuild does not create a second copy of the same index under a driver-generated name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Initialises the attribute with the ordered field list.</summary>
    /// <param name="fields">Property names, in index order.</param>
    public CompoundIndexAttribute(params string[] fields)
    {
        Fields = fields ?? Array.Empty<string>();
    }
}
