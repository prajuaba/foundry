namespace Foundry.Rules;

/// <summary>
/// Result definition of a business rule execution.
/// </summary>
public record RuleResult(bool IsPassed, string? ErrorMessage = null, string? RuleCode = null)
{
    /// <summary>Creates a successful rule result.</summary>
    public static RuleResult Success() => new(true);

    /// <summary>Creates a failed rule result with the specified error message.</summary>
    public static RuleResult Failure(string message, string? code = null) => new(false, message, code);
}
