using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// Guards the eval suite itself. A harness that miscounts or crashes is worse than none, because
/// it produces a number people trust.
/// </summary>
public class EvalHarnessTests
{
    [Fact]
    public void Cases_HaveUniqueIds()
    {
        var duplicates = EvalHarness.Cases
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, $"Duplicate case ids: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void Cases_AreWellFormed()
    {
        Assert.NotEmpty(EvalHarness.Cases);

        foreach (var c in EvalHarness.Cases)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Id), "case id must be set");
            Assert.False(string.IsNullOrWhiteSpace(c.Construct), $"{c.Id}: construct must be set");
            Assert.False(string.IsNullOrWhiteSpace(c.Prompt), $"{c.Id}: prompt must be set");
            Assert.True(c.Assertions.Count > 0, $"{c.Id}: needs at least one assertion");

            foreach (var a in c.Assertions)
                Assert.False(string.IsNullOrWhiteSpace(a.Description), $"{c.Id}: assertion needs a description");
        }
    }

    /// <summary>
    /// A model can omit anything, so no assertion may throw on a sparse or empty document —
    /// a harness crash would be indistinguishable from a model failure.
    /// </summary>
    [Fact]
    public void Assertions_DoNotThrowOnEmptyOrSparseDocuments()
    {
        var documents = new[]
        {
            new SchemaModel(),
            new SchemaModel { Namespace = "X" },
            new SchemaModel
            {
                Namespace = "X",
                Entities = new List<Entity> { new() { Name = "Thing" } }
            },
            new SchemaModel
            {
                Namespace = "X",
                Entities = new List<Entity>
                {
                    new()
                    {
                        Name = "Thing",
                        Properties = new List<Property> { new() { Name = "Id", Type = "ObjectId", IsKey = true } }
                    }
                },
                Workflows = new List<WorkflowModel> { new() { Name = "W" } },
                Dtos = new List<DtoModel> { new() { Name = "D" } },
                CustomEndpoints = new List<CustomEndpoint> { new() },
                Connectors = new List<ConnectorModel> { new() }
            }
        };

        foreach (var doc in documents)
        {
            foreach (var c in EvalHarness.Cases)
            {
                foreach (var a in c.Assertions)
                {
                    var exception = Record.Exception(() => a.Check(doc));
                    Assert.True(exception is null, $"{c.Id} / '{a.Description}' threw: {exception}");
                }
            }
        }
    }

    /// <summary>
    /// The baseline handed to the modification case must itself be valid, or that case measures
    /// the harness rather than the model.
    /// </summary>
    [Fact]
    public void ModificationBaseline_IsValidIr()
    {
        var bag = SchemaValidator.Validate(EvalHarness.ModificationBaseline);
        Assert.True(!bag.HasErrors, $"Modification baseline is invalid:\n{bag.Render()}");
    }

    /// <summary>
    /// The modification case asserts that a pre-existing property survives, so the baseline must
    /// actually contain it — otherwise the assertion passes or fails for the wrong reason.
    /// </summary>
    [Fact]
    public void ModificationBaseline_ContainsThePropertyTheCaseExpectsPreserved()
    {
        var product = EvalHarness.ModificationBaseline.Entities.Single(e => e.Name == "Product");
        Assert.Contains(product.Properties, p => p.Name == "Sku");
    }

    [Fact]
    public void PassRate_CountsOnlyFullyPassingRuns()
    {
        var run = new EvalRunResult
        {
            Results = new List<EvalCaseResult>
            {
                new()
                {
                    CaseId = "a", ProducedValidIr = true,
                    Assertions = new List<EvalAssertionResult> { new("x", true) }
                },
                new()
                {
                    // Valid IR but a failed assertion is still a failure.
                    CaseId = "b", ProducedValidIr = true,
                    Assertions = new List<EvalAssertionResult> { new("x", true), new("y", false) }
                },
                new()
                {
                    // Assertions can pass coincidentally on a best-effort document that never
                    // validated; that must not count as a pass.
                    CaseId = "c", ProducedValidIr = false,
                    Assertions = new List<EvalAssertionResult> { new("x", true) }
                },
                new()
                {
                    CaseId = "d", ProducedValidIr = true,
                    Assertions = new List<EvalAssertionResult> { new("x", true) }
                }
            }
        };

        Assert.Equal(0.5, run.PassRate);
        Assert.Equal(0.75, run.ValidIrRate);
    }
}
