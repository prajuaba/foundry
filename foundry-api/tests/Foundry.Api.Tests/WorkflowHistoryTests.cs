using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Foundry.Rules;
using NSubstitute;
using Xunit;

namespace Foundry.Api.Tests;

/// <summary>
/// The workflow history read path.
/// </summary>
/// <remarks>
/// <para>
/// <c>AppendActivityLogAsync</c> wrote an entry for every transition — who, when, from which state to
/// which, and the outcome of each automated action — and nothing served it. For a regulated buyer the
/// audit trail is the point, so a record that can be written and not read is half a feature.
/// </para>
/// <para>
/// The test manifest declares a workflow on <c>Order</c> whose GET_BY_ID roles are Admin and User, so
/// these also pin the rule that reading a record's history is governed by the roles for reading the
/// record.
/// </para>
/// </remarks>
public class WorkflowHistoryTests : IClassFixture<AuthenticatedApiFactory>
{
    private readonly AuthenticatedApiFactory _factory;

    static WorkflowHistoryTests()
    {
        Environment.SetEnvironmentVariable("MONGODB_ENCRYPTION_KEY", "12345678901234567890123456789012");
    }

    public WorkflowHistoryTests(AuthenticatedApiFactory factory) => _factory = factory;

    private const string EntityId = "507f1f77bcf86cd799439011";
    private const string Route = "/api/v1/orders/" + EntityId + "/history";

    private static WorkflowActivityLog Entry(string from, string to, string transition, DateTime at) => new()
    {
        Id = MongoDB.Bson.ObjectId.GenerateNewId(),
        EntityId = EntityId,
        EntityType = "Order",
        WorkflowId = "order-approval",
        WorkflowVersion = "1.0",
        FromState = from,
        ToState = to,
        TransitionId = transition,
        TriggeredBy = "clerk-1",
        TriggeredAt = at,
        PayloadDetails = "{}",
        Success = true,
        ExecutedActions =
        [
            new ActionExecutionDetail
            {
                ActionType = "ExternalApi",
                ActionName = "https://billing.example.com/charge",
                Success = true,
                StatusCode = 200,
                ResponseBody = "SECRET-UPSTREAM-BODY"
            }
        ]
    };

    /// <summary>A store that answers, so the endpoint rather than MongoDB is what is under test.</summary>
    private HttpClient ClientWith(
        IWorkflowStateful? entity, IReadOnlyList<WorkflowActivityLog> history, params string[] roles)
    {
        var store = Substitute.For<IWorkflowStateStore>();
        store.LoadAsync("Order", EntityId, Arg.Any<CancellationToken>()).Returns(entity);
        store.ReadActivityLogAsync("Order", EntityId, Arg.Any<CancellationToken>()).Returns(history);

        return _factory
            .WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton(store)))
            .CreateClient()
            .As(roles);
    }

    private sealed class StatefulOrder : IWorkflowStateful
    {
        public string CurrentState { get; set; } = "Approved";
        public string? WorkflowId { get; set; } = "order-approval";
        public string? WorkflowVersion { get; set; } = "1.0";
    }

    // ── It serves what was written ──────────────────────────────────────────

    [Fact]
    public async Task TheHistoryOfARecordIsServed()
    {
        var client = ClientWith(
            new StatefulOrder(),
            [
                Entry("Draft", "Submitted", "submit", new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc)),
                Entry("Submitted", "Approved", "approve", new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc))
            ],
            "Admin");

        var response = await client.GetAsync(Route);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, body.GetArrayLength());

        var first = body[0];
        Assert.Equal("submit", first.GetProperty("transitionId").GetString());
        Assert.Equal("Draft", first.GetProperty("fromState").GetString());
        Assert.Equal("Submitted", first.GetProperty("toState").GetString());
        Assert.Equal("clerk-1", first.GetProperty("triggeredBy").GetString());
        Assert.True(first.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task AnActionsOutcomeIsReportedWithoutItsResponseBody()
    {
        // An action can target any URL the workflow names, and the body it returns is unbounded and
        // unreviewed. Relaying it would hand an arbitrary third party's output to a caller authorized
        // to read this entity and nothing else.
        var client = ClientWith(
            new StatefulOrder(),
            [Entry("Draft", "Submitted", "submit", DateTime.UtcNow)],
            "Admin");

        var raw = await (await client.GetAsync(Route)).Content.ReadAsStringAsync();
        var action = JsonDocument.Parse(raw).RootElement[0].GetProperty("actions")[0];

        Assert.Equal("ExternalApi", action.GetProperty("actionType").GetString());
        Assert.Equal(200, action.GetProperty("statusCode").GetInt32());
        Assert.DoesNotContain("SECRET-UPSTREAM-BODY", raw);
    }

    [Fact]
    public async Task AnEmptyHistoryIsAnEmptyList()
    {
        // Not a 404: the record exists and has simply not transitioned yet, and conflating the two
        // would make "no history" indistinguishable from "no such record".
        var client = ClientWith(new StatefulOrder(), [], "Admin");

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetArrayLength());
    }

    // ── It cannot be used to reach a record the caller cannot read ──────────

    [Fact]
    public async Task AHistoryIsNotServedForARecordTheCallerCannotSee()
    {
        // The load happens first and the history is only read if it succeeded. WorkflowActivityLog is
        // not IMultiTenant and carries no owner, so querying it directly by entity id would be a read
        // path with none of the generated endpoints' filtering — the defect already found twice here,
        // in the archive reader and in the GraphQL resolver. A repository that filters the record out
        // returns null, and that has to be a 404.
        var client = ClientWith(entity: null, history: [Entry("Draft", "Submitted", "submit", DateTime.UtcNow)], "Admin");

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TheHistoryIsNotReadAtAllWhenTheRecordIsNotVisible()
    {
        // Stronger than the status code: a 404 assembled after reading the log would still have read
        // another tenant's history into memory.
        var store = Substitute.For<IWorkflowStateStore>();
        store.LoadAsync("Order", EntityId, Arg.Any<CancellationToken>()).Returns((IWorkflowStateful?)null);

        var client = _factory
            .WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton(store)))
            .CreateClient().As("Admin");

        await client.GetAsync(Route);

        await store.DidNotReceive().ReadActivityLogAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Access ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnAnonymousCallerIsRefused()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Route)).StatusCode);
    }

    [Fact]
    public async Task ACallerWithoutTheEntitysReadRoleIsRefused()
    {
        // The manifest declares GET_BY_ID on Order as Admin or User. Reading a record's history is a
        // read of that record, so it answers to the same declaration rather than a second one.
        var client = ClientWith(new StatefulOrder(), [], "Warehouse");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Route)).StatusCode);
    }

    [Fact]
    public async Task ACallerHoldingEitherDeclaredRoleIsAllowed()
    {
        var admin = ClientWith(new StatefulOrder(), [], "Admin");
        var user = ClientWith(new StatefulOrder(), [], "User");

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(Route)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync(Route)).StatusCode);
    }
}
