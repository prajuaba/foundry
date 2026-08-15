using System.Reflection;
using Xunit;

namespace Foundry.Cli.Tests;

/// <summary>
/// Regression gate: ensures that every AddFoundry* registration extension method is wired into
/// the generated Program.cs, or explicitly listed in an exemption set with a documented reason.
///
/// This guards against the silent-success defect: framework capabilities that are fully implemented
/// and documented but registered nowhere, so generated applications never use them. Each was found
/// by humans reading code months after being shipped. This test makes such absences fail the build.
/// </summary>
public class ScaffoldedAppWiringTests : IDisposable
{
    private readonly string _parentWorkspace =
        Path.Combine(Path.GetTempPath(), "foundry-wiring-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _projectDir;
    private readonly string _programCsContent;

    /// <summary>
    /// Framework registration methods that are NOT wired into every generated application,
    /// and the specific reasons they are deliberately left out.
    /// Each entry MUST carry an inline comment stating why it does not belong in every app.
    /// </summary>
    private static readonly HashSet<string> ExemptFromWiring = new()
    {
        "AddFoundryOIDC", // Alternative to AddFoundryAuthentication; user chooses one or the other, not both
        "AddFoundryRateLimiter", // Optional performance tuning; not every API needs it
        "AddFoundryKafka", // Never wired: it also starts a consumer host, which requires a GroupId and fails startup without one
        "AddFoundryKafkaProducer", // Wired, but only for a schema declaring enableKafkaOutbox, which this minimal schema does not
        "AddFoundryKafkaConsumerBridge", // Specialized consumer-bridge shape; full AddFoundryKafka is the standard
        "AddFoundryWorkflows", // Only wired when the schema declares at least one workflow entity
    };

    public ScaffoldedAppWiringTests()
    {
        Directory.CreateDirectory(_parentWorkspace);

        // Create a minimal schema for fast scaffolding
        var schemaPath = Path.Combine(_parentWorkspace, "schema.ir.json");
        File.WriteAllText(schemaPath, """
        {
          "namespace": "TestDomain",
          "entities": [
            {
              "name": "User",
              "properties": [
                { "name": "Id", "type": "ObjectId", "isKey": true },
                { "name": "Email", "type": "string", "attributes": ["Required"] }
              ],
              "apiEnabledMethods": ["GET", "POST"]
            }
          ]
        }
        """);

        // Run 'foundry new <ProjectName> --schema <path>' to scaffold the project.
        // The CLI creates a subdirectory named after the project inside the working directory.
        var projectName = "TestProject";
        var result = Cli.RunAsync(_parentWorkspace, "new", projectName, "--schema", schemaPath)
            .GetAwaiter().GetResult();

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to scaffold project '{projectName}'.\n" +
                $"Exit code: {result.ExitCode}\n" +
                $"StdOut: {result.StdOut}\n" +
                $"StdErr: {result.StdErr}");
        }

        // The scaffolded project is in a subdirectory named after the project.
        _projectDir = Path.Combine(_parentWorkspace, projectName);
        if (!Directory.Exists(_projectDir))
        {
            throw new InvalidOperationException(
                $"After running 'foundry new {projectName}', the project directory " +
                $"was not created at expected path: {_projectDir}");
        }

        var programPath = Path.Combine(_projectDir, "Program.cs");
        if (!File.Exists(programPath))
        {
            throw new FileNotFoundException(
                $"Generated Program.cs not found at {programPath}");
        }

        _programCsContent = File.ReadAllText(programPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_parentWorkspace, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void EveryFrameworkRegistrationExtensionIsWiredOrExplicitlyExempt()
    {
        // Discover all public static extension methods matching AddFoundry* or MapFoundry*
        // from the framework assemblies.
        var frameworkAssemblyNames = new[] { "Foundry.Mongo", "Foundry.Api", "Foundry.Rules", "Foundry.RealTime", "Foundry.Kafka" };
        var frameworkAssemblies = new List<Assembly>();

        foreach (var name in frameworkAssemblyNames)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == name);

            if (assembly is null)
            {
                Assert.Fail(
                    $"Framework assembly '{name}' was not loaded. Reflection discovery cannot " +
                    $"proceed, so this gate would silently pass while checking nothing — " +
                    $"the exact defect this test exists to prevent. Ensure the test project references " +
                    $"or loads all framework assemblies before running reflection discovery.");
            }

            frameworkAssemblies.Add(assembly);
        }

        // Verify that discovery actually found methods. If this assertion fails, it means
        // reflection discovery is broken, and the gate is not checking anything.
        var registrationMethods = new HashSet<string>();

        foreach (var assembly in frameworkAssemblies)
        {
            var methods = assembly
                .GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(m => (m.Name.StartsWith("AddFoundry") || m.Name.StartsWith("MapFoundry")))
                .Where(m =>
                {
                    var parameters = m.GetParameters();
                    if (parameters.Length == 0) return false;

                    var firstParamType = parameters[0].ParameterType;
                    var firstParamTypeName = firstParamType.Name;

                    // AddFoundry* should have IServiceCollection as first parameter
                    if (m.Name.StartsWith("AddFoundry"))
                        return firstParamTypeName == "IServiceCollection";

                    // MapFoundry* should have IEndpointRouteBuilder as first parameter
                    if (m.Name.StartsWith("MapFoundry"))
                        return firstParamTypeName == "IEndpointRouteBuilder";

                    return false;
                })
                .Select(m => m.Name)
                .Distinct();

            foreach (var method in methods)
            {
                registrationMethods.Add(method);
            }
        }

        // Verify that discovery actually found methods. If this assertion fails, it means
        // reflection discovery is broken or assemblies are not loaded, and the gate is not
        // actually checking anything. A lower count indicates the gate has stopped working,
        // not that the methods have disappeared.
        Assert.True(
            registrationMethods.Count >= 10,
            $"Expected to discover at least 10 AddFoundry*/MapFoundry* registrations across the " +
            $"framework assemblies, but found {registrationMethods.Count}. Reflection discovery is " +
            $"broken or assemblies failed to load, so this gate is not actually checking anything.");

        var unwiredMethods = new List<string>();

        foreach (var methodName in registrationMethods.OrderBy(x => x))
        {
            bool isWired = _programCsContent.Contains($".{methodName}(");
            bool isExempt = ExemptFromWiring.Contains(methodName);

            if (!isWired && !isExempt)
            {
                unwiredMethods.Add(methodName);
            }
        }

        if (unwiredMethods.Count > 0)
        {
            var message = string.Join(
                "\n",
                unwiredMethods.Select(method =>
                    ("AddFoundryFoo is a public framework registration that no generated " +
                    "application calls. Either wire it into the scaffolder's generated Program.cs, " +
                    "or add it to ExemptFromWiring with a comment saying why it does not belong in " +
                    "every app.").Replace("AddFoundryFoo", method)));

            Assert.Fail(message);
        }
    }

    [Fact]
    public void TheGeneratedApplicationMapsAHealthEndpoint()
    {
        // Verifies that the standard health check endpoint is present in the scaffolded Program.cs.
        // A health endpoint silently disappearing is the same class of defect as an unwired registration:
        // the capability is fully implemented, but generated applications do not use it.
        Assert.Contains("MapHealthChecks", _programCsContent);
    }
}
