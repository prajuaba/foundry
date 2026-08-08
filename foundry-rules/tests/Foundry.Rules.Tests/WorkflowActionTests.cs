using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Rules;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Foundry.Rules.Tests;

/// <summary>
/// Workflow actions, which are where a transition reaches the outside world.
/// </summary>
/// <remarks>
/// <para>
/// <c>ExecuteActionAsync</c> builds a URL, a set of headers and a JSON body by substituting values
/// from the request into templates, then sends them. The request is the MediatR command bound from
/// an HTTP body, so every substituted value is caller-controlled — this is the one place in the
/// framework where untrusted input is interpolated into three different grammars at once.
/// </para>
/// <para>
/// It had no tests.
/// </para>
/// </remarks>
public class WorkflowActionTests
{
    /// <summary>Captures the request an action produced, and returns a canned response.</summary>
    private sealed class CapturingHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

            return new HttpResponseMessage(status) { Content = new StringContent("{}") };
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (WorkflowEngine Engine, CapturingHandler Handler) EngineWithCapture(
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new CapturingHandler(status);
        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(handler));

        return (new WorkflowEngine(services.BuildServiceProvider()), handler);
    }

    /// <summary>A request payload whose values are whatever the caller sent.</summary>
    private sealed record Payload(string Reference, string Amount);

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

    private static Task<ActionExecutionDetail> ExternalAsync(
        WorkflowEngine engine,
        string url,
        object payload,
        string? bodyTemplate = null,
        Dictionary<string, string>? headers = null,
        string method = "POST")
        => engine.ExecuteActionAsync(
            "ExternalApi", null, null, method, url, headers, bodyTemplate, payload, CancellationToken.None);

    // ── The happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task AnExternalActionSendsTheTemplatedRequest()
    {
        var (engine, handler) = EngineWithCapture();

        var detail = await ExternalAsync(
            engine,
            "https://hooks.example.com/notify/{{Reference}}",
            new Payload("INV-001", "42"),
            """{"reference":"{{Reference}}"}""");

        Assert.True(detail.Success);
        Assert.Equal("https://hooks.example.com/notify/INV-001", handler.Request!.RequestUri!.ToString());
        Assert.Contains("INV-001", handler.Body);
    }

    [Fact]
    public async Task AFailingStatusIsReportedAsFailure()
    {
        var (engine, _) = EngineWithCapture(HttpStatusCode.BadGateway);

        var detail = await ExternalAsync(engine, "https://hooks.example.com/notify", new Payload("INV-001", "1"));

        Assert.False(detail.Success);
        Assert.Equal(502, detail.StatusCode);
    }

    [Fact]
    public async Task AMissingUrlIsRefusedRatherThanAttempted()
    {
        var (engine, handler) = EngineWithCapture();

        var detail = await ExternalAsync(engine, "", new Payload("INV-001", "1"));

        Assert.False(detail.Success);
        Assert.Equal(400, detail.StatusCode);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task AnUnreachableHostIsReportedNotThrown()
    {
        // A workflow action that throws would abort a transition that has already been authorised
        // and guarded. The failure belongs in the activity log, not in the caller's face.
        var engine = new WorkflowEngine(new ServiceCollection().BuildServiceProvider());

        var detail = await engine.ExecuteActionAsync(
            "ExternalApi", null, null, "POST", "http://127.0.0.1:1/never", null, null,
            new Payload("INV-001", "1"), CancellationToken.None);

        Assert.False(detail.Success);
    }

    // ── Token substitution into a URL ───────────────────────────────────────

    [Fact]
    public async Task ACallerCannotRedirectTheRequestToAnotherHost()
    {
        // The substituted value lands inside a URL. Unescaped, a value containing '@' rewrites the
        // authority: everything before it becomes userinfo and the request goes to the attacker's
        // host, carrying whatever credentials the action was configured with.
        var (engine, handler) = EngineWithCapture();

        await ExternalAsync(
            engine,
            "https://internal.example.com/notify/{{Reference}}",
            new Payload("x@evil.example.com", "1"));

        Assert.Equal("internal.example.com", handler.Request!.RequestUri!.Host);
    }

    [Fact]
    public async Task ACallerCannotEscapeThePathSegment()
    {
        var (engine, handler) = EngineWithCapture();

        await ExternalAsync(
            engine,
            "https://internal.example.com/notify/{{Reference}}",
            new Payload("../../admin/shutdown", "1"));

        Assert.DoesNotContain("admin/shutdown", handler.Request!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ACallerCannotAppendQueryParameters()
    {
        var (engine, handler) = EngineWithCapture();

        await ExternalAsync(
            engine,
            "https://internal.example.com/notify/{{Reference}}",
            new Payload("INV-001?admin=true", "1"));

        Assert.DoesNotContain("admin=true", handler.Request!.RequestUri!.Query);
    }

    // ── Token substitution into a JSON body ─────────────────────────────────

    [Fact]
    public async Task AQuoteInAValueDoesNotBreakTheJsonBody()
    {
        // Raw replacement into a JSON template lets a value close its own string and add fields.
        var (engine, handler) = EngineWithCapture();

        await ExternalAsync(
            engine,
            "https://hooks.example.com/notify",
            new Payload("\", \"approved\": true, \"x\": \"", "1"),
            """{"reference":"{{Reference}}"}""");

        using var document = System.Text.Json.JsonDocument.Parse(handler.Body!);
        Assert.False(document.RootElement.TryGetProperty("approved", out _));
    }

    [Fact]
    public async Task TheBodyRemainsValidJsonForAnyValue()
    {
        var (engine, handler) = EngineWithCapture();

        await ExternalAsync(
            engine,
            "https://hooks.example.com/notify",
            new Payload("line\nbreak \"quoted\" \\ backslash", "1"),
            """{"reference":"{{Reference}}"}""");

        using var document = System.Text.Json.JsonDocument.Parse(handler.Body!);
        Assert.Equal(
            "line\nbreak \"quoted\" \\ backslash",
            document.RootElement.GetProperty("reference").GetString());
    }

    // ── Token substitution into a header ────────────────────────────────────

    [Fact]
    public async Task ACarriageReturnInAValueFailsTheActionRatherThanBeingSent()
    {
        // Header values are added with TryAddWithoutValidation, which by design does not check for
        // CRLF. There is no meaningful header value containing a line break, so the action fails and
        // says so; stripping it silently would hide that the caller sent something the template did
        // not expect, which is how a probe goes unnoticed.
        var (engine, handler) = EngineWithCapture();

        var detail = await ExternalAsync(
            engine,
            "https://hooks.example.com/notify",
            new Payload("ok\r\nX-Injected: yes", "1"),
            headers: new Dictionary<string, string> { ["X-Reference"] = "{{Reference}}" });

        Assert.False(detail.Success);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task AWellFormedHeaderValueStillArrives()
    {
        // The escaping must not break the ordinary case it exists to protect.
        var (engine, handler) = EngineWithCapture();

        await ExternalAsync(
            engine,
            "https://hooks.example.com/notify",
            new Payload("INV-001", "1"),
            headers: new Dictionary<string, string> { ["X-Reference"] = "{{Reference}}" });

        Assert.Equal("INV-001", Assert.Single(handler.Request!.Headers.GetValues("X-Reference")));
    }

    // ── Internal actions ────────────────────────────────────────────────────

    [Fact]
    public async Task AnInternalActionWithNoRequestTypeIsRefused()
    {
        var engine = new WorkflowEngine(new ServiceCollection().BuildServiceProvider());

        var detail = await engine.ExecuteActionAsync(
            "InternalApi", "", null, null, null, null, null, new Payload("A", "1"), CancellationToken.None);

        Assert.False(detail.Success);
        Assert.Equal(400, detail.StatusCode);
    }

    [Fact]
    public async Task AnUnresolvableInternalCommandIsReported()
    {
        var resolver = new StubCommandResolver(); // Empty resolver with no registered commands
        var engine = new WorkflowEngine(new ServiceCollection().BuildServiceProvider(), resolver);

        var detail = await engine.ExecuteActionAsync(
            "InternalApi", "NoSuchCommandTypeAnywhere", null, null, null, null, null,
            new Payload("A", "1"), CancellationToken.None);

        Assert.False(detail.Success);
        Assert.Equal(404, detail.StatusCode);
        Assert.Contains("NoSuchCommandTypeAnywhere", detail.ResponseBody);
    }

    [Fact]
    public async Task AnUnknownActionTypeIsReportedRatherThanIgnored()
    {
        // Silently succeeding on an action type nobody implements is the defect class this codebase
        // is named for: the transition would complete and the action would never have run.
        var engine = new WorkflowEngine(new ServiceCollection().BuildServiceProvider());

        var detail = await engine.ExecuteActionAsync(
            "CarrierPigeon", null, null, null, null, null, null, new Payload("A", "1"), CancellationToken.None);

        Assert.False(detail.Success);
    }
}
