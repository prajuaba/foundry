using System.Net;
using System.Security.Claims;
using Foundry.Core.Entities;
using Foundry.Core.Security;
using Foundry.Core.User;
using Foundry.Rules;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Foundry.Rules.Tests;

/// <summary>
/// The four hardening changes to the workflow engine, each driven rather than inspected.
/// </summary>
public class WorkflowHardeningTests
{
    // ---- doubles ----

    /// <summary>A command whose TotalAmount is the caller's, deliberately unlike the entity's.</summary>
    private sealed record TransitionCommand : IRequest<Unit>, IWorkflowTransitionRequest
    {
        public string EntityId { get; init; } = "6a65ba09986eed749ed7e968";
        public string EntityType { get; init; } = "Order";
        public string TransitionId { get; init; } = "submit";
        public string FromState { get; init; } = "Draft";
        public string ToState { get; init; } = "Submitted";

        /// <summary>Caller-supplied. The entity's own total is 10.</summary>
        public decimal TotalAmount { get; init; } = 100_000m;
    }

    private sealed class StatefulEntity : IWorkflowStateful
    {
        public string CurrentState { get; set; } = "Draft";
        public string? WorkflowId { get; set; }
        public string? WorkflowVersion { get; set; }
        public decimal TotalAmount { get; set; } = 10m;
    }

    private sealed class FakeDefinitions(params WorkflowConfig[] workflows) : IWorkflowDefinitionProvider
    {
        public IReadOnlyList<WorkflowConfig> GetWorkflows() => workflows;
    }

    private sealed class FakeStore(IWorkflowStateful entity) : IWorkflowStateStore
    {
        public List<IWorkflowStateful> Saved { get; } = [];
        public List<WorkflowActivityLog> Logs { get; } = [];

        public Task<IWorkflowStateful?> LoadAsync(string t, string id, CancellationToken ct = default)
            => Task.FromResult<IWorkflowStateful?>(entity);

        public Task SaveAsync(string t, IWorkflowStateful e, CancellationToken ct = default)
        {
            Saved.Add(e);
            return Task.CompletedTask;
        }

        public Task AppendActivityLogAsync(WorkflowActivityLog log, CancellationToken ct = default)
        {
            Logs.Add(log);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkflowActivityLog>> ReadActivityLogAsync(
            string t, string id, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkflowActivityLog>>(Logs);
    }

    private sealed class EmptyProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static WorkflowConfig WorkflowGuarding(string source) => new()
    {
        Id = "order_wf",
        Name = "Order Workflow",
        Entity = "Order",
        Version = "1.0.0",
        IsActive = true,
        States = [new WorkflowStateConfig { Name = "Draft", IsInitial = true }],
        Transitions =
        [
            new WorkflowTransitionConfig
            {
                Id = "submit",
                FromState = "Draft",
                ToState = "Submitted",
                Conditions =
                [
                    // "over 1000". The entity holds 10; the caller's command claims 100000.
                    new WorkflowConditionConfig
                    {
                        Property = "TotalAmount",
                        Operator = "GreaterThan",
                        Value = "1000",
                        Source = source
                    }
                ]
            }
        ]
    };

    private static WorkflowTransitionBehavior<TransitionCommand, Unit> Behavior(
        IWorkflowDefinitionProvider definitions, IWorkflowStateStore store)
        => new(new WorkflowEngine(new EmptyProvider()), definitions, store);

    // ---- #1: a guard reads the one source it names ----

    /// <summary>
    /// The bypass. A guard on a value the server owns must not be satisfiable with a value the
    /// caller sent.
    /// </summary>
    /// <remarks>
    /// Previously the engine evaluated the request and then the entity and passed if either
    /// satisfied the condition, so this transition succeeded on the strength of the caller's own
    /// number while the entity's was two orders of magnitude below the threshold.
    /// </remarks>
    [Fact]
    public async Task AnEntityGuardIsNotSatisfiedByTheCallersOwnValue()
    {
        var entity = new StatefulEntity { TotalAmount = 10m };
        var store = new FakeStore(entity);

        var error = await Assert.ThrowsAsync<WorkflowException>(() =>
            Behavior(new FakeDefinitions(WorkflowGuarding("entity")), store)
                .Handle(new TransitionCommand(), () => Task.FromResult(Unit.Value), default));

        Assert.Contains("Guard condition failed", error.Message);
        Assert.Contains("entity", error.Message);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task AnEntityGuardPassesOnTheEntitysOwnValue()
    {
        var store = new FakeStore(new StatefulEntity { TotalAmount = 5000m });

        await Behavior(new FakeDefinitions(WorkflowGuarding("entity")), store)
            .Handle(new TransitionCommand(), () => Task.FromResult(Unit.Value), default);

        Assert.Single(store.Saved);
    }

    /// <summary>A guard that names the request still reads the request — the feature is kept, not removed.</summary>
    [Fact]
    public async Task ARequestGuardReadsTheRequest()
    {
        var store = new FakeStore(new StatefulEntity { TotalAmount = 10m });

        await Behavior(new FakeDefinitions(WorkflowGuarding("request")), store)
            .Handle(new TransitionCommand(), () => Task.FromResult(Unit.Value), default);

        Assert.Single(store.Saved);
    }

    /// <summary>An unrecognised source falls back to the entity, never to the caller.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Entity")]
    [InlineData("typo")]
    public async Task AnUnrecognisedSourceReadsTheEntity(string source)
    {
        var store = new FakeStore(new StatefulEntity { TotalAmount = 10m });

        await Assert.ThrowsAsync<WorkflowException>(() =>
            Behavior(new FakeDefinitions(WorkflowGuarding(source)), store)
                .Handle(new TransitionCommand(), () => Task.FromResult(Unit.Value), default));

        Assert.Empty(store.Saved);
    }

    // ---- #2: the entity advances only when the handler succeeds ----

    /// <summary>
    /// A failed transition must leave the record where it was.
    /// </summary>
    /// <remarks>
    /// The state was written before the handler ran, so a handler that threw left the entity in the
    /// new state with its own history recording the failure — an order sitting in Submitted whose
    /// log says submitting it failed.
    /// </remarks>
    [Fact]
    public async Task AHandlerFailureLeavesTheEntityInItsOriginalState()
    {
        var entity = new StatefulEntity { CurrentState = "Draft", TotalAmount = 5000m };
        var store = new FakeStore(entity);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Behavior(new FakeDefinitions(WorkflowGuarding("entity")), store)
                .Handle(
                    new TransitionCommand(),
                    () => throw new InvalidOperationException("handler rejected it"),
                    default));

        Assert.Equal("Draft", entity.CurrentState);
        Assert.Empty(store.Saved);

        // The failure is still recorded; only the state write is withheld.
        var log = Assert.Single(store.Logs);
        Assert.False(log.Success);
    }

    [Fact]
    public async Task AHandlerSuccessAdvancesAndRecordsTheEntity()
    {
        var entity = new StatefulEntity { CurrentState = "Draft", TotalAmount = 5000m };
        var store = new FakeStore(entity);

        await Behavior(new FakeDefinitions(WorkflowGuarding("entity")), store)
            .Handle(new TransitionCommand(), () => Task.FromResult(Unit.Value), default);

        Assert.Equal("Submitted", entity.CurrentState);
        Assert.Single(store.Saved);
        Assert.True(Assert.Single(store.Logs).Success);
    }

    // ---- #5 and #6: what an external endpoint may do to us ----

    [Fact]
    public void TheWorkflowClientCapsTheResponseItWillRead()
    {
        var services = new ServiceCollection();
        services.AddFoundryRules();

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("FoundryWorkflow");

        Assert.Equal(WorkflowHttpLimits.MaxResponseBytes, client.MaxResponseContentBufferSize);
    }

    /// <summary>
    /// A redirect from an external endpoint is not followed.
    /// </summary>
    /// <remarks>
    /// Driven against a real listener rather than asserted on the handler's properties, because the
    /// property is not the behaviour: what matters is that a third party answering 302 cannot make
    /// this process issue a second request to an address of their choosing.
    /// </remarks>
    [Fact]
    public async Task TheWorkflowClientDoesNotFollowRedirects()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var followed = false;

        var serving = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch { return; }

                if (context.Request.Url!.AbsolutePath.StartsWith("/internal"))
                {
                    followed = true;
                    context.Response.StatusCode = 200;
                }
                else
                {
                    context.Response.StatusCode = 302;
                    context.Response.Headers["Location"] = $"http://127.0.0.1:{port}/internal";
                }

                context.Response.Close();
            }
        });

        var services = new ServiceCollection();
        services.AddFoundryRules();
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("FoundryWorkflow");

        var response = await client.GetAsync($"http://127.0.0.1:{port}/start");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.False(followed, "the client followed a redirect chosen by the remote endpoint");

        listener.Stop();
        await serving;
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public void ARecordedResponseIsTrimmedAndSaysSo()
    {
        var body = new string('x', WorkflowHttpLimits.MaxRecordedResponseCharacters + 500);

        var recorded = WorkflowHttpLimits.ForRecording(body);

        Assert.True(recorded.Length < body.Length);
        Assert.Contains("truncated for the activity log", recorded);
        Assert.Contains("500 more character(s)", recorded);
    }

    [Fact]
    public void AShortResponseIsRecordedWhole()
    {
        Assert.Equal("{\"ok\":true}", WorkflowHttpLimits.ForRecording("{\"ok\":true}"));
        Assert.Equal(string.Empty, WorkflowHttpLimits.ForRecording(null));
    }

    // ---- #3: external actions run only after the handler has accepted ----

    private sealed class RecordingEngine : IWorkflowEngine
    {
        private readonly WorkflowEngine _inner = new(new EmptyProvider());
        public List<string> Calls { get; } = [];
        public bool FailAction { get; init; }
        public Queue<ActionExecutionDetail> ResultQueue { get; } = new();
        public int ActionCallCount { get; private set; }

        public void ValidatePermission(string t, string s, List<string> tr, List<string> sr, IEnumerable<string> ur)
            => _inner.ValidatePermission(t, s, tr, sr, ur);

        public bool EvaluateCondition(string p, string op, string v, object payload)
            => _inner.EvaluateCondition(p, op, v, payload);

        public Task<ActionExecutionDetail> ExecuteActionAsync(
            string actionType, string? requestType, string? payloadTemplate, string? method, string? url,
            Dictionary<string, string>? headers, string? bodyTemplate, object requestPayload, CancellationToken ct)
        {
            Calls.Add("action");
            ActionCallCount++;

            if (ResultQueue.Count > 0)
            {
                var result = ResultQueue.Dequeue();
                // Set AttemptNumber to the call count if it hasn't been set already
                result = result with { AttemptNumber = result.AttemptNumber == 1 ? ActionCallCount : result.AttemptNumber };
                return Task.FromResult(result);
            }

            // Fallback to original FailAction behavior when no queue is present
            var fallback = new ActionExecutionDetail
            {
                ActionType = actionType,
                ActionName = "test",
                Success = !FailAction,
                StatusCode = FailAction ? 500 : 200,
                AttemptNumber = ActionCallCount
            };
            return Task.FromResult(fallback);
        }
    }

    private static WorkflowConfig WorkflowWithAction() => new()
    {
        Id = "order_wf",
        Name = "Order Workflow",
        Entity = "Order",
        Version = "1.0.0",
        IsActive = true,
        States = [new WorkflowStateConfig { Name = "Draft", IsInitial = true }],
        Transitions =
        [
            new WorkflowTransitionConfig
            {
                Id = "submit",
                FromState = "Draft",
                ToState = "Submitted",
                Actions = [new WorkflowActionConfig { Type = "ExternalApi", Url = "https://example.invalid/hook" }]
            }
        ]
    };

    /// <summary>
    /// A handler that rejects the transition must not have already caused an external effect.
    /// </summary>
    /// <remarks>
    /// Actions used to run before the handler, so a transition the handler went on to refuse had
    /// already charged the card or sent the notification.
    /// </remarks>
    [Fact]
    public async Task AHandlerRejectionHappensBeforeAnyExternalAction()
    {
        var engine = new RecordingEngine();
        var store = new FakeStore(new StatefulEntity());
        var behavior = new WorkflowTransitionBehavior<TransitionCommand, Unit>(
            engine, new FakeDefinitions(WorkflowWithAction()), store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            behavior.Handle(new TransitionCommand(), () => throw new InvalidOperationException("no"), default));

        Assert.Empty(engine.Calls);
        Assert.Empty(store.Saved);
    }

    /// <summary>A failed action still fails the transition, and is recorded rather than lost.</summary>
    [Fact]
    public async Task AFailedActionFailsTheTransitionAndIsRecorded()
    {
        var engine = new RecordingEngine { FailAction = true };
        var store = new FakeStore(new StatefulEntity());
        var behavior = new WorkflowTransitionBehavior<TransitionCommand, Unit>(
            engine, new FakeDefinitions(WorkflowWithAction()), store);

        await Assert.ThrowsAsync<WorkflowException>(() =>
            behavior.Handle(new TransitionCommand(), () => Task.FromResult(Unit.Value), default));

        Assert.Single(engine.Calls);
        Assert.Empty(store.Saved);

        var log = Assert.Single(store.Logs);
        Assert.False(log.Success);
        Assert.Single(log.ExecutedActions);
    }

    // ---- #4: only repeatable methods get retries ----

    [Theory]
    [InlineData("GET", true)]
    [InlineData("PUT", true)]
    [InlineData("DELETE", true)]
    [InlineData("HEAD", true)]
    [InlineData("POST", false)]
    [InlineData("PATCH", false)]
    [InlineData("post", false)]
    [InlineData(null, false)]
    [InlineData("WEIRD", false)]
    public void OnlyRepeatableMethodsUseTheRetryingClient(string? method, bool retrying)
    {
        Assert.Equal(retrying, WorkflowHttpLimits.IsSafeToRepeat(method));

        Assert.Equal(
            retrying ? WorkflowHttpLimits.RetryingClientName : WorkflowHttpLimits.SingleAttemptClientName,
            WorkflowHttpLimits.ClientNameFor(method));
    }

    [Fact]
    public void BothWorkflowClientsAreRegisteredAndCapped()
    {
        var services = new ServiceCollection();
        services.AddFoundryRules();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        foreach (var name in new[] { WorkflowHttpLimits.RetryingClientName, WorkflowHttpLimits.SingleAttemptClientName })
        {
            using var client = factory.CreateClient(name);
            Assert.Equal(WorkflowHttpLimits.MaxResponseBytes, client.MaxResponseContentBufferSize);
        }
    }

    // ---- #7: the log does not keep what the entity protects ----

    private sealed record SensitiveCommand : IRequest<Unit>, IWorkflowTransitionRequest
    {
        public string EntityId { get; init; } = "6a65ba09986eed749ed7e968";
        public string EntityType { get; init; } = "Order";
        public string TransitionId { get; init; } = "submit";
        public string FromState { get; init; } = "Draft";
        public string ToState { get; init; } = "Submitted";

        public string Reference { get; init; } = "INV-001";

        [SensitiveData(Protection = ProtectionType.Encrypt)]
        public string PaymentCardNumber { get; init; } = "4111111111111111";

        [PiiData(PiiType.Email)]
        public string CustomerEmail { get; init; } = "someone@example.com";
    }

    [Fact]
    public void DeclaredSensitiveValuesAreNotWrittenToTheActivityLog()
    {
        var json = WorkflowPayloadRedactor.Serialize(new SensitiveCommand());

        Assert.DoesNotContain("4111111111111111", json);
        Assert.DoesNotContain("someone@example.com", json);
        Assert.Contains(WorkflowPayloadRedactor.Redacted, json);

        // Everything not declared sensitive is still there: a log that redacts everything is no log.
        Assert.Contains("INV-001", json);
        Assert.Contains("submit", json);
    }

    [Fact]
    public async Task TheActivityLogItselfCarriesNoSensitiveValue()
    {
        var store = new FakeStore(new StatefulEntity { TotalAmount = 5000m });

        var behavior = new WorkflowTransitionBehavior<SensitiveCommand, Unit>(
            new WorkflowEngine(new EmptyProvider()), new FakeDefinitions(WorkflowGuarding("entity")), store);

        await behavior.Handle(new SensitiveCommand(), () => Task.FromResult(Unit.Value), default);

        var log = Assert.Single(store.Logs);
        Assert.DoesNotContain("4111111111111111", log.PayloadDetails);
        Assert.Contains("INV-001", log.PayloadDetails);
    }

    // ---- nested redaction (one level deep) ----

    private sealed record NestedCommand : IRequest<Unit>, IWorkflowTransitionRequest
    {
        public string EntityId { get; init; } = "6a65ba09986eed749ed7e968";
        public string EntityType { get; init; } = "Order";
        public string TransitionId { get; init; } = "submit";
        public string FromState { get; init; } = "Draft";
        public string ToState { get; init; } = "Submitted";

        // Non-sensitive top-level property to verify it's preserved
        public string ReferenceNumber { get; init; } = "REF-12345";

        // Nested object with mixed sensitivity
        public CustomerData Customer { get; init; } = new();
    }

    private sealed record CustomerData
    {
        // Non-sensitive nested property
        public string Name { get; init; } = "John Doe";

        // Sensitive nested property (one level deep)
        [PiiData(PiiType.Email)]
        public string Email { get; init; } = "john.doe@example.com";
    }

    [Fact]
    public void NestedSensitiveValuesAreRedactedOneLevel()
    {
        var json = WorkflowPayloadRedactor.Serialize(new NestedCommand());

        // Non-sensitive top-level property should be present
        Assert.Contains("REF-12345", json);

        // Non-sensitive nested property (one level deep) should be present
        Assert.Contains("\"Name\":\"John Doe\"", json);

        // Sensitive nested property (one level deep) should NOT appear in plaintext
        Assert.DoesNotContain("john.doe@example.com", json);

        // Redaction indicator should appear for the nested sensitive field
        Assert.Contains(WorkflowPayloadRedactor.Redacted, json);
    }

    // ---- NEW: Retry and Compensation tests ----

    private static WorkflowActionConfig MakeRetryable(string actionName)
        => new()
        {
            Type = "ExternalApi",
            Url = $"https://example.invalid/{actionName}",
            Method = "POST",
            Headers = [],
            BodyTemplate = "{}",
            Retryable = true
        };

    private static WorkflowActionConfig MakeNonRetryable(string actionName)
        => new()
        {
            Type = "ExternalApi",
            Url = $"https://example.invalid/{actionName}",
            Method = "POST",
            Headers = [],
            BodyTemplate = "{}"
        };

    [Fact]
    public async Task RetryableActionWithThreeTotalAttemptsFailFailSucceed()
    {
        var engine = new RecordingEngine();
        // Queue: fail, fail, succeed (3 total attempts due to MaxActionAttempts=3)
        engine.ResultQueue.Enqueue(new ActionExecutionDetail
        { ActionType = "ExternalApi", ActionName = "action1", Success = false, StatusCode = 500 });
        engine.ResultQueue.Enqueue(new ActionExecutionDetail
        { ActionType = "ExternalApi", ActionName = "action1", Success = false, StatusCode = 500 });
        engine.ResultQueue.Enqueue(new ActionExecutionDetail
        { ActionType = "ExternalApi", ActionName = "action1", Success = true, StatusCode = 200 });

        var store = new FakeStore(new StatefulEntity());

        var wfConfig = new WorkflowConfig
        {
            Id = "order_wf",
            Entity = "Order",
            Version = "1.0.0",
            IsActive = true,
            States = [new WorkflowStateConfig { Name = "Draft", IsInitial = true }],
            Transitions =
            [
                new WorkflowTransitionConfig
                {
                    Id = "submit",
                    FromState = "Draft",
                    ToState = "Submitted",
                    Actions = [MakeRetryable("action1")]
                }
            ]
        };

        var behavior = new WorkflowTransitionBehavior<TransitionCommand, Unit>(
            engine, new FakeDefinitions(wfConfig), store, delay: (_, _) => Task.CompletedTask);

        await behavior.Handle(new TransitionCommand(), () => Task.FromResult(Unit.Value), default);

        Assert.Equal(3, engine.ActionCallCount);
        Assert.Single(store.Saved);
        Assert.Equal("Submitted", store.Saved[0].CurrentState);

        var log = Assert.Single(store.Logs);
        Assert.True(log.Success);
        Assert.Equal(3, log.ExecutedActions.Count);
        Assert.Equal(new[] { false, false, true }, log.ExecutedActions.Select(a => a.Success));
        Assert.Equal(new[] { 1, 2, 3 }, log.ExecutedActions.Select(a => a.AttemptNumber));
    }

    [Fact]
    public async Task RetryableActionExhaustsAllRetriesFailFailFail()
    {
        var engine = new RecordingEngine();
        // Queue: fail, fail, fail (MaxActionAttempts=3)
        engine.ResultQueue.Enqueue(new ActionExecutionDetail
        { ActionType = "ExternalApi", ActionName = "action1", Success = false, StatusCode = 500 });
        engine.ResultQueue.Enqueue(new ActionExecutionDetail
        { ActionType = "ExternalApi", ActionName = "action1", Success = false, StatusCode = 500 });
        engine.ResultQueue.Enqueue(new ActionExecutionDetail
        { ActionType = "ExternalApi", ActionName = "action1", Success = false, StatusCode = 500 });

        var store = new FakeStore(new StatefulEntity());

        var wfConfig = new WorkflowConfig
        {
            Id = "order_wf",
            Entity = "Order",
            Version = "1.0.0",
            IsActive = true,
            States = [new WorkflowStateConfig { Name = "Draft", IsInitial = true }],
            Transitions =
            [
                new WorkflowTransitionConfig
                {
                    Id = "submit",
                    FromState = "Draft",
                    ToState = "Submitted",
                    Actions = [MakeRetryable("action1")]
                }
            ]
        };

        var behavior = new WorkflowTransitionBehavior<TransitionCommand, Unit>(
            engine, new FakeDefinitions(wfConfig), store, delay: (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<WorkflowException>(() =>
            behavior.Handle(new TransitionCommand(), () => Task.FromResult(Unit.Value), default));

        Assert.Equal(3, engine.ActionCallCount);
        Assert.Empty(store.Saved);

        var log = Assert.Single(store.Logs);
        Assert.False(log.Success);
        Assert.Equal(3, log.ExecutedActions.Count);
        Assert.All(log.ExecutedActions, a => Assert.False(a.Success));
        Assert.Equal(new[] { 1, 2, 3 }, log.ExecutedActions.Select(a => a.AttemptNumber));
    }

    [Fact]
    public async Task ThreeActionsWithCompensationTwoSucceedThirdFails()
    {
        var engine = new RecordingEngine();
        // A succeeds, B succeeds, C fails
        engine.ResultQueue.Enqueue(new ActionExecutionDetail { ActionType = "ExternalApi", ActionName = "A", Success = true, StatusCode = 200 });
        engine.ResultQueue.Enqueue(new ActionExecutionDetail { ActionType = "ExternalApi", ActionName = "B", Success = true, StatusCode = 200 });
        engine.ResultQueue.Enqueue(new ActionExecutionDetail { ActionType = "ExternalApi", ActionName = "C", Success = false, StatusCode = 500 });
        // B's compensation succeeds
        engine.ResultQueue.Enqueue(new ActionExecutionDetail { ActionType = "ExternalApi", ActionName = "Compensate B", Success = true, StatusCode = 200 });
        // A's compensation succeeds
        engine.ResultQueue.Enqueue(new ActionExecutionDetail { ActionType = "ExternalApi", ActionName = "Compensate A", Success = true, StatusCode = 200 });

        var compensateA = new WorkflowActionConfig
        {
            Type = "ExternalApi",
            Url = "https://example.invalid/comp-a",
            Method = "POST",
            Headers = [],
            BodyTemplate = "{}"
        };
        var compensateB = new WorkflowActionConfig
        {
            Type = "ExternalApi",
            Url = "https://example.invalid/comp-b",
            Method = "POST",
            Headers = [],
            BodyTemplate = "{}"
        };

        var store = new FakeStore(new StatefulEntity());

        var wfConfig = new WorkflowConfig
        {
            Id = "order_wf",
            Entity = "Order",
            Version = "1.0.0",
            IsActive = true,
            States = [new WorkflowStateConfig { Name = "Draft", IsInitial = true }],
            Transitions =
            [
                new WorkflowTransitionConfig
                {
                    Id = "submit",
                    FromState = "Draft",
                    ToState = "Submitted",
                    Actions =
                    [
                        new WorkflowActionConfig { Type = "ExternalApi", Url = "https://example.invalid/A", Method = "POST", Headers = [], BodyTemplate = "{}", CompensateWith = compensateA },
                        new WorkflowActionConfig { Type = "ExternalApi", Url = "https://example.invalid/B", Method = "POST", Headers = [], BodyTemplate = "{}", CompensateWith = compensateB },
                        MakeNonRetryable("C")
                    ]
                }
            ]
        };

        var behavior = new WorkflowTransitionBehavior<TransitionCommand, Unit>(
            engine, new FakeDefinitions(wfConfig), store);

        await Assert.ThrowsAsync<WorkflowException>(() =>
            behavior.Handle(new TransitionCommand(), () => Task.FromResult(Unit.Value), default));

        var log = Assert.Single(store.Logs);
        Assert.False(log.Success);
        Assert.Equal(5, log.ExecutedActions.Count);

        // Order: A success (1), B success (2), C failure (3), Compensate B (4), Compensate A (5)
        Assert.Equal("A", log.ExecutedActions[0].ActionName);
        Assert.True(log.ExecutedActions[0].Success);
        Assert.False(log.ExecutedActions[0].IsCompensation);

        Assert.Equal("B", log.ExecutedActions[1].ActionName);
        Assert.True(log.ExecutedActions[1].Success);
        Assert.False(log.ExecutedActions[1].IsCompensation);

        Assert.Equal("C", log.ExecutedActions[2].ActionName);
        Assert.False(log.ExecutedActions[2].Success);
        Assert.False(log.ExecutedActions[2].IsCompensation);

        Assert.Equal("Compensate B", log.ExecutedActions[3].ActionName);
        Assert.True(log.ExecutedActions[3].Success);
        Assert.True(log.ExecutedActions[3].IsCompensation);
        Assert.Equal("B", log.ExecutedActions[3].CompensatesActionName);

        Assert.Equal("Compensate A", log.ExecutedActions[4].ActionName);
        Assert.True(log.ExecutedActions[4].Success);
        Assert.True(log.ExecutedActions[4].IsCompensation);
        Assert.Equal("A", log.ExecutedActions[4].CompensatesActionName);
    }

    [Fact]
    public async Task CompensationItselfFailsBUTCompensationsCompleteSweep()
    {
        var engine = new RecordingEngine();
        // A succeeds, B succeeds, C fails, Comp B fails, Comp A succeeds
        engine.ResultQueue.Enqueue(new ActionExecutionDetail { ActionType = "ExternalApi", ActionName = "A", Success = true, StatusCode = 200 });
        engine.ResultQueue.Enqueue(new ActionExecutionDetail { ActionType = "ExternalApi", ActionName = "B", Success = true, StatusCode = 200 });
        engine.ResultQueue.Enqueue(new ActionExecutionDetail { ActionType = "ExternalApi", ActionName = "C", Success = false, StatusCode = 500 });
        // B's compensation fails
        engine.ResultQueue.Enqueue(new ActionExecutionDetail { ActionType = "ExternalApi", ActionName = "Compensate B", Success = false, StatusCode = 500 });
        // A's compensation succeeds (even though B's failed - sweep continues)
        engine.ResultQueue.Enqueue(new ActionExecutionDetail { ActionType = "ExternalApi", ActionName = "Compensate A", Success = true, StatusCode = 200 });

        var compensateA = new WorkflowActionConfig
        {
            Type = "ExternalApi",
            Url = "https://example.invalid/comp-a",
            Method = "POST",
            Headers = [],
            BodyTemplate = "{}"
        };
        var compensateB = new WorkflowActionConfig
        {
            Type = "ExternalApi",
            Url = "https://example.invalid/comp-b",
            Method = "POST",
            Headers = [],
            BodyTemplate = "{}"
        };

        var store = new FakeStore(new StatefulEntity());

        var wfConfig = new WorkflowConfig
        {
            Id = "order_wf",
            Entity = "Order",
            Version = "1.0.0",
            IsActive = true,
            States = [new WorkflowStateConfig { Name = "Draft", IsInitial = true }],
            Transitions =
            [
                new WorkflowTransitionConfig
                {
                    Id = "submit",
                    FromState = "Draft",
                    ToState = "Submitted",
                    Actions =
                    [
                        new WorkflowActionConfig { Type = "ExternalApi", Url = "https://example.invalid/A", Method = "POST", Headers = [], BodyTemplate = "{}", CompensateWith = compensateA },
                        new WorkflowActionConfig { Type = "ExternalApi", Url = "https://example.invalid/B", Method = "POST", Headers = [], BodyTemplate = "{}", CompensateWith = compensateB },
                        MakeNonRetryable("C")
                    ]
                }
            ]
        };

        var behavior = new WorkflowTransitionBehavior<TransitionCommand, Unit>(
            engine, new FakeDefinitions(wfConfig), store);

        await Assert.ThrowsAsync<WorkflowException>(() =>
            behavior.Handle(new TransitionCommand(), () => Task.FromResult(Unit.Value), default));

        var log = Assert.Single(store.Logs);
        Assert.False(log.Success);
        Assert.Equal(5, log.ExecutedActions.Count);

        // Compensation order: B then A (reverse of original execution order)
        // Even though Compensate B failed, the sweep continued to Compensate A
        Assert.True(log.ExecutedActions[3].IsCompensation);
        Assert.Equal("B", log.ExecutedActions[3].CompensatesActionName);
        Assert.False(log.ExecutedActions[3].Success);

        Assert.True(log.ExecutedActions[4].IsCompensation);
        Assert.Equal("A", log.ExecutedActions[4].CompensatesActionName);
        Assert.True(log.ExecutedActions[4].Success);
    }
}
