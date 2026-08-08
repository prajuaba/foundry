using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using NSubstitute;
using MongoDB.Bson;
using Microsoft.Extensions.DependencyInjection;
using MediatR;
using Foundry.Rules;
using Foundry.Api.Manifest;
using Foundry.Core.User;
using Foundry.Core.Entities;
using Foundry.Mongo.Repositories;
using Paperclip.OrderingSystem.Domain; // Target entity types

namespace Foundry.Api.Tests;

public class WorkflowEngineTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IWorkflowEngine _workflowEngine;

    public WorkflowEngineTests()
    {
        var services = new ServiceCollection();

        // Register default workflow services
        services.AddFoundryRules();

        // Mock dependencies
        var mockSender = Substitute.For<ISender>();
        services.AddSingleton(mockSender);

        _serviceProvider = services.BuildServiceProvider();
        _workflowEngine = _serviceProvider.GetRequiredService<IWorkflowEngine>();
    }

    [Fact]
    public void ValidatePermission_WithAuthorizedRole_Succeeds()
    {
        // Act & Assert
        _workflowEngine.ValidatePermission(
            "transition_1",
            "Draft",
            new List<string> { "Manager" },
            new List<string> { "Admin" },
            new List<string> { "Admin", "Manager" });
    }

    [Fact]
    public void ValidatePermission_WithUnauthorizedTransitionRole_ThrowsWorkflowException()
    {
        // Act & Assert
        Assert.Throws<WorkflowException>(() =>
            _workflowEngine.ValidatePermission(
                "transition_1",
                "Draft",
                new List<string> { "Manager" },
                new List<string> { "Admin" },
                new List<string> { "Admin" })); // Missing Manager role
    }

    [Theory]
    [InlineData("TotalAmount", "GreaterThan", "100", 150.0, true)]
    [InlineData("TotalAmount", "GreaterThan", "100", 50.0, false)]
    [InlineData("TotalAmount", "LessThanOrEqual", "100.50", 100.50, true)]
    [InlineData("Status", "Equal", "Pending", "Pending", true)]
    [InlineData("Status", "NotEqual", "Approved", "Draft", true)]
    public void EvaluateCondition_WithDifferentOperators_ReturnsExpectedResult(
        string property, string op, string expected, object value, bool expectedResult)
    {
        // Arrange
        var request = new DummyRequest { Property = value };
        // We map the requested property dynamically in the test source object
        var requestSource = new DictionaryRequestSource(property, value);

        // Act
        var result = _workflowEngine.EvaluateCondition(property, op, expected, requestSource);

        // Assert
        Assert.Equal(expectedResult, result);
    }

    [Fact]
    public async Task ExecuteAction_WithInternalMediatRCommand_DispatchesSuccessfully()
    {
        // Arrange
        var mockSender = Substitute.For<ISender>();
        var services = new ServiceCollection();
        services.AddSingleton(mockSender);

        // Register the stub command resolver
        services.AddSingleton<IWorkflowCommandTypeResolver>(new StubCommandResolver(typeof(DummyCommand)));
        services.AddSingleton<IWorkflowEngine, WorkflowEngine>();
        var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<IWorkflowEngine>();

        var request = new DummyRequest { Property = "Order-123" };

        // Act
        var detail = await engine.ExecuteActionAsync(
            "InternalApi",
            "DummyCommand",
            "{ \"Id\": \"{{Property}}\" }",
            null, null, null, null,
            request,
            CancellationToken.None);

        // Assert
        Assert.True(detail.Success);
        Assert.Equal(200, detail.StatusCode);
        await mockSender.Received(1).Send(Arg.Is<object>(c => c is DummyCommand && ((DummyCommand)c).Id == "Order-123"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithChoiceNodeDecisionGate_RoutesDynamically()
    {
        // Arrange
        var mockServiceProvider = Substitute.For<IServiceProvider>();

        // 1. Stub ApiManifest
        var manifest = new ApiManifest
        {
            Workflows = new List<WorkflowConfig>
            {
                new()
                {
                    Id = "wf-1",
                    Entity = "TestWorkflowStatefulEntity",
                    IsActive = true,
                    States = new List<WorkflowStateConfig>
                    {
                        new() { Name = "Draft", IsInitial = true },
                        new() { Name = "Approved" },
                        new() { Name = "PendingManagerApproval" }
                    },
                    Transitions = new List<WorkflowTransitionConfig>
                    {
                        new()
                        {
                            Id = "submit",
                            FromState = "Draft",
                            ToState = "check_amount_choice"
                        }
                    },
                    ChoiceNodes = new List<WorkflowChoiceNodeConfig>
                    {
                        new()
                        {
                            Id = "check_amount_choice",
                            DefaultState = "Approved",
                            Branches = new List<WorkflowChoiceBranchConfig>
                            {
                                new()
                                {
                                    ToState = "PendingManagerApproval",
                                    Conditions = new List<WorkflowConditionConfig>
                                    {
                                        new() { Property = "TotalAmount", Operator = "GreaterThan", Value = "1000" }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        mockServiceProvider.GetService(manifest.GetType()).Returns(manifest);

        // 2. Stub Entity Repository
        var order = new TestWorkflowStatefulEntity
        {
            Id = ObjectId.GenerateNewId(),
            OrderNumber = "ORD-TEST-123",
            CurrentState = "Draft"
        };
        var mockRepo = Substitute.For<IRepository<TestWorkflowStatefulEntity>>();
        mockRepo.GetByIdAsync(Arg.Any<MongoDB.Bson.ObjectId>(), Arg.Any<MongoDB.Driver.IClientSessionHandle?>(), Arg.Any<CancellationToken>()).Returns(order);

        mockServiceProvider.GetService(typeof(IRepository<TestWorkflowStatefulEntity>)).Returns(mockRepo);

        // The activity-log repository is now required rather than skipped when absent: a transition
        // that leaves no history is worse than one that fails, because the history is where someone
        // looks to find out what happened.
        var mockLogRepo = Substitute.For<IRepository<Foundry.Rules.WorkflowActivityLog>>();
        mockServiceProvider.GetService(typeof(IRepository<Foundry.Rules.WorkflowActivityLog>)).Returns(mockLogRepo);

        // 3. Stub engine
        var mockEngine = Substitute.For<IWorkflowEngine>();
        mockEngine.EvaluateCondition("TotalAmount", "GreaterThan", "1000", Arg.Any<object>()).Returns(true);

        // The behaviour's collaborators are injected rather than discovered by reflection. This test
        // now exercises the real adapters -- the manifest-backed definition provider and the
        // repository-backed state store -- against mocked repositories, instead of relying on the
        // behaviour finding types by scanning loaded assemblies.
        var registry = new Foundry.Api.Workflow.WorkflowEntityTypeRegistry()
            .Register<TestWorkflowStatefulEntity>();
        var definitions = new Foundry.Api.Workflow.ApiManifestWorkflowDefinitionProvider(manifest);
        var stateStore = new Foundry.Api.Workflow.MongoWorkflowStateStore(mockServiceProvider, registry);

        var behavior = new WorkflowTransitionBehavior<SubmitOrderTransition, Unit>(
            mockEngine, definitions, stateStore);
        var request = new SubmitOrderTransition { EntityId = ObjectId.GenerateNewId().ToString() };

        // Act
        var nextCalled = false;
        await behavior.Handle(request, () =>
        {
            nextCalled = true;
            return Task.FromResult(Unit.Value);
        }, CancellationToken.None);

        // Assert
        Assert.True(nextCalled);
        Assert.Equal("PendingManagerApproval", order.CurrentState); // Routed dynamically!
        await mockRepo.Received(1).UpdateAsync(order, Arg.Any<MongoDB.Driver.IClientSessionHandle?>(), Arg.Any<CancellationToken>());
    }

    public record SubmitOrderTransition : IRequest<Unit>, IWorkflowTransitionRequest
    {
        public string EntityId { get; init; } = string.Empty;
        public string EntityType => "TestWorkflowStatefulEntity";
        public string TransitionId => "submit";
        public string FromState => "Draft";
        public string ToState => "check_amount_choice";
    }

    public record TestWorkflowStatefulEntity : BaseEntity<ObjectId>, IWorkflowStateful
    {
        public required string OrderNumber { get; init; }
        public string CurrentState { get; set; } = string.Empty;
        public string WorkflowId { get; set; } = string.Empty;
        public string WorkflowVersion { get; set; } = string.Empty;
    }

    private class DictionaryRequestSource
    {
        private readonly string _propName;
        private readonly object _value;

        public DictionaryRequestSource(string propName, object value)
        {
            _propName = propName;
            _value = value;
        }

        public object? TotalAmount => _propName == "TotalAmount" ? _value : null;
        public string? Status => _propName == "Status" ? (string)_value : null;
    }

    public class DummyRequest
    {
        public object? Property { get; set; }
    }

    public class DummyCommand : IRequest
    {
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>A stub command resolver for testing.</summary>
    private sealed class StubCommandResolver : IWorkflowCommandTypeResolver
    {
        private readonly Dictionary<string, Type> _registered;

        public StubCommandResolver(params Type[] commandTypes)
        {
            _registered = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            foreach (var type in commandTypes)
            {
                _registered[type.Name] = type;
                if (!string.IsNullOrEmpty(type.FullName)) _registered[type.FullName] = type;
            }
        }

        public Type Resolve(string commandTypeName)
        {
            if (_registered.TryGetValue(commandTypeName, out var type)) return type;

            var known = _registered.Keys.Count > 0 ? string.Join(", ", _registered.Keys) : "(none)";
            throw new InvalidOperationException(
                $"Workflow command type '{commandTypeName}' is not registered. Registered: {known}.");
        }
    }
}
