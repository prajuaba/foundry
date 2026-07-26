using Xunit;

namespace Foundry.Cli.Tests;

/// <summary>
/// The CLI's exit-code contract.
/// </summary>
/// <remarks>
/// CI uses these commands as gates, so a command that reports failure in its output while exiting 0
/// turns a red build green. That has already happened once here: <c>foundry schema build</c> returned
/// 0 unconditionally, so a failed compile looked identical to a successful one to any script.
/// </remarks>
public class CliContractTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "foundry-cli-tests-" + Guid.NewGuid().ToString("N"));

    public CliContractTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best effort */ }
    }

    private string WriteSchema(string name, string content)
    {
        var path = Path.Combine(_workspace, name);
        File.WriteAllText(path, content);
        return path;
    }

    private const string ValidSchema = """
    {
      "namespace": "Test.Domain",
      "entities": [
        {
          "name": "Customer",
          "properties": [
            { "name": "Id", "type": "ObjectId", "isKey": true },
            { "name": "FullName", "type": "string", "attributes": ["Required"] }
          ],
          "apiEnabledMethods": ["GET", "POST"]
        }
      ]
    }
    """;

    // ---- success paths ----

    [Fact]
    public async Task Version_Succeeds()
    {
        var result = await Cli.RunAsync(_workspace, "version");

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Output));
    }

    [Fact]
    public async Task Validate_OnAValidSchema_Succeeds()
    {
        var schema = WriteSchema("valid.ir.json", ValidSchema);

        var result = await Cli.RunAsync(_workspace, "validate", schema);

        Assert.Equal(0, result.ExitCode);
    }

    // ---- failure paths that CI depends on ----

    [Fact]
    public async Task Validate_OnASchemaWithErrors_ExitsNonZero()
    {
        // An entity with no key. If this exited 0 the schema gate in CI would pass a document the
        // compiler refuses to build.
        var schema = WriteSchema("no-key.ir.json", """
        {
          "namespace": "Test.Domain",
          "entities": [
            { "name": "Customer", "properties": [ { "name": "FullName", "type": "string" } ] }
          ]
        }
        """);

        var result = await Cli.RunAsync(_workspace, "validate", schema);

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Validate_OnAStudioCanvasFile_ExitsNonZeroAndSaysWhy()
    {
        // A canvas file deserialises into a structurally valid but empty model, so the compiler used
        // to report success having emitted nothing at all.
        var schema = WriteSchema("canvas.json", """
        { "nodes": [ { "id": "1", "data": { "label": "Customer" } } ], "edges": [] }
        """);

        var result = await Cli.RunAsync(_workspace, "validate", schema);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("FDY1010", result.Output);
    }

    [Fact]
    public async Task Validate_OnANonObjectIdKey_ExitsNonZero()
    {
        // The MongoDB data layer constrains IRepository<T> to IEntity<ObjectId>, so an entity keyed
        // on a string generates code that compiles and can never be persisted.
        var schema = WriteSchema("string-key.ir.json", """
        {
          "namespace": "Test.Domain",
          "entities": [
            {
              "name": "Customer",
              "properties": [ { "name": "Id", "type": "string", "isKey": true } ]
            }
          ]
        }
        """);

        var result = await Cli.RunAsync(_workspace, "validate", schema);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("FDY1011", result.Output);
    }

    [Fact]
    public async Task Validate_OnAMissingFile_ExitsNonZero()
    {
        var result = await Cli.RunAsync(_workspace, "validate", Path.Combine(_workspace, "nope.json"));

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Validate_OnMalformedJson_ExitsNonZero()
    {
        var schema = WriteSchema("broken.json", "{ this is not json");

        var result = await Cli.RunAsync(_workspace, "validate", schema);

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task SchemaBuild_OnASchemaWithErrors_ExitsNonZero()
    {
        // This returned 0 unconditionally: the compiler's exit code was discarded, so a failed
        // compile was indistinguishable from a successful one to any script or CI job.
        var schema = WriteSchema("bad.json", """
        { "namespace": "Test.Domain", "entities": [ { "name": "Customer", "properties": [] } ] }
        """);

        var result = await Cli.RunAsync(
            _workspace, "schema", "build", "--input", schema, "--output", Path.Combine(_workspace, "out"));

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task SchemaBuild_OnAValidSchema_WritesFilesAndSucceeds()
    {
        var schema = WriteSchema("valid.ir.json", ValidSchema);
        var output = Path.Combine(_workspace, "generated");

        var result = await Cli.RunAsync(_workspace, "schema", "build", "--input", schema, "--output", output);

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(Directory.GetFiles(output, "*.cs", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AnUnknownCommand_ExitsNonZero()
    {
        var result = await Cli.RunAsync(_workspace, "frobnicate");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task NoArguments_ExitsNonZeroAndPrintsHelp()
    {
        var result = await Cli.RunAsync(_workspace);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("validate", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the AI skill bundle, which CI regenerates and validates ----

    [Fact]
    public async Task AiSpec_EmitsABundleAndSucceeds()
    {
        var output = Path.Combine(_workspace, "skill");

        var result = await Cli.RunAsync(_workspace, "ai-spec", "--out", output);

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(Directory.GetFiles(output, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task EveryGoldenExampleInTheBundleValidates()
    {
        // A stale example teaches a model to emit IR the compiler rejects, so the bundle has to be
        // self-consistent. CI runs this too; asserting it here fails faster and locally.
        var bundle = Path.Combine(_workspace, "skill");
        await Cli.RunAsync(_workspace, "ai-spec", "--out", bundle);

        var examples = Directory.GetFiles(Path.Combine(bundle, "examples"), "*.json");
        Assert.NotEmpty(examples);

        foreach (var example in examples)
        {
            var result = await Cli.RunAsync(_workspace, "validate", example);
            Assert.Equal(0, result.ExitCode);
        }
    }
}
