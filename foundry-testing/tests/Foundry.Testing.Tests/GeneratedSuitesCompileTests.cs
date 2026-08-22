using System.Diagnostics;
using System.Text.Json;
using Foundry.Schema.Compiler;
using Foundry.Testing.Generators;
using Xunit;

namespace Foundry.Testing.Tests;

/// <summary>
/// The suites <c>foundry test</c> writes are real C# that compiles.
/// </summary>
/// <remarks>
/// <para>
/// Nothing compiled them and nothing ran them. That is how the generator came to emit
/// <c>/api/v1/{lowercase-singular}</c> — the fourth copy of a route rule the application does not
/// serve, after the OpenAPI exporter, the Postman exporter and Studio, all three of which were
/// corrected while this one survived. It also asserted <c>200 OK</c> with no <c>Authorization</c>
/// header against a framework where every generated endpoint calls <c>RequireAuthorization()</c>, so
/// a healthy application failed every REST assertion the "autonomous testing engine" produced.
/// </para>
/// <para>
/// Text assertions cannot catch a suite that does not build. This shells out to a real
/// <c>dotnet build</c> over the generated output, exactly as the compiler's own
/// <c>GeneratedCodeCompilesTests</c> does for entities — the gate that found ten defects in one
/// cycle once it existed.
/// </para>
/// </remarks>
public class GeneratedSuitesCompileTests
{
    private static string? FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Foundry.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName;
    }

    private static SchemaModel ShowcaseSchema(string root)
        => JsonSerializer.Deserialize<SchemaModel>(
            File.ReadAllText(Path.Combine(root, "samples", "Foundry.E2E.Showcase", "e2e-schema.ir.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void TheGeneratedSuitesCompile()
    {
        var root = FindRepositoryRoot();
        if (root is null) return; // Not running from the repository; nothing to verify.

        // The showcase schema, because it exercises every construct the IR declares -- so every
        // branch of this generator emits into the same project and has to build alongside the rest.
        var files = AutomatedTestSuiteGenerator.GenerateAllTestSuites(ShowcaseSchema(root));
        Assert.NotEmpty(files);

        var work = Path.Combine(Path.GetTempPath(), "foundry-suites-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            foreach (var (name, content) in files)
            {
                File.WriteAllText(Path.Combine(work, name), content);
            }

            // The project file comes from the generator, so this gate compiles the project a user
            // of `foundry test` actually gets. It used to be a private copy here, which meant the
            // gate could pass against a project nobody else had.

            var (exitCode, output) = RunDotnetBuild(work);

            Assert.True(exitCode == 0,
                "The suites written by `foundry test` do not compile:\n"
                + string.Join("\n", output.Split('\n')
                    .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
                    .Take(15)));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    private static (int ExitCode, string Output) RunDotnetBuild(string directory)
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", "build --nologo -v q")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, output);
    }
}
