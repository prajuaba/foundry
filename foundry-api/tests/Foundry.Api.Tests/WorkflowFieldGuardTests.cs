using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Api.MediatR;
using Foundry.Api.Workflow;
using Foundry.Core.Entities;
using Foundry.Rules;
using MediatR;
using MongoDB.Bson;
using NSubstitute;
using Xunit;

namespace Foundry.Api.Tests;

/// <summary>
/// Only a transition moves a workflow entity's state; a client's POST or PUT cannot.
/// </summary>
public class WorkflowFieldGuardTests
{
    public record Timesheet : BaseEntity<ObjectId>, IWorkflowStateful
    {
        public decimal Hours { get; init; }
        public string CurrentState { get; set; } = string.Empty;
        public string WorkflowId { get; set; } = string.Empty;
        public string WorkflowVersion { get; set; } = string.Empty;
    }

    public record Note : BaseEntity<ObjectId>
    {
        public string Text { get; init; } = string.Empty;
    }

    private sealed record Unrelated(string Text) : IRequest<string>;

    private static readonly WorkflowConfig Workflow = new()
    {
        Id = "timesheet-approval",
        Version = "2.0",
        Entity = "Timesheet",
        IsActive = true,
        States = new List<WorkflowStateConfig>
        {
            new() { Name = "Draft", IsInitial = true },
            new() { Name = "Submitted" },
            new() { Name = "Approved" },
        },
    };

    private readonly IWorkflowStateStore _store = Substitute.For<IWorkflowStateStore>();

    private WorkflowFieldGuardBehavior<TRequest, TResponse> Guard<TRequest, TResponse>(params WorkflowConfig[] workflows)
        where TRequest : notnull
    {
        var definitions = Substitute.For<IWorkflowDefinitionProvider>();
        definitions.GetWorkflows().Returns(workflows);
        return new WorkflowFieldGuardBehavior<TRequest, TResponse>(definitions, _store);
    }

    private static Timesheet Claiming(string state) => new()
    {
        Id = ObjectId.GenerateNewId(),
        Hours = 8,
        CurrentState = state,
        WorkflowId = "forged",
        WorkflowVersion = "9.9",
    };

    private static Task<Timesheet> Pass(Timesheet entity) => Task.FromResult(entity);

    [Fact]
    public async Task ACreateStartsInTheInitialStateWhateverTheBodyClaims()
    {
        var entity = Claiming("Approved");

        await Guard<InsertCommand<Timesheet>, Timesheet>(Workflow)
            .Handle(new InsertCommand<Timesheet>(entity), () => Pass(entity), CancellationToken.None);

        Assert.Equal("Draft", entity.CurrentState);
        Assert.Equal("timesheet-approval", entity.WorkflowId);
        Assert.Equal("2.0", entity.WorkflowVersion);
    }

    [Fact]
    public async Task ACreateThatSendsNoStateIsStillStampedSoAStateFilterFindsIt()
    {
        var entity = Claiming(string.Empty);

        await Guard<InsertCommand<Timesheet>, Timesheet>(Workflow)
            .Handle(new InsertCommand<Timesheet>(entity), () => Pass(entity), CancellationToken.None);

        Assert.Equal("Draft", entity.CurrentState);
    }

    [Fact]
    public async Task AnUpdateKeepsTheStoredStateWhateverTheBodyClaims()
    {
        // The reproduced case: a TeamMember's PUT of their own draft claiming Approved.
        var entity = Claiming("Approved");
        _store.LoadAsync("Timesheet", entity.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(new Timesheet { Id = entity.Id, CurrentState = "Submitted", WorkflowId = "timesheet-approval", WorkflowVersion = "2.0" });

        await Guard<UpdateCommand<Timesheet>, Timesheet>(Workflow)
            .Handle(new UpdateCommand<Timesheet>(entity), () => Pass(entity), CancellationToken.None);

        Assert.Equal("Submitted", entity.CurrentState);
        Assert.Equal("timesheet-approval", entity.WorkflowId);
        Assert.Equal("2.0", entity.WorkflowVersion);
        Assert.Equal(8, entity.Hours);
    }

    [Fact]
    public async Task AnUpdateToARowTheCallerCannotSeeDoesNotKeepTheClaimedState()
    {
        var entity = Claiming("Approved");
        _store.LoadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((IWorkflowStateful?)null);

        await Guard<UpdateCommand<Timesheet>, Timesheet>(Workflow)
            .Handle(new UpdateCommand<Timesheet>(entity), () => Pass(entity), CancellationToken.None);

        Assert.Equal(string.Empty, entity.CurrentState);
        Assert.Equal(string.Empty, entity.WorkflowId);
    }

    [Fact]
    public async Task ACreateWithNoActiveWorkflowIsLeftOutsideOne()
    {
        var entity = Claiming("Approved");
        var inactive = new WorkflowConfig { Id = Workflow.Id, Entity = Workflow.Entity, States = Workflow.States, IsActive = false };

        await Guard<InsertCommand<Timesheet>, Timesheet>(inactive)
            .Handle(new InsertCommand<Timesheet>(entity), () => Pass(entity), CancellationToken.None);

        Assert.Equal(string.Empty, entity.CurrentState);
    }

    [Fact]
    public async Task AnEntityWithNoWorkflowAndAnyOtherRequestPassThroughUntouched()
    {
        var note = new Note { Id = ObjectId.GenerateNewId(), Text = "kept" };
        var noteResult = await Guard<UpdateCommand<Note>, Note>(Workflow)
            .Handle(new UpdateCommand<Note>(note), () => Task.FromResult(note), CancellationToken.None);

        var other = await Guard<Unrelated, string>(Workflow)
            .Handle(new Unrelated("x"), () => Task.FromResult("next"), CancellationToken.None);

        Assert.Same(note, noteResult);
        Assert.Equal("next", other);
        await _store.DidNotReceiveWithAnyArgs().LoadAsync(default!, default!, default);
    }
}
