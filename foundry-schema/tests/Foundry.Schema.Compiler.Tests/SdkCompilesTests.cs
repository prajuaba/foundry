using System.Diagnostics;
using System.Text.Json;
using Foundry.Schema.Compiler.Generators;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// The TypeScript and Python SDKs are run through their own compilers.
/// </summary>
/// <remarks>
/// <para>
/// The C# SDK is compiled by <c>GeneratedCodeCompilesTests</c>, and that gate earned its place
/// immediately: it caught an SDK that shipped as source with <c>public ObjectId Id</c> and no
/// <c>using MongoDB.Bson</c> — a build error in the consumer's project, which is the worst place for
/// one. The other two languages come out of the same generator family and had no equivalent check;
/// every assertion about them was a string comparison.
/// </para>
/// <para>
/// A type error in a shipped SDK is not the consumer's to debug. These shell out to the real
/// <c>tsc</c> and the real <c>python</c> for the same reason the C# one shells out to
/// <c>dotnet build</c>: only the compiler knows.
/// </para>
/// </remarks>
public class SdkCompilesTests
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

    /// <summary>The showcase schema, which exercises every construct the IR declares.</summary>
    private static SchemaModel? ShowcaseSchema(string root)
    {
        var path = Path.Combine(root, "samples", "Foundry.E2E.Showcase", "e2e-schema.ir.json");
        if (!File.Exists(path)) return null;

        return JsonSerializer.Deserialize<SchemaModel>(
            File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private static (int ExitCode, string Output) Run(string command, string arguments, string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo(command, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, output);
    }

    [Fact]
    public void TheTypeScriptSdkTypeChecks()
    {
        var root = FindRepositoryRoot();
        var schema = root is null ? null : ShowcaseSchema(root);
        if (root is null || schema is null) return;

        // Studio's own TypeScript compiler, so this needs no network and no global install. If it is
        // absent the developer has not built Studio yet, which the README requires anyway.
        var tsc = Path.Combine(root, "foundry-studio", "node_modules", ".bin", "tsc");
        if (!File.Exists(tsc)) return;

        var work = Path.Combine(Path.GetTempPath(), "foundry-sdk-ts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            File.WriteAllText(Path.Combine(work, "client.ts"), TypeScriptSdkGenerator.Generate(schema));

            // Strict, because a client that only type-checks loosely still breaks in a consumer's
            // project — which is where the C# SDK's defect surfaced.
            var (exitCode, output) = Run(
                tsc,
                "--noEmit --strict --target es2022 --module esnext --moduleResolution bundler "
                + "--lib es2022,dom client.ts",
                work);

            Assert.True(exitCode == 0, "The generated TypeScript SDK does not type-check:\n" + output);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ThePythonSdkCompiles()
    {
        var root = FindRepositoryRoot();
        var schema = root is null ? null : ShowcaseSchema(root);
        if (root is null || schema is null) return;

        var work = Path.Combine(Path.GetTempPath(), "foundry-sdk-py-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            File.WriteAllText(Path.Combine(work, "client.py"), PythonSdkGenerator.Generate(schema));

            // py_compile rather than an import: it parses and byte-compiles without needing the
            // `requests` package present, so the gate tests the generator rather than the machine.
            var (exitCode, output) = Run("python3", "-m py_compile client.py", work);

            Assert.True(exitCode == 0, "The generated Python SDK does not compile:\n" + output);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }
}
