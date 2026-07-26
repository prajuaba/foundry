using System;
using System.Collections.Generic;

namespace Foundry.Rules;

/// <summary>
/// Domain exception thrown when business rule policy validations fail.
/// </summary>
public class BusinessRuleException : Exception
{
    /// <summary>
    /// Gets the list of failed rule execution results.
    /// </summary>
    public IReadOnlyList<RuleResult> Failures { get; }

    /// <summary>
    /// Initializes a new instance of BusinessRuleException.
    /// </summary>
    public BusinessRuleException(string message, IReadOnlyList<RuleResult> failures)
        : base(message)
    {
        Failures = failures;
    }
}
