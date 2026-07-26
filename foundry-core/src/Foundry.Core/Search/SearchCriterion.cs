using System.Linq.Expressions;
using MongoDB.Bson;

namespace Foundry.Core.Search;

/// <summary>
/// Defines the comparison operators supported by dynamic search criteria.
/// Each operator maps to a MongoDB query expression and uses LINQ-compatible filtering.
/// </summary>
public enum SearchOperator : byte
{
    Equals = 0,
    NotEquals = 1,
    Contains = 2,
    StartsWith = 3,
    EndsWith = 4,
    GreaterThan = 5,
    LessThan = 6,
    GreaterThanOrEqual = 7,
    LessThanOrEqual = 8,
    In = 9,
    NotIn = 10,
    Exists = 11,
}

/// <summary>
/// A single search criterion that represents one field-value-operator filter rule.
/// Used by DynamicExpressionBuilder to construct complex Expression<Func&lt;T, bool>> queries at runtime.
/// </summary>
public sealed record SearchCriterion
{
    /// <summary>The property path to search (supports dot-notation for nested: "Address.City").</summary>
    public required string Field { get; init; }

    /// <summary>The comparison operator to apply against the field value.</summary>
    public required SearchOperator Operator { get; init; }

    /// <summary>The value to compare against. Must be compatible with the field type for safe comparison.</summary>
    public object? Value { get; init; }

    /// <summary>Logical grouping key — criteria with the same GroupKey are combined with AND/OR per the builder configuration.</summary>
    public string? GroupKey { get; init; }

    /// <summary>Creates a criterion for equality comparison (field == value).</summary>
    public static SearchCriterion Equals(string field, object? value) => new() { Field = field, Operator = SearchOperator.Equals, Value = value };

    /// <summary>Creates a criterion for string contains search.</summary>
    public static SearchCriterion Contains(string field, string? value) => new() { Field = field, Operator = SearchOperator.Contains, Value = value };

    /// <summary>Creates a criterion for string startswith search.</summary>
    public static SearchCriterion StartsWith(string field, string? value) => new() { Field = field, Operator = SearchOperator.StartsWith, Value = value };

    /// <summary>Creates a criterion for numeric/greater-than comparison.</summary>
    public static SearchCriterion GreaterThan<T>(string field, T value) where T : IComparable => new() { Field = field, Operator = SearchOperator.GreaterThan, Value = value };

    /// <summary>Creates an In array comparison.</summary>
    public static SearchCriterion In(string field, IEnumerable<object?> values) => new() { Field = field, Operator = SearchOperator.In, Value = values?.ToArray() ?? [] };
}

/// <summary>
/// Result of DynamicExpressionBuilder compilation — contains the compiled Expression and metadata for verification/logging.
/// </summary>
public sealed record CompiledSearchExpression<T> where T : class
{
    /// <summary>The compiled Expression&lt;Func&lt;T, bool>> ready for MongoDB query execution.</summary>
    public Expression<Func<T, bool>> FilterExpression { get; init; } = default!;

    /// <summary>All search criteria that were combined to produce this expression.</summary>
    public IReadOnlyList<SearchCriterion> Criteria { get; init; } = [];

    /// <summary>The aggregation pipeline stage document (BsonDocument) for cross-collection searches using raw MongoDB filters.</summary>
    public BsonDocument? FilterStage { get; init; }

    /// <summary>True when this compiled expression uses any dynamic operator (contains, starts with) requiring client-side evaluation.</summary>
    public bool UsesServerSideOnly => Criteria.All(c => c.Operator is SearchOperator.Equals or SearchOperator.GreaterThan or SearchOperator.LessThan);
}
