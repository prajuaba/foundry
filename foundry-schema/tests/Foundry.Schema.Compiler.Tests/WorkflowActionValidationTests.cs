using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// Validation rules for workflow action types and required fields.
/// </summary>
public class WorkflowActionValidationTests
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
    public void UnrecognizedActionType_IsAnError()
    {
        var workflow = WorkflowWithStatesAndTransitions(
            new List<WorkflowStateModel>
            {
                new() { Name = "Draft", IsInitial = true, AllowedRoles = new List<string> { "User" } },
                new() { Name = "Approved", IsFinal = true, AllowedRoles = new List<string> { "Admin" } }
            },
            new List<WorkflowTransitionModel>
            {
                new()
                {
                    Id = "approve",
                    Name = "Approve",
                    FromState = "Draft",
                    ToState = "Approved",
                    RequiredRoles = new List<string> { "Admin" },
                    Actions = new List<WorkflowActionModel>
                    {
                        new()
                        {
                            Type = "Webhook",
                            Url = "https://example.com/hook"
                        }
                    }
                }
            });

        var diagnostics = SchemaValidator.Validate(SchemaWithWorkflow(workflow));

        Assert.Contains(DiagnosticCatalog.WorkflowActionUnrecognizedType, diagnostics.Render());
        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void InternalApiMissingRequestType_IsAnError()
    {
        var workflow = WorkflowWithStatesAndTransitions(
            new List<WorkflowStateModel>
            {
                new() { Name = "Draft", IsInitial = true, AllowedRoles = new List<string> { "User" } },
                new() { Name = "Processing", IsFinal = true, AllowedRoles = new List<string> { "System" } }
            },
            new List<WorkflowTransitionModel>
            {
                new()
                {
                    Id = "process",
                    Name = "Process",
                    FromState = "Draft",
                    ToState = "Processing",
                    RequiredRoles = new List<string> { "System" },
                    Actions = new List<WorkflowActionModel>
                    {
                        new()
                        {
                            Type = "InternalApi",
                            RequestType = null
                        }
                    }
                }
            });

        var diagnostics = SchemaValidator.Validate(SchemaWithWorkflow(workflow));

        Assert.Contains(DiagnosticCatalog.WorkflowActionMissingRequestType, diagnostics.Render());
        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void ExternalApiMissingUrl_IsAnError()
    {
        var workflow = WorkflowWithStatesAndTransitions(
            new List<WorkflowStateModel>
            {
                new() { Name = "Draft", IsInitial = true, AllowedRoles = new List<string> { "User" } },
                new() { Name = "Notified", IsFinal = true, AllowedRoles = new List<string> { "System" } }
            },
            new List<WorkflowTransitionModel>
            {
                new()
                {
                    Id = "notify",
                    Name = "Notify",
                    FromState = "Draft",
                    ToState = "Notified",
                    RequiredRoles = new List<string> { "System" },
                    Actions = new List<WorkflowActionModel>
                    {
                        new()
                        {
                            Type = "ExternalApi",
                            Url = null
                        }
                    }
                }
            });

        var diagnostics = SchemaValidator.Validate(SchemaWithWorkflow(workflow));

        Assert.Contains(DiagnosticCatalog.WorkflowActionMissingUrl, diagnostics.Render());
        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void ValidInternalApiAction_ValidatesWithoutError()
    {
        var workflow = WorkflowWithStatesAndTransitions(
            new List<WorkflowStateModel>
            {
                new() { Name = "Draft", IsInitial = true, AllowedRoles = new List<string> { "User" } },
                new() { Name = "Processing", IsFinal = true, AllowedRoles = new List<string> { "System" } }
            },
            new List<WorkflowTransitionModel>
            {
                new()
                {
                    Id = "process",
                    Name = "Process",
                    FromState = "Draft",
                    ToState = "Processing",
                    RequiredRoles = new List<string> { "System" },
                    Actions = new List<WorkflowActionModel>
                    {
                        new()
                        {
                            Type = "InternalApi",
                            RequestType = "ProcessOrderCommand",
                            PayloadTemplate = "{\"id\": \"{{OrderId}}\"}"
                        }
                    }
                }
            });

        var diagnostics = SchemaValidator.Validate(SchemaWithWorkflow(workflow));

        var output = diagnostics.Render();
        Assert.DoesNotContain(DiagnosticCatalog.WorkflowActionUnrecognizedType, output);
        Assert.DoesNotContain(DiagnosticCatalog.WorkflowActionMissingRequestType, output);
        Assert.DoesNotContain(DiagnosticCatalog.WorkflowActionMissingUrl, output);
        Assert.False(diagnostics.HasErrors);
    }

    [Fact]
    public void ValidExternalApiAction_ValidatesWithoutError()
    {
        var workflow = WorkflowWithStatesAndTransitions(
            new List<WorkflowStateModel>
            {
                new() { Name = "Draft", IsInitial = true, AllowedRoles = new List<string> { "User" } },
                new() { Name = "Notified", IsFinal = true, AllowedRoles = new List<string> { "System" } }
            },
            new List<WorkflowTransitionModel>
            {
                new()
                {
                    Id = "notify",
                    Name = "Notify",
                    FromState = "Draft",
                    ToState = "Notified",
                    RequiredRoles = new List<string> { "System" },
                    Actions = new List<WorkflowActionModel>
                    {
                        new()
                        {
                            Type = "ExternalApi",
                            Url = "https://api.example.com/webhook",
                            Method = "POST",
                            BodyTemplate = "{\"status\": \"{{Status}}\"}"
                        }
                    }
                }
            });

        var diagnostics = SchemaValidator.Validate(SchemaWithWorkflow(workflow));

        var output = diagnostics.Render();
        Assert.DoesNotContain(DiagnosticCatalog.WorkflowActionUnrecognizedType, output);
        Assert.DoesNotContain(DiagnosticCatalog.WorkflowActionMissingRequestType, output);
        Assert.DoesNotContain(DiagnosticCatalog.WorkflowActionMissingUrl, output);
        Assert.False(diagnostics.HasErrors);
    }
}
