using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Foundry.Rules;

/// <summary>
/// Model representing a dynamically configured business validation rule.
/// </summary>
public record DynamicRule
{
    /// <summary>Gets the unique name of the rule.</summary>
    public string RuleName { get; init; } = string.Empty;

    /// <summary>Gets the human-readable description of the rule.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Gets the target domain entity name (e.g. "Order").</summary>
    public string TargetEntity { get; init; } = string.Empty;

    /// <summary>Gets the name of the property to evaluate (e.g. "TotalAmount").</summary>
    public string PropertyName { get; init; } = string.Empty;

    /// <summary>Gets the evaluation operator (e.g. "==", "contains", "lessthan").</summary>
    public string Operator { get; init; } = string.Empty;

    /// <summary>Gets the value to compare against.</summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>Gets the full logical expression for Microsoft.RulesEngine evaluation (optional).</summary>
    public string Expression { get; init; } = string.Empty;

    /// <summary>Gets the error message to return on validation failure.</summary>
    public string ErrorMessage { get; init; } = string.Empty;

    /// <summary>Gets the unique error code to return on validation failure.</summary>
    public string ErrorCode { get; init; } = string.Empty;
}

/// <summary>
/// Contract for retrieving dynamically configured validation rules.
/// </summary>
public interface IDynamicRuleStore
{
    /// <summary>
    /// Gets all registered dynamic rules for a specific entity type.
    /// </summary>
    Task<IEnumerable<DynamicRule>> GetRulesForEntityAsync(string entityName, CancellationToken ct);
}

/// <summary>
/// An in-memory implementation of the dynamic rules store.
/// </summary>
public class InMemoryDynamicRuleStore : IDynamicRuleStore
{
    private readonly IEnumerable<DynamicRule> _rules;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryDynamicRuleStore"/> class.
    /// </summary>
    public InMemoryDynamicRuleStore(IEnumerable<DynamicRule>? rules = null)
    {
        _rules = rules ?? Enumerable.Empty<DynamicRule>();
    }

    /// <inheritdoc />
    public Task<IEnumerable<DynamicRule>> GetRulesForEntityAsync(string entityName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityName))
            return Task.FromResult(Enumerable.Empty<DynamicRule>());

        var matchedRules = _rules.Where(r => 
            string.Equals(r.TargetEntity, entityName, StringComparison.OrdinalIgnoreCase));
        
        return Task.FromResult(matchedRules);
    }
}
