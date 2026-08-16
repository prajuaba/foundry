using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// Conformance tests that guard against the recurring defect of declaring IR fields,
/// validating them, and then silently dropping them before they reach the manifest.
/// </summary>
/// <remarks>
/// <para>
/// Eight fields have historically been validated in the IR, reached intermediate
/// processing stages, and then vanished before being written to the manifest. Since
/// the manifest is the ONLY channel between the compiler and the running application,
/// an absent field reaches the runtime as "not declared", regardless of validation.
/// </para>
/// <para>
/// Instance 7 (custom endpoint roles) passed 1,291 tests by asserting route correctness
/// and method availability without checking that declared roles survived the trip.
/// 20 endpoints reached production with role lists that never appeared in the manifest,
/// served to any authenticated caller, and shipped in v2.0.0.
/// </para>
/// <para>
/// These tests make instance 9 impossible to ship by:
/// (1) asserting that every access-bearing field carries its value to the manifest
///     at the exact JSON path where the runtime reads it, using sentinel values
///     that make a missing field unambiguous.
/// (2) failing when a new public property appears on an access-bearing IR type,
///     forcing a deliberate decision: the new field goes to the manifest, to generated
///     code attributes, nowhere by design, or is not access-bearing. Without this,
///     new fields are added quietly and tested against the old tooling.
/// </para>
/// </remarks>
public class ManifestConformanceTests
{
    // ---- Part 1: Conformance Test ----
    // Each row asserts that one IR field carries its value to the manifest.

    public record ConformanceCase(
        string Description,
        SchemaModel Schema,
        string JsonPath,
        string ExpectedValue
    );

    private static List<ConformanceCase> ConformanceCases() =>
        new()
        {
            // Entity CRUD access control
            new("Entity.ApiEnabledMethods[0] -> Endpoints[0].Methods[0]",
                EntityCrudSchema(),
                "$.Endpoints[0].Methods[0]",
                "GET"),

            new("Entity.ApiRoles[GET] -> Endpoints[0].Roles.GET[0]",
                EntityCrudSchema(),
                "$.Endpoints[0].Roles.GET[0]",
                "SENTINEL_Customer_GET_ROLE"),

            new("Entity.ApiBusinessRules[POST] -> Endpoints[0].BusinessRules.POST[0]",
                EntityCrudSchema(),
                "$.Endpoints[0].BusinessRules.POST[0]",
                "SENTINEL_Customer_POST_RULE"),

            new("Entity.ApiCaching[GET].Enabled -> Endpoints[0].Caching.GET.Enabled",
                EntityCrudSchema(),
                "$.Endpoints[0].Caching.GET.Enabled",
                "true"),

            new("Entity.ApiCaching[GET].TtlSeconds -> Endpoints[0].Caching.GET.TtlSeconds",
                EntityCrudSchema(),
                "$.Endpoints[0].Caching.GET.TtlSeconds",
                "3600"),

            new("Entity.GraphQlEnabled -> Endpoints[0].GraphQL",
                EntityCrudSchema(),
                "$.Endpoints[0].GraphQL",
                "true"),

            // Custom endpoint access control
            new("CustomEndpoint.Route -> CustomEndpoints[0].Route",
                CustomEndpointSchema(),
                "$.CustomEndpoints[0].Route",
                "/api/custom/test"),

            new("CustomEndpoint.Method -> CustomEndpoints[0].Method",
                CustomEndpointSchema(),
                "$.CustomEndpoints[0].Method",
                "POST"),

            new("CustomEndpoint.RequestType -> CustomEndpoints[0].RequestType",
                CustomEndpointSchema(),
                "$.CustomEndpoints[0].RequestType",
                "TestCommand"),

            new("CustomEndpoint.Roles[0] -> CustomEndpoints[0].Roles[0]",
                CustomEndpointSchema(),
                "$.CustomEndpoints[0].Roles[0]",
                "SENTINEL_CustomEndpoint_ROLE"),

            new("CustomEndpoint.BusinessRules[0] -> CustomEndpoints[0].BusinessRules[0]",
                CustomEndpointSchema(),
                "$.CustomEndpoints[0].BusinessRules[0]",
                "SENTINEL_CustomEndpoint_RULE"),

            // Workflow state access control
            new("WorkflowStateModel.AllowedRoles[0] -> Workflows[0].States[0].AllowedRoles[0]",
                WorkflowSchema(),
                "$.Workflows[0].States[0].AllowedRoles[0]",
                "SENTINEL_State_ROLE"),

            // Workflow transition access control
            new("WorkflowTransitionModel.RequiredRoles[0] -> Workflows[0].Transitions[0].RequiredRoles[0]",
                WorkflowSchema(),
                "$.Workflows[0].Transitions[0].RequiredRoles[0]",
                "SENTINEL_Transition_ROLE"),

            // Workflow transition conditions
            new("WorkflowConditionModel.Property -> Workflows[0].Transitions[0].Conditions[0].Property",
                WorkflowSchema(),
                "$.Workflows[0].Transitions[0].Conditions[0].Property",
                "SENTINEL_Condition_PROPERTY"),

            new("WorkflowConditionModel.Operator -> Workflows[0].Transitions[0].Conditions[0].Operator",
                WorkflowSchema(),
                "$.Workflows[0].Transitions[0].Conditions[0].Operator",
                "SENTINEL_Condition_OPERATOR"),

            new("WorkflowConditionModel.Value -> Workflows[0].Transitions[0].Conditions[0].Value",
                WorkflowSchema(),
                "$.Workflows[0].Transitions[0].Conditions[0].Value",
                "SENTINEL_Condition_VALUE"),

            new("WorkflowConditionModel.Source -> Workflows[0].Transitions[0].Conditions[0].Source",
                WorkflowSchema(),
                "$.Workflows[0].Transitions[0].Conditions[0].Source",
                "request"),

            // Workflow choice node
            new("WorkflowChoiceNodeModel.DefaultState -> Workflows[0].ChoiceNodes[0].DefaultState",
                WorkflowSchema(),
                "$.Workflows[0].ChoiceNodes[0].DefaultState",
                "SENTINEL_ChoiceNode_DefaultState"),

            new("WorkflowChoiceNodeModel.Branches[0].TargetState -> Workflows[0].ChoiceNodes[0].Branches[0].ToState",
                WorkflowSchema(),
                "$.Workflows[0].ChoiceNodes[0].Branches[0].ToState",
                "SENTINEL_Branch_TargetState"),

            // Workflow transition endpoint (derived custom endpoint). WorkflowSchema declares no
            // customEndpoints of its own, so the compiler-derived transition route is the first
            // entry rather than following one.
            new("WorkflowTransitionModel.RequiredRoles -> CustomEndpoints[0].Roles (transition endpoint)",
                WorkflowSchema(),
                "$.CustomEndpoints[0].Roles[0]",
                "SENTINEL_Transition_ROLE"),
        };

    private static SchemaModel EntityCrudSchema() =>
        new()
        {
            Namespace = "Conformance.Test",
            Entities =
            [
                new Entity
                {
                    Name = "Customer",
                    Properties = [new Property { Name = "Id", Type = "ObjectId", IsKey = true }],
                    ApiEnabledMethods = ["GET", "POST"],
                    ApiRoles = new()
                    {
                        ["GET"] = ["SENTINEL_Customer_GET_ROLE"]
                    },
                    ApiBusinessRules = new()
                    {
                        ["POST"] = ["SENTINEL_Customer_POST_RULE"]
                    },
                    ApiCaching = new()
                    {
                        ["GET"] = new ApiCachingConfig { Enabled = true, TtlSeconds = 3600 }
                    },
                    GraphQlEnabled = true
                }
            ]
        };

    private static SchemaModel CustomEndpointSchema() =>
        new()
        {
            Namespace = "Conformance.Test",
            CustomEndpoints =
            [
                new CustomEndpoint
                {
                    Route = "/api/custom/test",
                    Method = "POST",
                    RequestType = "TestCommand",
                    Roles = ["SENTINEL_CustomEndpoint_ROLE"],
                    BusinessRules = ["SENTINEL_CustomEndpoint_RULE"]
                }
            ]
        };

    private static SchemaModel WorkflowSchema() =>
        new()
        {
            Namespace = "Conformance.Test",
            Workflows =
            [
                new WorkflowModel
                {
                    Id = "TestWorkflow",
                    Name = "TestWorkflow",
                    Entity = "Order",
                    IsActive = true,
                    States =
                    [
                        new WorkflowStateModel
                        {
                            Name = "Pending",
                            IsInitial = true,
                            AllowedRoles = ["SENTINEL_State_ROLE"]
                        }
                    ],
                    Transitions =
                    [
                        new WorkflowTransitionModel
                        {
                            Trigger = "Approve",
                            FromState = "Pending",
                            ToState = "Approved",
                            RequiredRoles = ["SENTINEL_Transition_ROLE"],
                            Conditions =
                            [
                                new WorkflowConditionModel
                                {
                                    Property = "SENTINEL_Condition_PROPERTY",
                                    Operator = "SENTINEL_Condition_OPERATOR",
                                    Value = "SENTINEL_Condition_VALUE",
                                    Source = "request"
                                }
                            ]
                        }
                    ],
                    ChoiceNodes =
                    [
                        new WorkflowChoiceNodeModel
                        {
                            Id = "DecisionGate",
                            DefaultState = "SENTINEL_ChoiceNode_DefaultState",
                            Branches =
                            [
                                new WorkflowBranchModel
                                {
                                    TargetState = "SENTINEL_Branch_TargetState",
                                    Condition = new WorkflowConditionModel { Property = "Test" }
                                }
                            ]
                        }
                    ]
                }
            ]
        };

    /// <summary>
    /// Every access-bearing IR field must carry its value to the manifest at the exact JSON path
    /// where the runtime reads it. Missing or misrouted values are silent defects at runtime.
    /// </summary>
    [Theory]
    [MemberData(nameof(GetConformanceCases))]
    public void AccessBearingFieldsReachManifestAtCorrectJsonPath(ConformanceCase test)
    {
        var manifest = JsonDocument.Parse(ApiManifestGenerator.Generate(test.Schema))
            .RootElement
            .Clone();

        // Navigate the JSON path and assert the value
        var actual = GetValueAtJsonPath(manifest, test.JsonPath);

        Assert.NotNull(actual);
        Assert.Equal(test.ExpectedValue, actual);
    }

    public static IEnumerable<object[]> GetConformanceCases() =>
        ConformanceCases().Select(c => new object[] { c }).ToArray();

    /// <summary>
    /// Simple JSON path navigator for testing (understands $, [N], and .Key).
    /// </summary>
    private static string? GetValueAtJsonPath(JsonElement root, string path)
    {
        var current = root;
        var parts = path.Split('.');

        foreach (var part in parts.Skip(1)) // Skip the '$'
        {
            if (part.EndsWith(']'))
            {
                var bracketIndex = part.IndexOf('[');
                var propName = part.Substring(0, bracketIndex);
                var index = int.Parse(part.Substring(bracketIndex + 1, part.Length - bracketIndex - 2));

                if (!string.IsNullOrEmpty(propName))
                {
                    current = current.GetProperty(propName);
                }

                current = current[index];
            }
            else if (!string.IsNullOrEmpty(part))
            {
                current = current.GetProperty(part);
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => current.GetRawText(),
            _ => null
        };
    }

    // ---- Part 2: Tripwire Test ----
    // When a new public property appears on an access-bearing IR type, this fails and names it.
    // A developer must then decide where it goes and add conformance coverage if it is manifest-borne.

    /// <summary>
    /// Detects when new public properties are added to access-bearing IR model types
    /// without a deliberate decision about where they are manifest-borne.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eight fields have historically been added to IR types, validated correctly,
    /// and then silently dropped before reaching the manifest:
    /// 1. Entity.ApiEnabledMethods / ApiRoles / ApiCaching — Studio wrote them, compiler discarded
    /// 2. CustomEndpoint.FilterOperator — dropped on load
    /// 3. Entity.Indexes — parsed, validated, dropped
    /// 4. Entity.KafkaTopic — dropped by publisher
    /// 5. WorkflowModel — entire type absent from manifest
    /// 6. WorkflowChoiceNodeModel.DefaultState — hardcoded empty
    /// 7. CustomEndpoint.Roles — hardcoded empty array, shipped in v2.0.0
    /// 8. CustomEndpoint.BusinessRules — hardcoded empty array, same as #7
    /// </para>
    /// <para>
    /// This test makes the ninth impossible. When a developer adds a property to one of these
    /// types, this test fails immediately and reports the new properties by name. They must then:
    /// 1. Decide if the field is access-bearing (controls runtime behavior)
    /// 2. If yes, add it to the manifest generator and write a conformance test
    /// 3. Add the new properties to the expected set below
    /// 4. Run this test again to confirm it passes
    /// </para>
    /// </remarks>
    [Fact]
    public void NewPropertiesOnAccessBearingTypesAreDetected()
    {
        var accessBearingTypes = new Dictionary<Type, HashSet<string>>
        {
            // Entity properties that influence API surface and access control
            {
                typeof(Entity),
                new()
                {
                    nameof(Entity.Name),
                    nameof(Entity.Description),
                    nameof(Entity.Properties),
                    nameof(Entity.Indexes),
                    nameof(Entity.BaseClass),
                    nameof(Entity.SoftDelete),
                    nameof(Entity.Auditable),
                    nameof(Entity.Partitioned),
                    nameof(Entity.ArchiveThresholdYears),
                    nameof(Entity.RealTime),
                    nameof(Entity.RealTimeRoles),
                    nameof(Entity.KafkaOutboxEnabled),
                    nameof(Entity.KafkaTopic),
                    nameof(Entity.FileIoEnabled),
                    nameof(Entity.FileIoAllowedExtensions),
                    nameof(Entity.GraphQlEnabled),
                    nameof(Entity.MultiTenant),
                    nameof(Entity.TenantProperty),
                    nameof(Entity.OwnerScoped),
                    nameof(Entity.OwnerExemptRoles),
                    nameof(Entity.OwnerReadExemptRoles),
                    nameof(Entity.ApiEnabledMethods),
                    nameof(Entity.ApiRoles),
                    nameof(Entity.ApiBusinessRules),
                    nameof(Entity.ApiCaching)
                }
            },

            // CustomEndpoint properties that define routing and access control
            {
                typeof(CustomEndpoint),
                new()
                {
                    nameof(CustomEndpoint.Method),
                    nameof(CustomEndpoint.Route),
                    nameof(CustomEndpoint.RequestType),
                    nameof(CustomEndpoint.TargetEntity),
                    nameof(CustomEndpoint.OperationType),
                    nameof(CustomEndpoint.FilterField),
                    nameof(CustomEndpoint.FilterOperator),
                    nameof(CustomEndpoint.FilterSourceValue),
                    nameof(CustomEndpoint.Assignments),
                    nameof(CustomEndpoint.Roles),
                    nameof(CustomEndpoint.BusinessRules),
                    nameof(CustomEndpoint.Description)
                }
            },

            // WorkflowModel carries the entire workflow definition to the manifest
            {
                typeof(WorkflowModel),
                new()
                {
                    nameof(WorkflowModel.Id),
                    nameof(WorkflowModel.Name),
                    nameof(WorkflowModel.Entity),
                    nameof(WorkflowModel.Version),
                    nameof(WorkflowModel.EffectiveDate),
                    nameof(WorkflowModel.ExpirationDate),
                    nameof(WorkflowModel.IsActive),
                    nameof(WorkflowModel.States),
                    nameof(WorkflowModel.Transitions),
                    nameof(WorkflowModel.ChoiceNodes),
                    nameof(WorkflowModel.Description)
                }
            },

            // WorkflowStateModel carries state definitions including allowed roles
            {
                typeof(WorkflowStateModel),
                new()
                {
                    nameof(WorkflowStateModel.Name),
                    nameof(WorkflowStateModel.IsInitial),
                    nameof(WorkflowStateModel.IsFinal),
                    nameof(WorkflowStateModel.AllowedRoles)
                }
            },

            // WorkflowTransitionModel defines routing and access control
            {
                typeof(WorkflowTransitionModel),
                new()
                {
                    nameof(WorkflowTransitionModel.Id),
                    nameof(WorkflowTransitionModel.Name),
                    nameof(WorkflowTransitionModel.FromState),
                    nameof(WorkflowTransitionModel.ToState),
                    nameof(WorkflowTransitionModel.Trigger),
                    nameof(WorkflowTransitionModel.UseCustomCommand),
                    nameof(WorkflowTransitionModel.RequiredRoles),
                    nameof(WorkflowTransitionModel.Conditions),
                    nameof(WorkflowTransitionModel.Actions)
                }
            },

            // WorkflowConditionModel carries guards and conditions
            {
                typeof(WorkflowConditionModel),
                new()
                {
                    nameof(WorkflowConditionModel.Type),
                    nameof(WorkflowConditionModel.Property),
                    nameof(WorkflowConditionModel.Operator),
                    nameof(WorkflowConditionModel.Value),
                    nameof(WorkflowConditionModel.Source)
                }
            },

            // WorkflowChoiceNodeModel carries decision gates including default state
            {
                typeof(WorkflowChoiceNodeModel),
                new()
                {
                    nameof(WorkflowChoiceNodeModel.Id),
                    nameof(WorkflowChoiceNodeModel.Name),
                    nameof(WorkflowChoiceNodeModel.DefaultState),
                    nameof(WorkflowChoiceNodeModel.Branches)
                }
            },

            // WorkflowBranchModel carries branch targets and conditions
            {
                typeof(WorkflowBranchModel),
                new()
                {
                    nameof(WorkflowBranchModel.Id),
                    nameof(WorkflowBranchModel.Condition),
                    nameof(WorkflowBranchModel.TargetState)
                }
            }
        };

        var errors = new List<string>();

        foreach (var (type, expectedProperties) in accessBearingTypes)
        {
            var actualProperties = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .ToHashSet();

            var newProperties = actualProperties.Except(expectedProperties).ToList();
            if (newProperties.Count > 0)
            {
                errors.Add(
                    $"New properties found on {type.Name}: {string.Join(", ", newProperties)}. " +
                    $"Decide if each is access-bearing (controls runtime behavior via the manifest). " +
                    $"If yes, update {nameof(ApiManifestGenerator)} to carry it, write a conformance test, " +
                    $"and add it to the expected set in this test. Then update this list to include: " +
                    $"{string.Join(", ", newProperties.Select(p => $"nameof({type.Name}.{p})"))}"
                );
            }

            var removedProperties = expectedProperties.Except(actualProperties).ToList();
            if (removedProperties.Count > 0)
            {
                errors.Add(
                    $"Properties removed from {type.Name}: {string.Join(", ", removedProperties)}. " +
                    $"Update this test's expected set to remove them."
                );
            }
        }

        // Assert.True with a joined message rather than Assert.Empty: this test's whole value is the
        // instruction it prints, and xUnit truncates collection elements in an Assert.Empty failure
        // -- the reader saw "New properties found on Entity: Probe. Decide if e..." and lost the part
        // telling them what to do about it.
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }
}
