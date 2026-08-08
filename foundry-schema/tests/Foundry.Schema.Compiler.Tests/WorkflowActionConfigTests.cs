using System.Collections.Generic;
using System.Text.Json;
using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// Tests for workflow action configuration generation, including Retryable and CompensateWith fields.
/// </summary>
public class WorkflowActionConfigTests
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

    [Fact]
    public void WorkflowAction_WithRetryable_GeneratesConfigWithRetryableFlag()
    {
        // Arrange
        var action = new WorkflowActionModel
        {
            Type = "ExternalApi",
            Method = "POST",
            Url = "https://api.example.com/notify",
            Retryable = true,
            CompensateWith = null
        };

        var transition = new WorkflowTransitionModel
        {
            Id = "test-trans",
            Name = "Notify",
            FromState = "Draft",
            ToState = "Notified",
            Trigger = "NotifyOrder",
            Actions = new List<WorkflowActionModel> { action }
        };

        var workflow = new WorkflowModel
        {
            Id = "test-wf",
            Name = "TestWorkflow",
            Entity = "Order",
            Version = "1.0",
            IsActive = true,
            States = new List<WorkflowStateModel>
            {
                new() { Name = "Draft", IsInitial = true },
                new() { Name = "Notified", IsFinal = true }
            },
            Transitions = new List<WorkflowTransitionModel> { transition }
        };

        var schema = SchemaWithWorkflow(workflow);

        // Act
        var generated = PocoGenerator.Generate(schema);

        // Assert
        Assert.True(generated.ContainsKey("Workflow/WorkflowConfigurations"));
        var config = generated["Workflow/WorkflowConfigurations"];

        Assert.Contains("Retryable = true", config);
        Assert.Contains("Type = \"ExternalApi\"", config);
        Assert.Contains("Method = \"POST\"", config);
        Assert.Contains("Url = \"https://api.example.com/notify\"", config);
    }

    [Fact]
    public void WorkflowAction_WithoutRetryable_DefaultsToFalse()
    {
        // Arrange
        var action = new WorkflowActionModel
        {
            Type = "ExternalApi",
            Method = "POST",
            Url = "https://api.example.com/notify",
            Retryable = false,
            CompensateWith = null
        };

        var transition = new WorkflowTransitionModel
        {
            Id = "test-trans",
            Name = "Notify",
            FromState = "Draft",
            ToState = "Notified",
            Trigger = "NotifyOrder",
            Actions = new List<WorkflowActionModel> { action }
        };

        var workflow = new WorkflowModel
        {
            Id = "test-wf",
            Name = "TestWorkflow",
            Entity = "Order",
            Version = "1.0",
            IsActive = true,
            States = new List<WorkflowStateModel>
            {
                new() { Name = "Draft", IsInitial = true },
                new() { Name = "Notified", IsFinal = true }
            },
            Transitions = new List<WorkflowTransitionModel> { transition }
        };

        var schema = SchemaWithWorkflow(workflow);

        // Act
        var generated = PocoGenerator.Generate(schema);

        // Assert
        Assert.True(generated.ContainsKey("Workflow/WorkflowConfigurations"));
        var config = generated["Workflow/WorkflowConfigurations"];

        Assert.Contains("Retryable = false", config);
    }

    [Fact]
    public void WorkflowAction_WithCompensateWith_GeneratesNestedAction()
    {
        // Arrange
        var compensateAction = new WorkflowActionModel
        {
            Type = "InternalApi",
            RequestType = "RollbackNotificationCommand",
            PayloadTemplate = "{\"orderId\": \"${entity.Id}\"}",
            Retryable = false,
            CompensateWith = null
        };

        var primaryAction = new WorkflowActionModel
        {
            Type = "ExternalApi",
            Method = "POST",
            Url = "https://api.example.com/notify",
            Retryable = true,
            CompensateWith = compensateAction
        };

        var transition = new WorkflowTransitionModel
        {
            Id = "test-trans",
            Name = "Notify",
            FromState = "Draft",
            ToState = "Notified",
            Trigger = "NotifyOrder",
            Actions = new List<WorkflowActionModel> { primaryAction }
        };

        var workflow = new WorkflowModel
        {
            Id = "test-wf",
            Name = "TestWorkflow",
            Entity = "Order",
            Version = "1.0",
            IsActive = true,
            States = new List<WorkflowStateModel>
            {
                new() { Name = "Draft", IsInitial = true },
                new() { Name = "Notified", IsFinal = true }
            },
            Transitions = new List<WorkflowTransitionModel> { transition }
        };

        var schema = SchemaWithWorkflow(workflow);

        // Act
        var generated = PocoGenerator.Generate(schema);

        // Assert
        Assert.True(generated.ContainsKey("Workflow/WorkflowConfigurations"));
        var config = generated["Workflow/WorkflowConfigurations"];

        // Primary action should have CompensateWith set
        Assert.Contains("CompensateWith = new WorkflowActionConfig", config);
        Assert.Contains("Type = \"ExternalApi\"", config);
        Assert.Contains("Type = \"InternalApi\"", config);
        Assert.Contains("RequestType = \"RollbackNotificationCommand\"", config);
    }

    [Fact]
    public void WorkflowAction_RoundTripIrToSchema_PreservesNewFields()
    {
        // Arrange - Use a properly constructed schema instead of JSON parsing
        var compensateAction = new WorkflowActionModel
        {
            Type = "InternalApi",
            RequestType = "RollbackCommand",
            PayloadTemplate = "{\"orderId\":\"value\"}",
            Retryable = false,
            CompensateWith = null
        };

        var primaryAction = new WorkflowActionModel
        {
            Type = "ExternalApi",
            Method = "POST",
            Url = "https://api.example.com/notify",
            Retryable = true,
            CompensateWith = compensateAction
        };

        var transition = new WorkflowTransitionModel
        {
            Id = "test-trans",
            Name = "Notify",
            FromState = "Draft",
            ToState = "Notified",
            Trigger = "NotifyOrder",
            Actions = new List<WorkflowActionModel> { primaryAction }
        };

        var workflow = new WorkflowModel
        {
            Id = "test-wf",
            Name = "TestWorkflow",
            Entity = "Order",
            Version = "1.0",
            IsActive = true,
            States = new List<WorkflowStateModel>
            {
                new() { Name = "Draft", IsInitial = true },
                new() { Name = "Notified", IsFinal = true }
            },
            Transitions = new List<WorkflowTransitionModel> { transition }
        };

        var schema = SchemaWithWorkflow(workflow);

        // Act
        var generated = PocoGenerator.Generate(schema);

        // Assert
        Assert.True(generated.ContainsKey("Workflow/WorkflowConfigurations"));
        var config = generated["Workflow/WorkflowConfigurations"];

        // Verify round-trip preserved the data
        Assert.Contains("Retryable = true", config);
        Assert.Contains("CompensateWith = new WorkflowActionConfig", config);
        Assert.Contains("Type = \"ExternalApi\"", config);
        Assert.Contains("Type = \"InternalApi\"", config);
        Assert.Contains("RequestType = \"RollbackCommand\"", config);
    }
}
