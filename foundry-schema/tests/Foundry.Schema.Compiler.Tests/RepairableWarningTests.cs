using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// Invariants for the repairable-warning allowlist, which decides what the AI repair loop is
/// allowed to spend an extra generation on.
/// </summary>
public class RepairableWarningTests
{
    [Fact]
    public void EveryRepairableCode_IsARealDocumentedCode()
    {
        foreach (var code in DiagnosticCatalog.RepairableWarnings)
        {
            Assert.True(
                DiagnosticCatalog.Descriptions.ContainsKey(code),
                $"'{code}' is in RepairableWarnings but has no entry in Descriptions. "
                + "A typo here silently disables the soft repair it was meant to enable.");
        }
    }

    /// <summary>
    /// The allowlist should stay small. Every entry costs a full extra generation whenever it
    /// fires, so growth needs to be a deliberate decision rather than a reflex.
    /// </summary>
    [Fact]
    public void AllowlistStaysNarrow()
    {
        Assert.InRange(DiagnosticCatalog.RepairableWarnings.Count, 1, 10);
    }

    /// <summary>
    /// The case that motivated the mechanism: a model declaring an enum then typing the property
    /// as a scalar. Valid, compiles, and not what anyone meant.
    /// </summary>
    [Fact]
    public void UnusedEnum_IsRepairable()
    {
        Assert.Contains(DiagnosticCatalog.UnusedEnum, DiagnosticCatalog.RepairableWarnings);
    }

    /// <summary>
    /// Errors already drive the loop unconditionally. Listing one here would be redundant and
    /// would suggest the allowlist governs more than it does.
    /// </summary>
    [Theory]
    [InlineData("FDY1001")] // MissingNamespace
    [InlineData("FDY1005")] // EntityNoKey
    [InlineData("FDY2014")] // DuplicateTypeName
    [InlineData("FDY4001")] // InvalidIdentifier
    public void ErrorCodes_AreNotInTheAllowlist(string errorCode)
    {
        Assert.DoesNotContain(errorCode, DiagnosticCatalog.RepairableWarnings);
    }

    /// <summary>
    /// A warning outside the allowlist must not reach the model, or the loop drifts back to
    /// chasing every advisory it sees.
    /// </summary>
    [Fact]
    public void AdvisoryOnlyWarnings_AreExcluded()
    {
        Assert.DoesNotContain(DiagnosticCatalog.UnknownType, DiagnosticCatalog.RepairableWarnings);
        Assert.DoesNotContain(DiagnosticCatalog.EntityNoProperties, DiagnosticCatalog.RepairableWarnings);
    }
}
