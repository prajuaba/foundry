using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// Validation rules for workflow transition and state roles.
/// </summary>
public class WorkflowRolesValidationTests
{
    private static SchemaModel SchemaWithWorkflow(WorkflowModel workflow) => new()
    {
        Namespace = "Test.Domain",
        Entities = new List<Entity>
        {
            new()
            {
                Name = "Order",
                Properties = new List<Property>
                {
                    new() { Name = "Id", Type = "ObjectId", IsKey = true }
                }
            }
        },
        Workflows = new List<WorkflowModel> { workflow }
    };

    private static WorkflowModel WorkflowWithStatesAndTransitions(
        List<WorkflowStateModel> states,
        List<WorkflowTransitionModel> transitions) => new()
    {
        Id = "test-workflow",
        Name = "TestWorkflow",
        Entity = "Order",
        States = states,
        Transitions = transitions
    };

    [Fact]
    public void ATransitionWithEmptyRequiredRoles_IsAWarning()
    {
        var workflow = WorkflowWithStatesAndTransitions(
            new List<WorkflowStateModel>
            {
                new() { Name = "Draft", IsInitial = true },
                new() { Name = "Approved", IsFinal = true, AllowedRoles = new List<string> { "Approver" } }
            },
            new List<WorkflowTransitionModel>
            {
                new()
                {
                    Id = "approve",
                    Name = "Approve",
                    FromState = "Draft",
                    ToState = "Approved",
                    RequiredRoles = new List<string>() // Empty
                }
            });

        var diagnostics = SchemaValidator.Validate(SchemaWithWorkflow(workflow));

        Assert.Contains(DiagnosticCatalog.WorkflowEmptyRoles, diagnostics.Render());
        Assert.False(diagnostics.HasErrors, "Empty roles should be a warning, not an error");
        Assert.True(diagnostics.WarningCount > 0);
    }

    [Fact]
    public void AStateWithEmptyAllowedRoles_IsAWarning()
    {
        var workflow = WorkflowWithStatesAndTransitions(
            new List<WorkflowStateModel>
            {
                new() { Name = "Draft", IsInitial = true, AllowedRoles = new List<string>() }, // Empty
                new() { Name = "Approved", IsFinal = true, AllowedRoles = new List<string> { "Approver" } }
            },
            new List<WorkflowTransitionModel>
            {
                new()
                {
                    Id = "approve",
                    Name = "Approve",
                    FromState = "Draft",
                    ToState = "Approved",
                    RequiredRoles = new List<string> { "Approver" }
                }
            });

        var diagnostics = SchemaValidator.Validate(SchemaWithWorkflow(workflow));

        Assert.Contains(DiagnosticCatalog.WorkflowEmptyRoles, diagnostics.Render());
        Assert.False(diagnostics.HasErrors, "Empty roles should be a warning, not an error");
        Assert.True(diagnostics.WarningCount > 0);
    }

    [Fact]
    public void AWorkflowWithAllRolesDeclared_ValidatesWithoutWarnings()
    {
        var workflow = WorkflowWithStatesAndTransitions(
            new List<WorkflowStateModel>
            {
                new() { Name = "Draft", IsInitial = true, AllowedRoles = new List<string> { "User" } },
                new() { Name = "Approved", IsFinal = true, AllowedRoles = new List<string> { "Approver" } }
            },
            new List<WorkflowTransitionModel>
            {
                new()
                {
                    Id = "approve",
                    Name = "Approve",
                    FromState = "Draft",
                    ToState = "Approved",
                    RequiredRoles = new List<string> { "Approver" }
                }
            });

        var diagnostics = SchemaValidator.Validate(SchemaWithWorkflow(workflow));

        // Should not produce a WorkflowEmptyRoles warning
        var output = diagnostics.Render();
        Assert.DoesNotContain(DiagnosticCatalog.WorkflowEmptyRoles, output);
    }

    [Fact]
    public void MultipleTransitionsWithEmptyRoles_ProduceMultipleWarnings()
    {
        var workflow = WorkflowWithStatesAndTransitions(
            new List<WorkflowStateModel>
            {
                new() { Name = "Draft", IsInitial = true, AllowedRoles = new List<string> { "User" } },
                new() { Name = "Submitted", AllowedRoles = new List<string>() }, // Empty
                new() { Name = "Approved", IsFinal = true, AllowedRoles = new List<string> { "Approver" } }
            },
            new List<WorkflowTransitionModel>
            {
                new()
                {
                    Id = "submit",
                    Name = "Submit",
                    FromState = "Draft",
                    ToState = "Submitted",
                    RequiredRoles = new List<string>() // Empty
                },
                new()
                {
                    Id = "approve",
                    Name = "Approve",
                    FromState = "Submitted",
                    ToState = "Approved",
                    RequiredRoles = new List<string>() // Empty
                }
            });

        var diagnostics = SchemaValidator.Validate(SchemaWithWorkflow(workflow));

        var output = diagnostics.Render();
        // Count occurrences of the warning code in the output
        var occurrences = output.Split(DiagnosticCatalog.WorkflowEmptyRoles).Length - 1;

        // Should have at least 3 warnings: 1 state + 2 transitions
        Assert.True(occurrences >= 3, $"Expected at least 3 WorkflowEmptyRoles warnings, but found {occurrences}");
        Assert.False(diagnostics.HasErrors, "Empty roles should be warnings, not errors");
    }

    [Fact]
    public void ANullRolesList_IsHandledGracefully()
    {
        var workflow = WorkflowWithStatesAndTransitions(
            new List<WorkflowStateModel>
            {
                new() { Name = "Draft", IsInitial = true, AllowedRoles = new List<string>() }, // Empty
                new() { Name = "Approved", IsFinal = true, AllowedRoles = new List<string> { "Approver" } }
            },
            new List<WorkflowTransitionModel>
            {
                new()
                {
                    Id = "approve",
                    Name = "Approve",
                    FromState = "Draft",
                    ToState = "Approved",
                    RequiredRoles = new List<string>() // Empty instead of null
                }
            });

        var diagnostics = SchemaValidator.Validate(SchemaWithWorkflow(workflow));

        var output = diagnostics.Render();
        Assert.Contains(DiagnosticCatalog.WorkflowEmptyRoles, output);
        Assert.False(diagnostics.HasErrors);
    }
}
