using System.Text.Json;
using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// Workflows crossing from the IR into the runtime manifest.
/// </summary>
/// <remarks>
/// <para>
/// The manifest is the only channel between the compiler and a running application, and
/// <c>Workflows</c> was never written to it. Every layer downstream was in place — the definition
/// provider, the state store, the pipeline behaviour, the generated commands — all waiting on a list
/// that arrived empty every time. A workflow declared in a schema was compiled, validated, and inert.
/// </para>
/// <para>
/// These assert the crossing itself, which is the part nothing covered.
/// </para>
/// </remarks>
public class WorkflowManifestTests
{
    private static SchemaModel SchemaWithWorkflow(params WorkflowTransitionModel[] transitions) => new()
    {
        Namespace = "Test.Domain",
        Entities =
        [
            new Entity
            {
                Name = "Order",
                Properties = [new Property { Name = "Id", Type = "ObjectId", IsKey = true }],
                ApiEnabledMethods = ["GET", "POST"]
            }
        ],
        Workflows =
        [
            new WorkflowModel
            {
                Id = "order-approval",
                Name = "Order Approval",
                Entity = "Order",
                Version = "1.0",
                IsActive = true,
                States =
                [
                    new WorkflowStateModel { Name = "Draft", IsInitial = true },
                    new WorkflowStateModel { Name = "Approved", IsFinal = true, AllowedRoles = ["Approver"] }
                ],
                Transitions = [.. transitions]
            }
        ]
    };

    private static WorkflowTransitionModel Approve() => new()
    {
        Id = "approve",
        Name = "Approve",
        FromState = "Draft",
        ToState = "Approved",
        Trigger = "ApproveOrder",
        RequiredRoles = ["Approver"]
    };

    private static JsonElement Generate(SchemaModel schema) =>
        JsonDocument.Parse(ApiManifestGenerator.Generate(schema)).RootElement.Clone();

    [Fact]
    public void AWorkflowReachesTheManifest()
    {
        var workflows = Generate(SchemaWithWorkflow(Approve())).GetProperty("Workflows");

        Assert.Equal(1, workflows.GetArrayLength());
        Assert.Equal("Order", workflows[0].GetProperty("Entity").GetString());
        Assert.Equal("order-approval", workflows[0].GetProperty("Id").GetString());
        Assert.True(workflows[0].GetProperty("IsActive").GetBoolean());
    }

    [Fact]
    public void StatesAndTheirFlagsSurvive()
    {
        // The engine adopts the initial state for an entity that has not entered the workflow, so
        // losing IsInitial would strand every new record outside its own workflow.
        var states = Generate(SchemaWithWorkflow(Approve())).GetProperty("Workflows")[0].GetProperty("States");

        Assert.Equal(2, states.GetArrayLength());
        Assert.True(states[0].GetProperty("IsInitial").GetBoolean());
        Assert.True(states[1].GetProperty("IsFinal").GetBoolean());
        Assert.Equal("Approver", states[1].GetProperty("AllowedRoles")[0].GetString());
    }

    [Fact]
    public void TransitionsCarryTheirStatesAndRoles()
    {
        var transition = Generate(SchemaWithWorkflow(Approve()))
            .GetProperty("Workflows")[0].GetProperty("Transitions")[0];

        Assert.Equal("approve", transition.GetProperty("Id").GetString());
        Assert.Equal("Draft", transition.GetProperty("FromState").GetString());
        Assert.Equal("Approved", transition.GetProperty("ToState").GetString());
        Assert.Equal("ApproveOrder", transition.GetProperty("Trigger").GetString());
        Assert.Equal("Approver", transition.GetProperty("RequiredRoles")[0].GetString());
    }

    [Fact]
    public void ATransitionWithNoIdIsKeyedByItsTrigger()
    {
        // The engine matches a request to a definition on Id. An empty one would match the first
        // transition in the workflow, or none -- and the generated command applies the same fallback,
        // so the two have to agree.
        var withoutId = Approve() with { Id = "" };

        var transition = Generate(SchemaWithWorkflow(withoutId))
            .GetProperty("Workflows")[0].GetProperty("Transitions")[0];

        Assert.Equal("ApproveOrder", transition.GetProperty("Id").GetString());
        Assert.Equal("ApproveOrder", ApiManifestGenerator.TransitionId(withoutId));
    }

    // ── Routes ──────────────────────────────────────────────────────────────

    [Fact]
    public void EachTransitionBecomesAnEndpoint()
    {
        // Without this the definitions arrive, the behaviour is registered, the commands are
        // generated -- and nothing can ever send one.
        var custom = Generate(SchemaWithWorkflow(Approve())).GetProperty("CustomEndpoints");

        Assert.Equal(1, custom.GetArrayLength());
        Assert.Equal("/api/orders/transitions/approveorder", custom[0].GetProperty("Route").GetString());
        Assert.Equal("POST", custom[0].GetProperty("Method").GetString());
    }

    [Fact]
    public void TheEndpointNamesTheGeneratedCommand()
    {
        // The command is emitted into '<namespace>.Commands', and the endpoint generator qualifies
        // the request type with the manifest namespace.
        var custom = Generate(SchemaWithWorkflow(Approve())).GetProperty("CustomEndpoints");

        Assert.Equal("Commands.ApproveOrder", custom[0].GetProperty("RequestType").GetString());
    }

    [Fact]
    public void TheTransitionsRolesGuardItsEndpoint()
    {
        // Enforced at the endpoint before the request reaches the pipeline, in addition to the
        // workflow engine's own permission check.
        var custom = Generate(SchemaWithWorkflow(Approve())).GetProperty("CustomEndpoints");

        Assert.Equal("Approver", custom[0].GetProperty("Roles")[0].GetString());
    }

    [Fact]
    public void TransitionRoutesAreDistinctPerTrigger()
    {
        var second = Approve() with { Id = "reject", Trigger = "RejectOrder", ToState = "Draft", RequiredRoles = [] };

        var custom = Generate(SchemaWithWorkflow(Approve(), second)).GetProperty("CustomEndpoints");

        Assert.Equal(2, custom.GetArrayLength());
        Assert.Equal("/api/orders/transitions/approveorder", custom[0].GetProperty("Route").GetString());
        Assert.Equal("/api/orders/transitions/rejectorder", custom[1].GetProperty("Route").GetString());
    }

    [Fact]
    public void ATransitionWithNoTriggerIsSkipped()
    {
        // Nothing to route to and no command to generate.
        var manifest = Generate(SchemaWithWorkflow(Approve() with { Trigger = "" }));

        Assert.Empty(manifest.GetProperty("CustomEndpoints").EnumerateArray());
        Assert.Empty(manifest.GetProperty("Workflows")[0].GetProperty("Transitions").EnumerateArray());
    }

    [Fact]
    public void ChoiceNodesCrossTheManifestBoundary()
    {
        // The same omission as the workflow list itself, one level down: the IR carries them, the
        // runtime config has a property for them, and the behaviour resolves them -- so dropping them
        // here would leave a declared decision gate silently absent, with its transition landing on
        // whatever target state it named.
        var schema = SchemaWithWorkflow(Approve());
        schema = schema with
        {
            Workflows =
            [
                schema.Workflows[0] with
                {
                    ChoiceNodes =
                    [
                        new WorkflowChoiceNodeModel
                        {
                            Id = "route-by-amount",
                            Name = "Route by amount",
                            Branches =
                            [
                                new WorkflowBranchModel
                                {
                                    TargetState = "Approved",
                                    Condition = new WorkflowConditionModel
                                    {
                                        Property = "Total", Operator = "LessThan", Value = "100"
                                    }
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var node = Generate(schema).GetProperty("Workflows")[0].GetProperty("ChoiceNodes")[0];

        Assert.Equal("route-by-amount", node.GetProperty("Id").GetString());

        var branch = node.GetProperty("Branches")[0];
        Assert.Equal("Approved", branch.GetProperty("ToState").GetString());
        Assert.Equal("Total", branch.GetProperty("Conditions")[0].GetProperty("Property").GetString());
        Assert.Equal("LessThan", branch.GetProperty("Conditions")[0].GetProperty("Operator").GetString());
    }

    [Fact]
    public void ASchemaWithNoWorkflowsEmitsAnEmptyList()
    {
        // Present and empty rather than absent: the provider reads the property, and a missing one
        // would be indistinguishable from a manifest written before workflows existed.
        var manifest = Generate(new SchemaModel
        {
            Namespace = "Test.Domain",
            Entities = [new Entity { Name = "Order", Properties = [new Property { Name = "Id", Type = "ObjectId", IsKey = true }], ApiEnabledMethods = ["GET"] }]
        });

        Assert.Empty(manifest.GetProperty("Workflows").EnumerateArray());
    }
}
