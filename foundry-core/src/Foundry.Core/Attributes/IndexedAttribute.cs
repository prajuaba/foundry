using System;

namespace Foundry.Core.Entities;

/// <summary>
/// Specifies that the decorated property should be indexed in MongoDB.
/// The DAL scans for this attribute during startup to register indexes automatically.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class IndexedAttribute : Attribute
{
    /// <summary>Enforces unique constraints on the index (duplications will throw MongoWriteException).</summary>
    public bool Unique { get; set; }

    /// <summary>If true, creates a descending index; otherwise ascending (default).</summary>
    public bool Descending { get; set; }

    /// <summary>Optional custom name for the index. If omitted, MongoDB Driver will generate it.</summary>
    public string? Name { get; set; }
}

/// <summary>
/// Specifies that the decorated property should be part of a text search index.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class TextIndexedAttribute : Attribute
{
    /// <summary>Weight of the text field relative to other text fields in search relevance score.</summary>
    public int Weight { get; set; } = 1;
}
