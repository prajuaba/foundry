using System.Diagnostics;
using System.Text.Json;
using Foundry.Schema.Compiler;
using Foundry.Schema.Compiler.Generators;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// End-to-end guard: the compiler's output must actually build.
/// </summary>
/// <remarks>
/// <para>
/// Every other test asserts on generated <em>text</em>. That catches a missing attribute but not
/// an unresolvable type, a bad using, or a signature that does not match the runtime libraries —
/// and this compiler previously emitted <c>public EncryptedString Email</c> for an unrecognised
/// type while reporting success. Only a real <c>dotnet build</c> against Foundry.Core and
/// Foundry.Mongo closes that gap.
/// </para>
/// <para>
/// The test shells out to the SDK, so it is slower than the rest of the suite and is skipped when
/// the repository layout or SDK is unavailable rather than failing spuriously.
/// </para>
/// </remarks>
public class GeneratedCodeCompilesTests
{
    private static string? FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "foundry-core"))
                && Directory.Exists(Path.Combine(dir.FullName, "foundry-schema")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void ShowcaseIr_GeneratesCodeThatCompilesAgainstFoundryLibraries()
    {
        var root = FindRepositoryRoot();
        if (root is null) return; // Not running from the repo; nothing to verify.

        var irPath = Path.Combine(root, "samples", "Foundry.E2E.Showcase", "e2e-schema.ir.json");
        if (!File.Exists(irPath)) return;

        var schema = JsonSerializer.Deserialize<SchemaModel>(
            File.ReadAllText(irPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // The document must be clean before its output is worth compiling.
        var diagnostics = SchemaValidator.Validate(schema);
        Assert.True(!diagnostics.HasErrors, $"Showcase IR does not validate:\n{diagnostics.Render()}");

        var work = Path.Combine(Path.GetTempPath(), "foundry-compile-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            var files = PocoGenerator.GenerateFiles(schema!);
            Assert.NotEmpty(files);

            foreach (var file in files)
            {
                var target = Path.Combine(work, file.Path + ".cs");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, file.Content);
            }

            // A second multi-tenant entity, compiled into the same project so it costs no extra
            // build.
            //
            // The showcase declares two of its own now. When this was added it declared none, and
            // neither did anything else, so no multi-tenant entity the compiler emitted had ever
            // been built. None of them compiled: IMultiTenant declares
            // `string TenantId { get; set; }` and the emitted tenant key was `init`, which C# will
            // not accept as an implementation of `set` (CS8854). The framework's headline claim had
            // never reached a running application, and a text-level assertion would not have shown
            // it -- only a real compile against the real interface does.
            // Only the entity file: the generator also emits a JsonSerializerContext per schema,
            // and two of those in one project collide inside System.Text.Json's own generator. The
            // entity is what carries the IMultiTenant implementation, which is what needs compiling.
            var tenantEntity = PocoGenerator.GenerateFiles(MultiTenantSchema())
                .Single(f => f.Path == "Entities/TenantScopedInvoice");

            File.WriteAllText(Path.Combine(work, "TenantScopedInvoice.cs"), tenantEntity.Content);

            // A second workflow, for the same reason. The showcase carries one of its own now; when
            // this was added it declared none, so no schema with a workflow in it had ever been
            // compiled -- and none of them built: the emitted handler
            // named its command without importing the namespace it lives in (CS0246), and the command
            // implemented the void IRequest, which the endpoint generator cannot assign a result
            // from. Both are invisible to a text-level assertion and obvious to a real build.
            foreach (var file in PocoGenerator.GenerateFiles(WorkflowSchema()))
            {
                if (file.Path.StartsWith("Serialization/", StringComparison.Ordinal)
                    || file.Path.StartsWith("Diagnostics/", StringComparison.Ordinal))
                {
                    // One JsonSerializerContext per project; a second collides inside
                    // System.Text.Json's own generator.
                    continue;
                }

                var target = Path.Combine(work, "Workflow", file.Path + ".cs");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, file.Content);
            }

            // The generated C# client SDK, compiled in its own namespace. It shipped as source with
            // `public ObjectId Id` and no `using MongoDB.Bson`, and referred to enums it never
            // declared -- so it was a build error in the *consumer's* project, which is the worst
            // place for one. Text assertions cover its routes; only a compile covers this.
            File.WriteAllText(
                Path.Combine(work, "ClientSdk.cs"), CsharpSdkGenerator.Generate(SdkSchema()));

            // Any interface an entity names in `baseClass` belongs to the application, not to the
            // compiler -- it is the one place a schema points outside itself. A developer declares
            // it; this harness compiles generated output alone, so it declares them here for the
            // same reason, and a missing one would otherwise read as a code-generation fault.
            var externalBases = schema!.Entities
                .Select(e => e.BaseClass)
                .Concat(WorkflowSchema().Entities.Select(e => e.BaseClass))
                .Where(b => !string.IsNullOrWhiteSpace(b))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (externalBases.Count > 0)
            {
                File.WriteAllText(
                    Path.Combine(work, "ExternalBases.cs"),
                    string.Join("\n", externalBases.Select(b => $"public interface {b} {{ }}")));
            }

            // A library project referencing the same runtime libraries a scaffolded app would.
            var csproj = $"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- The point of this test is compile errors; warnings are not the signal. -->
    <NoWarn>$(NoWarn);CS1591;CS8618;CS0169;CS0414</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="{Path.Combine(root, "foundry-core", "src", "Foundry.Core", "Foundry.Core.csproj")}" />
    <ProjectReference Include="{Path.Combine(root, "foundry-mongo", "src", "Foundry.Mongo", "Foundry.Mongo.csproj")}" />
    <ProjectReference Include="{Path.Combine(root, "foundry-rules", "src", "Foundry.Rules", "Foundry.Rules.csproj")}" />
    <ProjectReference Include="{Path.Combine(root, "foundry-kafka", "src", "Foundry.Kafka", "Foundry.Kafka.csproj")}" />
    <ProjectReference Include="{Path.Combine(root, "foundry-file-io", "src", "Foundry.FileIO", "Foundry.FileIO.csproj")}" />
    <ProjectReference Include="{Path.Combine(root, "foundry-api", "src", "Foundry.Api", "Foundry.Api.csproj")}" />
    <ProjectReference Include="{Path.Combine(root, "foundry-realtime", "src", "Foundry.RealTime", "Foundry.RealTime.csproj")}" />
  </ItemGroup>
</Project>
""";
            File.WriteAllText(Path.Combine(work, "GeneratedCheck.csproj"), csproj);

            var (exitCode, output) = RunDotnetBuild(work);

            Assert.True(
                exitCode == 0,
                "Generated code from the showcase IR does not compile:\n"
                + string.Join("\n", output
                    .Split('\n')
                    .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
                    .Take(25)));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// A document exercising every access-control interface at once, in its own namespace so it
    /// cannot collide with the showcase.
    /// </summary>
    /// <remarks>
    /// Tenancy and ownership are composed on one type deliberately. Both fix the accessor of a
    /// generated property (<c>set</c>, not <c>init</c>) to satisfy an interface, and both are the
    /// kind of thing a text-level assertion reports as correct while the type does not compile.
    /// </remarks>
    private static SchemaModel MultiTenantSchema() => new()
    {
        Namespace = "Foundry.CompileCheck.Tenancy",
        Entities = new List<Entity>
        {
            new()
            {
                Name = "TenantScopedInvoice",
                MultiTenant = true,
                OwnerScoped = true,
                OwnerExemptRoles = new List<string> { "Supervisor" },
                SoftDelete = true,
                Properties = new List<Property>
                {
                    new() { Name = "Id", Type = "ObjectId", IsKey = true },
                    new() { Name = "TenantId", Type = "string", IsTenantKey = true },
                    new() { Name = "OwnerId", Type = "string", IsOwnerKey = true },
                    new() { Name = "Reference", Type = "string", Attributes = new List<string> { "Required" } }
                }
            }
        }
    };

    /// <summary>
    /// A document with a workflow, whose commands, handlers and state-bearing entity must all build.
    /// </summary>
    private static SchemaModel WorkflowSchema() => new()
    {
        Namespace = "Foundry.CompileCheck.Workflows",
        Entities =
        [
            new Entity
            {
                Name = "WorkflowScopedOrder",
                Properties =
                [
                    new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                    new Property { Name = "Reference", Type = "string" }
                ]
            }
        ],
        Workflows =
        [
            new WorkflowModel
            {
                Id = "compile-check",
                Name = "Compile Check",
                Entity = "WorkflowScopedOrder",
                Version = "1.0",
                IsActive = true,
                States =
                [
                    new WorkflowStateModel { Name = "Draft", IsInitial = true },
                    new WorkflowStateModel { Name = "Done", IsFinal = true }
                ],
                Transitions =
                [
                    new WorkflowTransitionModel
                    {
                        Id = "finish",
                        Name = "Finish",
                        FromState = "Draft",
                        ToState = "Done",
                        Trigger = "FinishWorkflowScopedOrder"
                    }
                ]
            }
        ]
    };

    /// <summary>A document whose client SDK must compile: an id, an enum and a decimal.</summary>
    private static SchemaModel SdkSchema() => new()
    {
        Namespace = "Foundry.CompileCheck.Sdk",
        Enums = [new Enum { Name = "SdkCustomerTier", Values = ["Standard", "Premium"] }],
        Entities =
        [
            new Entity
            {
                Name = "SdkCustomer",
                ApiEnabledMethods = ["GET", "POST", "GET_BY_ID", "DELETE"],
                Properties =
                [
                    new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                    new Property { Name = "FullName", Type = "string" },
                    new Property { Name = "CreditLimit", Type = "decimal" },
                    new Property { Name = "Tier", Type = "SdkCustomerTier", IsEnum = true }
                ]
            }
        ]
    };

    private static (int ExitCode, string Output) RunDotnetBuild(string workingDirectory)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("q");
        psi.ArgumentList.Add("--nologo");

        using var process = Process.Start(psi);
        if (process is null) return (0, ""); // SDK unavailable; treat as skipped.

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(300_000);

        return (process.ExitCode, stdout + "\n" + stderr);
    }
}
