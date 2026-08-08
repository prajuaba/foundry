using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Rules;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Foundry.Rules.Tests;

/// <summary>
/// Tests for the workflow command type resolver, which replaces assembly scanning for InternalApi actions.
/// </summary>
/// <remarks>
/// <para>
/// The original implementation scanned <see cref="AppDomain.CurrentDomain.GetAssemblies()"/> for a type
/// whose simple name matched the string in the workflow schema, returning the first match. This was
/// nondeterministic when two types shared a simple name, and silently wrong in both cases — the second
/// type was never considered.
/// </para>
/// <para>
/// The resolver requires explicit registration, so the application's commands are named at startup and
/// the schema is validated against them.
/// </para>
/// </remarks>
public class WorkflowCommandTypeResolverTests
{
    /// <summary>A test command for dispatch via workflow actions.</summary>
    private sealed record TestCommand(string Id, string Value) : IRequest<Unit>;

    /// <summary>A different test command to verify disambiguation.</summary>
    private sealed record AnotherTestCommand(string Data) : IRequest<Unit>;

    /// <summary>A stub command resolver for testing without a full DI container.</summary>
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

    /// <summary>
    /// The resolver correctly identifies a registered command type by simple name.
    /// </summary>
    [Fact]
    public void ARegisteredCommandCanBeResolvedBySimpleName()
    {
        var resolver = new StubCommandResolver(typeof(TestCommand));

        var resolvedType = resolver.Resolve("TestCommand");

        Assert.Equal(typeof(TestCommand), resolvedType);
    }

    /// <summary>
    /// The resolver correctly identifies a registered command type by full name.
    /// </summary>
    [Fact]
    public void ARegisteredCommandCanBeResolvedByFullName()
    {
        var resolver = new StubCommandResolver(typeof(TestCommand));

        var resolvedType = resolver.Resolve(typeof(TestCommand).FullName!);

        Assert.Equal(typeof(TestCommand), resolvedType);
    }

    /// <summary>
    /// When a command type is not registered, the resolver throws with an actionable error message.
    /// </summary>
    [Fact]
    public void AnUnregisteredCommandTypeThrows()
    {
        var resolver = new StubCommandResolver(typeof(TestCommand));

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve("UnregisteredCommand"));

        Assert.Contains("UnregisteredCommand", ex.Message);
        Assert.Contains("not registered", ex.Message);
    }

    /// <summary>
    /// When no resolver is configured, InternalApi actions fail with a helpful error message.
    /// </summary>
    [Fact]
    public async Task NoResolverConfiguredFailsWithHelpfulMessage()
    {
        var engine = new WorkflowEngine(new ServiceCollection().BuildServiceProvider());

        var detail = await engine.ExecuteActionAsync(
            "InternalApi",
            "AnyCommand",
            null,
            null,
            null,
            null,
            null,
            new TestCommand("id-123", "test-value"),
            CancellationToken.None);

        Assert.False(detail.Success);
        Assert.Equal(500, detail.StatusCode);
        Assert.Contains("not configured", detail.ResponseBody);
        Assert.Contains("AddFoundryWorkflows", detail.ResponseBody);
    }

    /// <summary>
    /// When multiple command types are registered, each is resolvable by name without collision.
    /// </summary>
    [Fact]
    public void MultipleCommandTypesAreDisambiguated()
    {
        var resolver = new StubCommandResolver(typeof(TestCommand), typeof(AnotherTestCommand));

        var resolved1 = resolver.Resolve("TestCommand");
        var resolved2 = resolver.Resolve("AnotherTestCommand");

        Assert.Equal(typeof(TestCommand), resolved1);
        Assert.Equal(typeof(AnotherTestCommand), resolved2);
    }
}
