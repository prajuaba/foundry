using System.Runtime.CompilerServices;
using Foundry.Mongo.Infrastructure.Conventions;

namespace Foundry.Mongo.Tests;

/// <summary>
/// Establishes the process-global MongoDB serialization configuration once, before any test runs.
/// </summary>
/// <remarks>
/// <para>
/// <c>MongoDbConventions.Register()</c> mutates state that is global to the process: a camelCase
/// element-name convention, an enum-as-string convention, and a <c>Guid</c> serializer. It is
/// idempotent, but <em>when</em> it first runs is not: previously it was triggered incidentally by
/// whichever test happened to call <c>AddFoundryMongo</c> (or call it directly) first, and xUnit runs
/// test classes in parallel by default.
/// </para>
/// <para>
/// That made the whole assembly order-dependent. The MongoDB driver freezes a type's class map the
/// first time it is used, so a test that serialises an entity before the conventions are registered
/// sees different element names from one that runs after — and which happens depends on thread
/// scheduling, not on anything in the tests. This suite has already produced one confirmed bug of
/// exactly this shape, where a test passed or failed according to whether another test's
/// <c>AddFoundryMongo</c> call had run first.
/// </para>
/// <para>
/// A module initializer runs on assembly load, before any test, so every test now observes the same
/// configuration. This changes nothing about production behaviour: applications register the
/// conventions through <c>AddFoundryMongo</c> during startup, which is already deterministic.
/// </para>
/// </remarks>
internal static class TestAssemblySetup
{
    [ModuleInitializer]
    internal static void Initialize() => MongoDbConventions.Register();
}
