using System.Security.Claims;
using Foundry.Core.User;
using Foundry.Rules;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Xunit;

namespace Foundry.Rules.Tests;

/// <summary>
/// The workflow transition pipeline, end to end with fakes.
/// </summary>
/// <remarks>
/// This orchestration was previously untestable. It located the API manifest, the current-user context,
/// the entity's CLR type and <c>IRepository&lt;T&gt;</c> by scanning <c>AppDomain</c> for simple-name
/// matches and invoking methods through <c>MethodInfo</c>, so exercising it required a real MongoDB
/// repository and the API assembly loaded in-process. Every collaborator is now injected, which is what
/// makes the cases below reachable at all.
/// </remarks>
public class WorkflowTransitionBehaviorTests
{
    // ---- test doubles ----

    private sealed record TransitionCommand : IRequest<Unit>, IWorkflowTransitionRequest
    {
        public string EntityId { get; init; } = "6a65ba09986eed749ed7e968";
        public string EntityType { get; init; } = "Order";
        public string TransitionId { get; init; } = "submit";
        public string FromState { get; init; } = "Draft";
        public string ToState { get; init; } = "Submitted";
        public decimal TotalAmount { get; init; } = 100m;
    }

    private sealed record PlainCommand : IRequest<Unit>;

    private sealed class StatefulEntity : IWorkflowStateful
    {
        public string CurrentState { get; set; } = "Draft";
        public string? WorkflowId { get; set; }
        public string? WorkflowVersion { get; set; }
        public decimal TotalAmount { get; set; } = 100m;
    }

    private sealed class FakeDefinitions(params WorkflowConfig[] workflows) : IWorkflowDefinitionProvider
    {
        public IReadOnlyList<WorkflowConfig> GetWorkflows() => workflows;
    }

    private sealed class FakeStore(IWorkflowStateful? entity) : IWorkflowStateStore
    {
        public IWorkflowStateful? Entity { get; } = entity;
        public List<IWorkflowStateful> Saved { get; } = [];
        public List<WorkflowActivityLog> Logs { get; } = [];
        public bool ThrowOnLoad { get; init; }

        public Task<IWorkflowStateful?> LoadAsync(string entityTypeName, string entityId, CancellationToken ct = default)
        {
            if (ThrowOnLoad) throw new WorkflowException($"'{entityTypeName}' is not registered.");
            return Task.FromResult(Entity);
        }

        public Task SaveAsync(string entityTypeName, IWorkflowStateful entity, CancellationToken ct = default)
        {
            Saved.Add(entity);
            return Task.CompletedTask;
        }

        public Task AppendActivityLogAsync(WorkflowActivityLog log, CancellationToken ct = default)
        {
            Logs.Add(log);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkflowActivityLog>> ReadActivityLogAsync(
            string entityTypeName, string entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkflowActivityLog>>(
                Logs.Where(l => l.EntityId == entityId && l.EntityType == entityTypeName).ToList());
    }

    private sealed class FakeUser(string operatorId, params string[] roles) : ICurrentUserContext
    {
        public string OperatorId => operatorId;
        public string? OperatorName => operatorId;

        public ClaimsPrincipal? User => new(new ClaimsIdentity(
            roles.Select(r => new Claim(ClaimTypes.Role, r))));
    }

    // ---- fixtures ----

    private static WorkflowConfig OrderWorkflow(
        List<WorkflowTransitionConfig>? transitions = null,
        List<WorkflowStateConfig>? states = null,
        List<WorkflowChoiceNodeConfig>? choiceNodes = null,
        bool isActive = true,
        string? effectiveDate = null,
        string? expirationDate = null) => new()
        {
            Id = "order_wf",
            Name = "Order Workflow",
            Entity = "Order",
            Version = "1.0.0",
            IsActive = isActive,
            EffectiveDate = effectiveDate,
            ExpirationDate = expirationDate,
            States = states ?? [new WorkflowStateConfig { Name = "Draft", IsInitial = true }],
            Transitions = transitions ??
            [
                new WorkflowTransitionConfig { Id = "submit", FromState = "Draft", ToState = "Submitted" }
            ],
            ChoiceNodes = choiceNodes ?? []
        };

    private static WorkflowEngine Engine() => new(new EmptyProvider());

    private sealed class EmptyProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static WorkflowTransitionBehavior<TRequest, Unit> Behavior<TRequest>(
        IWorkflowDefinitionProvider definitions,
        IWorkflowStateStore store,
        ICurrentUserContext? user = null)
        where TRequest : IRequest<Unit>
        => new(Engine(), definitions, store, user);

    private static Task<Unit> Next(Action? onCalled = null)
    {
        onCalled?.Invoke();
        return Task.FromResult(Unit.Value);
    }

    // ---- decision gates ----
    //
    // Emitted by the compiler, carried by the manifest, resolved here -- and never driven. The
    // transition names the gate as its target state and the gate routes to a real one.

    private static WorkflowConfig GatedWorkflow(string? defaultState) => OrderWorkflow(
        transitions:
        [
            // The transition targets the gate rather than a state.
            new WorkflowTransitionConfig { Id = "submit", FromState = "Draft", ToState = "amount_gate" }
        ],
        choiceNodes:
        [
            new WorkflowChoiceNodeConfig
            {
                Id = "amount_gate",
                Name = "Amount Gate",
                DefaultState = defaultState ?? string.Empty,
                Branches =
                [
                    new WorkflowChoiceBranchConfig
                    {
                        ToState = "NeedsApproval",
                        Conditions =
                        [
                            new WorkflowConditionConfig
                            {
                                Property = "TotalAmount", Operator = "greaterthan", Value = "500"
                            }
                        ]
                    },
                    new WorkflowChoiceBranchConfig
                    {
                        ToState = "AutoApproved",
                        Conditions =
                        [
                            new WorkflowConditionConfig
                            {
                                Property = "TotalAmount", Operator = "lessthanorequal", Value = "500"
                            }
                        ]
                    }
                ]
            }
        ]);

    [Fact]
    public async Task AGateRoutesToTheBranchWhoseConditionHolds()
    {
        var entity = new StatefulEntity { TotalAmount = 900m };
        var store = new FakeStore(entity);
        var behavior = Behavior<TransitionCommand>(new FakeDefinitions(GatedWorkflow("Rejected")), store);

        await behavior.Handle(
            new TransitionCommand { ToState = "amount_gate", TotalAmount = 900m }, () => Next(), default);

        Assert.Equal("NeedsApproval", entity.CurrentState);
    }

    [Fact]
    public async Task AGateRoutesToTheOtherBranchWhenTheFirstDoesNotHold()
    {
        var entity = new StatefulEntity { TotalAmount = 100m };
        var store = new FakeStore(entity);
        var behavior = Behavior<TransitionCommand>(new FakeDefinitions(GatedWorkflow("Rejected")), store);

        await behavior.Handle(
            new TransitionCommand { ToState = "amount_gate", TotalAmount = 100m }, () => Next(), default);

        Assert.Equal("AutoApproved", entity.CurrentState);
    }

    [Fact]
    public async Task TheResolvedStateIsWhatTheHistoryRecords()
    {
        // Not the gate id. A history saying the record moved to "amount_gate" would name something
        // that is not a state, in the one place someone looks to find out what happened.
        var entity = new StatefulEntity { TotalAmount = 900m };
        var store = new FakeStore(entity);
        var behavior = Behavior<TransitionCommand>(new FakeDefinitions(GatedWorkflow("Rejected")), store);

        await behavior.Handle(
            new TransitionCommand { ToState = "amount_gate", TotalAmount = 900m }, () => Next(), default);

        Assert.Equal("NeedsApproval", store.Logs.Single().ToState);
    }

    [Fact]
    public async Task AnUnmatchedGateFallsBackToItsDeclaredDefault()
    {
        var entity = new StatefulEntity { TotalAmount = 900m };
        var store = new FakeStore(entity);

        var workflow = OrderWorkflow(
            transitions: [new WorkflowTransitionConfig { Id = "submit", FromState = "Draft", ToState = "amount_gate" }],
            choiceNodes:
            [
                new WorkflowChoiceNodeConfig
                {
                    Id = "amount_gate",
                    Name = "Amount Gate",
                    DefaultState = "ManualReview",
                    Branches =
                    [
                        new WorkflowChoiceBranchConfig
                        {
                            ToState = "AutoApproved",
                            Conditions =
                            [
                                new WorkflowConditionConfig
                                {
                                    Property = "TotalAmount", Operator = "lessthan", Value = "10"
                                }
                            ]
                        }
                    ]
                }
            ]);

        var behavior = Behavior<TransitionCommand>(new FakeDefinitions(workflow), store);

        await behavior.Handle(
            new TransitionCommand { ToState = "amount_gate", TotalAmount = 900m }, () => Next(), default);

        Assert.Equal("ManualReview", entity.CurrentState);
    }

    [Fact]
    public async Task AnUnmatchedGateWithNoDefaultIsRefused()
    {
        // What this used to do instead: assign the empty string as the entity's state and save it.
        // The comment beside the manifest emitter said an unmatched gate was "a routing failure
        // rather than guessing", and nothing implemented that -- the record landed in a state no
        // transition matches, unreachable and invisible, behind a 200 and a history entry naming "".
        var entity = new StatefulEntity { TotalAmount = 900m };
        var store = new FakeStore(entity);

        var workflow = OrderWorkflow(
            transitions: [new WorkflowTransitionConfig { Id = "submit", FromState = "Draft", ToState = "amount_gate" }],
            choiceNodes:
            [
                new WorkflowChoiceNodeConfig
                {
                    Id = "amount_gate",
                    Name = "Amount Gate",
                    DefaultState = string.Empty,
                    Branches =
                    [
                        new WorkflowChoiceBranchConfig
                        {
                            ToState = "AutoApproved",
                            Conditions =
                            [
                                new WorkflowConditionConfig
                                {
                                    Property = "TotalAmount", Operator = "lessthan", Value = "10"
                                }
                            ]
                        }
                    ]
                }
            ]);

        var behavior = Behavior<TransitionCommand>(new FakeDefinitions(workflow), store);

        var error = await Assert.ThrowsAsync<WorkflowException>(() => behavior.Handle(
            new TransitionCommand { ToState = "amount_gate", TotalAmount = 900m }, () => Next(), default));

        Assert.Contains("amount_gate", error.Message);
        Assert.Equal("Draft", entity.CurrentState);
        Assert.Empty(store.Saved);
    }

    // ---- pass-through ----

    [Fact]
    public async Task ARequestThatIsNotATransitionPassesStraightThrough()
    {
        var store = new FakeStore(new StatefulEntity());
        var behavior = Behavior<PlainCommand>(new FakeDefinitions(), store);
        var nextCalled = false;

        await behavior.Handle(new PlainCommand(), () => Next(() => nextCalled = true), default);

        Assert.True(nextCalled);
        Assert.Empty(store.Saved);
        Assert.Empty(store.Logs);
    }

    // ---- workflow resolution ----

    [Fact]
    public async Task AnEntityWithNoActiveWorkflowIsRejected()
    {
        var behavior = Behavior<TransitionCommand>(
            new FakeDefinitions(OrderWorkflow(isActive: false)), new FakeStore(new StatefulEntity()));

        var error = await Assert.ThrowsAsync<WorkflowException>(
            () => behavior.Handle(new TransitionCommand(), () => Next(), default));

        Assert.Contains("No active workflow", error.Message);
    }

    [Fact]
    public async Task AWorkflowThatIsNotYetEffectiveIsRejected()
    {
        var behavior = Behavior<TransitionCommand>(
            new FakeDefinitions(OrderWorkflow(effectiveDate: DateTime.UtcNow.AddDays(7).ToString("O"))),
            new FakeStore(new StatefulEntity()));

        var error = await Assert.ThrowsAsync<WorkflowException>(
            () => behavior.Handle(new TransitionCommand(), () => Next(), default));

        Assert.Contains("not yet effective", error.Message);
    }

    [Fact]
    public async Task AnExpiredWorkflowIsRejected()
    {
        var behavior = Behavior<TransitionCommand>(
            new FakeDefinitions(OrderWorkflow(expirationDate: DateTime.UtcNow.AddDays(-1).ToString("O"))),
            new FakeStore(new StatefulEntity()));

        var error = await Assert.ThrowsAsync<WorkflowException>(
            () => behavior.Handle(new TransitionCommand(), () => Next(), default));

        Assert.Contains("expired", error.Message);
    }

    [Fact]
    public async Task AnUndefinedTransitionIsRejected()
    {
        var behavior = Behavior<TransitionCommand>(
            new FakeDefinitions(OrderWorkflow()), new FakeStore(new StatefulEntity()));

        var error = await Assert.ThrowsAsync<WorkflowException>(
            () => behavior.Handle(new TransitionCommand { TransitionId = "teleport" }, () => Next(), default));

        Assert.Contains("teleport", error.Message);
    }

    // ---- authorisation ----

    [Fact]
    public async Task ACallerWithoutTheRequiredRoleIsRejected()
    {
        var workflow = OrderWorkflow(transitions:
        [
            new WorkflowTransitionConfig
            {
                Id = "submit", FromState = "Draft", ToState = "Submitted",
                RequiredRoles = ["Approver"]
            }
        ]);

        var behavior = Behavior<TransitionCommand>(
            new FakeDefinitions(workflow), new FakeStore(new StatefulEntity()), new FakeUser("ada", "Reader"));

        await Assert.ThrowsAsync<WorkflowException>(
            () => behavior.Handle(new TransitionCommand(), () => Next(), default));
    }

    [Fact]
    public async Task ACallerWithTheRequiredRoleProceeds()
    {
        var workflow = OrderWorkflow(transitions:
        [
            new WorkflowTransitionConfig
            {
                Id = "submit", FromState = "Draft", ToState = "Submitted",
                RequiredRoles = ["Approver"]
            }
        ]);
        var store = new FakeStore(new StatefulEntity());

        var behavior = Behavior<TransitionCommand>(
            new FakeDefinitions(workflow), store, new FakeUser("ada", "Approver"));

        await behavior.Handle(new TransitionCommand(), () => Next(), default);

        Assert.Single(store.Saved);
    }

    [Fact]
    public async Task WithNoUserContextTheTransitionIsAttributedToTheSystem()
    {
        // A background or system-initiated transition has no user. Requiring one would make the
        // workflow unusable outside a request; recording it as "system" keeps the history honest.
        var store = new FakeStore(new StatefulEntity());
        var behavior = Behavior<TransitionCommand>(new FakeDefinitions(OrderWorkflow()), store);

        await behavior.Handle(new TransitionCommand(), () => Next(), default);

        Assert.Equal("system", Assert.Single(store.Logs).TriggeredBy);
    }

    // ---- entity state ----

    [Fact]
    public async Task AMissingEntityIsReportedAsMissing()
    {
        var behavior = Behavior<TransitionCommand>(
            new FakeDefinitions(OrderWorkflow()), new FakeStore(entity: null));

        var error = await Assert.ThrowsAsync<WorkflowException>(
            () => behavior.Handle(new TransitionCommand(), () => Next(), default));

        Assert.Contains("does not exist", error.Message);
    }

    [Fact]
    public async Task AnUnregisteredEntityTypeIsDistinguishedFromAMissingRecord()
    {
        // "no such entity type" is a configuration error and "no such record" is a data condition.
        // Conflating them sends someone looking for a record that was never the problem.
        var behavior = Behavior<TransitionCommand>(
            new FakeDefinitions(OrderWorkflow()), new FakeStore(new StatefulEntity()) { ThrowOnLoad = true });

        var error = await Assert.ThrowsAsync<WorkflowException>(
            () => behavior.Handle(new TransitionCommand(), () => Next(), default));

        Assert.Contains("not registered", error.Message);
    }

    [Fact]
    public async Task ATransitionFromTheWrongStateIsRejected()
    {
        var store = new FakeStore(new StatefulEntity { CurrentState = "Approved" });
        var behavior = Behavior<TransitionCommand>(new FakeDefinitions(OrderWorkflow()), store);

        var error = await Assert.ThrowsAsync<WorkflowException>(
            () => behavior.Handle(new TransitionCommand(), () => Next(), default));

        Assert.Contains("Approved", error.Message);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task AnEntityWithNoStateAdoptsTheInitialState()
    {
        var entity = new StatefulEntity { CurrentState = "" };
        var store = new FakeStore(entity);
        var behavior = Behavior<TransitionCommand>(new FakeDefinitions(OrderWorkflow()), store);

        await behavior.Handle(new TransitionCommand(), () => Next(), default);

        Assert.Equal("Submitted", entity.CurrentState);
    }

    [Fact]
    public async Task TheEntityIsStampedWithTheWorkflowIdentity()
    {
        var entity = new StatefulEntity();
        var store = new FakeStore(entity);
        var behavior = Behavior<TransitionCommand>(new FakeDefinitions(OrderWorkflow()), store);

        await behavior.Handle(new TransitionCommand(), () => Next(), default);

        Assert.Equal("Submitted", entity.CurrentState);
        Assert.Equal("order_wf", entity.WorkflowId);
        Assert.Equal("1.0.0", entity.WorkflowVersion);
    }

    // ---- guard conditions ----

    [Fact]
    public async Task AFailingGuardBlocksTheTransition()
    {
        var workflow = OrderWorkflow(transitions:
        [
            new WorkflowTransitionConfig
            {
                Id = "submit", FromState = "Draft", ToState = "Submitted",
                Conditions = [new WorkflowConditionConfig { Property = "TotalAmount", Operator = "greaterthan", Value = "5000" }]
            }
        ]);
        var store = new FakeStore(new StatefulEntity { TotalAmount = 10m });

        var behavior = Behavior<TransitionCommand>(new FakeDefinitions(workflow), store);

        await Assert.ThrowsAsync<WorkflowException>(
            () => behavior.Handle(new TransitionCommand { TotalAmount = 10m }, () => Next(), default));

        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task AGuardSatisfiedByTheEntityRatherThanTheRequestPasses()
    {
        // Guards are evaluated against the request first and then the entity, so a condition on a field
        // the command does not carry still resolves.
        var workflow = OrderWorkflow(transitions:
        [
            new WorkflowTransitionConfig
            {
                Id = "submit", FromState = "Draft", ToState = "Submitted",
                Conditions = [new WorkflowConditionConfig { Property = "TotalAmount", Operator = "greaterthan", Value = "5000" }]
            }
        ]);
        var store = new FakeStore(new StatefulEntity { TotalAmount = 7500m });

        var behavior = Behavior<TransitionCommand>(new FakeDefinitions(workflow), store);

        await behavior.Handle(new TransitionCommand { TotalAmount = 0m }, () => Next(), default);

        Assert.Single(store.Saved);
    }

    // ---- choice nodes ----

    [Fact]
    public async Task AChoiceNodeRoutesToTheMatchingBranch()
    {
        var workflow = OrderWorkflow(
            transitions: [new WorkflowTransitionConfig { Id = "submit", FromState = "Draft", ToState = "gate" }],
            choiceNodes:
            [
                new WorkflowChoiceNodeConfig
                {
                    Id = "gate", Name = "Amount gate", DefaultState = "PendingReview",
                    Branches =
                    [
                        new WorkflowChoiceBranchConfig
                        {
                            ToState = "PendingManagerApproval",
                            Conditions = [new WorkflowConditionConfig { Property = "TotalAmount", Operator = "greaterthan", Value = "5000" }]
                        }
                    ]
                }
            ]);

        var entity = new StatefulEntity { TotalAmount = 7500m };
        var behavior = Behavior<TransitionCommand>(new FakeDefinitions(workflow), new FakeStore(entity));

        await behavior.Handle(new TransitionCommand { ToState = "gate", TotalAmount = 7500m }, () => Next(), default);

        Assert.Equal("PendingManagerApproval", entity.CurrentState);
    }

    [Fact]
    public async Task AChoiceNodeFallsBackToItsDefaultState()
    {
        var workflow = OrderWorkflow(
            transitions: [new WorkflowTransitionConfig { Id = "submit", FromState = "Draft", ToState = "gate" }],
            choiceNodes:
            [
                new WorkflowChoiceNodeConfig
                {
                    Id = "gate", Name = "Amount gate", DefaultState = "PendingReview",
                    Branches =
                    [
                        new WorkflowChoiceBranchConfig
                        {
                            ToState = "PendingManagerApproval",
                            Conditions = [new WorkflowConditionConfig { Property = "TotalAmount", Operator = "greaterthan", Value = "5000" }]
                        }
                    ]
                }
            ]);

        var entity = new StatefulEntity { TotalAmount = 10m };
        var behavior = Behavior<TransitionCommand>(new FakeDefinitions(workflow), new FakeStore(entity));

        await behavior.Handle(new TransitionCommand { ToState = "gate", TotalAmount = 10m }, () => Next(), default);

        Assert.Equal("PendingReview", entity.CurrentState);
    }

    [Fact]
    public async Task ACyclicChoiceChainIsReportedRatherThanLeavingTheEntityInANode()
    {
        // A node that routes to itself used to exhaust the depth counter and then fall through, leaving
        // CurrentState set to the choice node's id -- a state no transition matches, so the document is
        // silently stranded.
        var workflow = OrderWorkflow(
            transitions: [new WorkflowTransitionConfig { Id = "submit", FromState = "Draft", ToState = "gate" }],
            choiceNodes:
            [
                new WorkflowChoiceNodeConfig
                {
                    Id = "gate", Name = "Loop", DefaultState = "gate", Branches = []
                }
            ]);

        var entity = new StatefulEntity();
        var behavior = Behavior<TransitionCommand>(new FakeDefinitions(workflow), new FakeStore(entity));

        var error = await Assert.ThrowsAsync<WorkflowException>(
            () => behavior.Handle(new TransitionCommand { ToState = "gate" }, () => Next(), default));

        Assert.Contains("cycle", error.Message);
        Assert.NotEqual("gate", entity.CurrentState);
    }

    // ---- activity history ----

    [Fact]
    public async Task ASuccessfulTransitionIsRecorded()
    {
        var store = new FakeStore(new StatefulEntity());
        var behavior = Behavior<TransitionCommand>(
            new FakeDefinitions(OrderWorkflow()), store, new FakeUser("ada"));

        await behavior.Handle(new TransitionCommand(), () => Next(), default);

        var log = Assert.Single(store.Logs);
        Assert.True(log.Success);
        Assert.Equal("Draft", log.FromState);
        Assert.Equal("Submitted", log.ToState);
        Assert.Equal("submit", log.TransitionId);
        Assert.Equal("ada", log.TriggeredBy);
        Assert.Equal("order_wf", log.WorkflowId);
        Assert.NotEqual(ObjectId.Empty, log.Id);
    }

    [Fact]
    public async Task AHandlerFailureIsRecordedAsAFailure()
    {
        // The log used to be written before the handler ran with Success hardcoded to true, so a
        // handler that threw left a history entry claiming the transition had succeeded -- a false
        // record in the one place someone would look to find out what happened.
        var store = new FakeStore(new StatefulEntity());
        var behavior = Behavior<TransitionCommand>(new FakeDefinitions(OrderWorkflow()), store);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => behavior.Handle(
                new TransitionCommand(),
                () => throw new InvalidOperationException("handler exploded"),
                default));

        var log = Assert.Single(store.Logs);
        Assert.False(log.Success);
        Assert.Contains("exploded", log.ErrorMessage);
    }

    [Fact]
    public async Task AHandlerFailurePropagatesToTheCaller()
    {
        var behavior = Behavior<TransitionCommand>(
            new FakeDefinitions(OrderWorkflow()), new FakeStore(new StatefulEntity()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => behavior.Handle(
                new TransitionCommand(),
                () => throw new InvalidOperationException("handler exploded"),
                default));
    }

    [Fact]
    public async Task ARejectedTransitionWritesNoHistory()
    {
        // A transition that never happened must not appear in the history.
        var store = new FakeStore(new StatefulEntity { CurrentState = "Approved" });
        var behavior = Behavior<TransitionCommand>(new FakeDefinitions(OrderWorkflow()), store);

        await Assert.ThrowsAsync<WorkflowException>(
            () => behavior.Handle(new TransitionCommand(), () => Next(), default));

        Assert.Empty(store.Logs);
    }

    /// <summary>
    /// The state is persisted only after the handler has succeeded.
    /// </summary>
    /// <remarks>
    /// This test asserted the opposite — that the save had already happened by the time the handler
    /// ran — and it passed, because that is what the code did. It was encoding the defect as intent:
    /// a handler that threw left the entity advanced while its own history recorded the failure. The
    /// ordering is now handler, then state, then log, and this asserts the first half of that.
    /// </remarks>
    [Fact]
    public async Task TheStateIsPersistedOnlyAfterTheHandlerSucceeds()
    {
        var store = new FakeStore(new StatefulEntity());
        var behavior = Behavior<TransitionCommand>(new FakeDefinitions(OrderWorkflow()), store);
        var savedBeforeNext = -1;

        await behavior.Handle(new TransitionCommand(), () => Next(() => savedBeforeNext = store.Saved.Count), default);

        Assert.Equal(0, savedBeforeNext);
        Assert.Single(store.Saved);
    }

    // ---- construction ----

    [Fact]
    public void RequiredCollaboratorsAreValidated()
    {
        var definitions = new FakeDefinitions();
        var store = new FakeStore(null);

        Assert.Throws<ArgumentNullException>(() =>
            new WorkflowTransitionBehavior<TransitionCommand, Unit>(null!, definitions, store));
        Assert.Throws<ArgumentNullException>(() =>
            new WorkflowTransitionBehavior<TransitionCommand, Unit>(Engine(), null!, store));
        Assert.Throws<ArgumentNullException>(() =>
            new WorkflowTransitionBehavior<TransitionCommand, Unit>(Engine(), definitions, null!));
    }
}
