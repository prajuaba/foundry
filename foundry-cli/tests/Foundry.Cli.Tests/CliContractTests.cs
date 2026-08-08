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
    public async Task SchemaBuild_WritesTheApiManifest()
    {
        // Without this file the application has no REST surface at all: the routes are emitted by an
        // analyser that reads api-manifest.json, and with none it emits empty registrations and every
        // entity route answers 404. It used to be written only by `foundry new`, so compiling a
        // schema into an existing project -- the documented way to do exactly that -- produced
        // entities, handlers and rules, and an API that did not exist.
        var schema = WriteSchema("valid.ir.json", ValidSchema);
        var output = Path.Combine(_workspace, "generated");

        var result = await Cli.RunAsync(_workspace, "schema", "build", "--input", schema, "--output", output);

        Assert.Equal(0, result.ExitCode);

        var manifest = Path.Combine(output, "api-manifest.json");
        Assert.True(File.Exists(manifest), $"no api-manifest.json in {output}");

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
        var endpoints = doc.RootElement.GetProperty("Endpoints");
        Assert.Equal(1, endpoints.GetArrayLength());
        Assert.Equal("Customer", endpoints[0].GetProperty("Entity").GetString());
    }

    [Fact]
    public async Task SchemaBuild_HonoursAnExplicitManifestPath()
    {
        // `foundry new` puts the manifest at the project root while the code goes to Generated/, and
        // it does so through this flag rather than through a second implementation of its own.
        var schema = WriteSchema("valid.ir.json", ValidSchema);
        var output = Path.Combine(_workspace, "generated");
        var manifest = Path.Combine(_workspace, "root", "api-manifest.json");

        var result = await Cli.RunAsync(_workspace, "schema", "build",
            "--input", schema, "--output", output, "--manifest", manifest);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(manifest));
        Assert.False(File.Exists(Path.Combine(output, "api-manifest.json")));
    }

    [Fact]
    public async Task SchemaBuild_OnASchemaWithErrors_WritesNoManifest()
    {
        // The manifest is written after the code, and a failed compile must leave neither. A stale
        // manifest beside no entities is worse than nothing: it describes routes for types that were
        // never emitted.
        var schema = WriteSchema("broken.ir.json", """
        { "namespace": "Test.Domain", "entities": [ { "name": "Customer", "properties": [] } ] }
        """);
        var output = Path.Combine(_workspace, "generated");

        var result = await Cli.RunAsync(_workspace, "schema", "build", "--input", schema, "--output", output);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(output, "api-manifest.json")));
    }

    // ---- migrate ----

    private const string CanvasSchema = """
    {
      "namespace": "MyDomain",
      "nodes": [
        {
          "id": "node-1",
          "type": "classNode",
          "position": { "x": 250, "y": 150 },
          "data": {
            "Name": "User",
            "SoftDelete": true,
            "Properties": [
              { "Name": "Id", "Type": "ObjectId", "IsKey": true },
              { "Name": "Email", "Type": "string", "Attributes": ["Required"] }
            ]
          }
        }
      ],
      "edges": []
    }
    """;

    [Fact]
    public async Task Migrate_ConvertsACanvasDocumentAndTheResultValidates()
    {
        // FDY1010's hint has always said to run this, and the command did not exist: it printed the
        // help banner. The VS Code extension's "New Schema" wrote exactly this document, so a user
        // hit the dead end without doing anything unusual.
        var canvas = WriteSchema("mydomain.foundry.json", CanvasSchema);

        var migrate = await Cli.RunAsync(_workspace, "migrate", canvas);

        Assert.Equal(0, migrate.ExitCode);

        var ir = Path.Combine(_workspace, "mydomain.ir.json");
        Assert.True(File.Exists(ir), $"no IR written; output was: {migrate.Output}");

        var validate = await Cli.RunAsync(_workspace, "validate", ir);
        Assert.Equal(0, validate.ExitCode);
    }

    [Fact]
    public async Task Migrate_LeavesTheCanvasFileAlone()
    {
        // The canvas is still the layout Studio draws from. Overwriting it would trade one format
        // for the other rather than deriving one from the other.
        var canvas = WriteSchema("mydomain.foundry.json", CanvasSchema);

        await Cli.RunAsync(_workspace, "migrate", canvas);

        Assert.Equal(CanvasSchema, File.ReadAllText(canvas));
    }

    [Fact]
    public async Task Migrate_HonoursAnExplicitOutputPath()
    {
        var canvas = WriteSchema("mydomain.foundry.json", CanvasSchema);
        var target = Path.Combine(_workspace, "nested", "domain.ir.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var result = await Cli.RunAsync(_workspace, "migrate", canvas, "--out", target);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task Migrate_OnADocumentThatIsAlreadyIr_ExitsNonZeroAndSaysSo()
    {
        var ir = WriteSchema("already.ir.json", ValidSchema);

        var result = await Cli.RunAsync(_workspace, "migrate", ir);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not a Studio canvas document", result.Output);
    }

    [Fact]
    public async Task Migrate_OnAMissingFile_ExitsNonZero()
    {
        var result = await Cli.RunAsync(_workspace, "migrate", Path.Combine(_workspace, "nope.json"));

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Migrate_WritesNothingWhenTheResultWouldNotValidate()
    {
        // A canvas can be missing what the IR requires -- here, an entity with no key property.
        // Writing it anyway would move the failure to the next command instead of reporting it.
        var canvas = WriteSchema("broken.foundry.json", """
        {
          "namespace": "MyDomain",
          "nodes": [ { "type": "classNode", "data": { "entity": { "name": "User", "properties": [] } } } ]
        }
        """);

        var result = await Cli.RunAsync(_workspace, "migrate", canvas);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(_workspace, "broken.ir.json")));
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

    // ---- token mint ----

    private const string ValidSigningKey = "this-is-a-test-signing-key-that-is-long-enough-1234567890";

    [Fact]
    public async Task TokenMint_WithValidArgsProducesAJwt()
    {
        var result = await Cli.RunAsync(_workspace, "token", "mint",
            "--signing-key", ValidSigningKey,
            "--sub", "alice",
            "--role", "Admin");

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(result.StdOut);

        var token = result.StdOut.Trim();
        Assert.Contains(".", token);
        Assert.True(token.Split('.').Length == 3, "JWT should have three parts (header.payload.signature)");
    }

    [Fact]
    public async Task TokenMint_WithoutSigningKeyExitsNonZero()
    {
        // Clears the environment variable if set, to test the error case.
        var result = await Cli.RunWithEnvironmentAsync(
            new Dictionary<string, string?> { ["Authentication__Jwt__SigningKey"] = null },
            false,
            "token", "mint", "--sub", "alice", "--role", "Admin");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("No signing key provided", result.StdErr);
    }

    [Fact]
    public async Task TokenMint_WithAShortSigningKeyExitsNonZero()
    {
        var result = await Cli.RunAsync(_workspace, "token", "mint",
            "--signing-key", "tooshort");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("HS256 requires at least 32 bytes", result.StdErr);
    }

    [Fact]
    public async Task TokenMint_UsesEnvironmentVariableForSigningKey()
    {
        var result = await Cli.RunWithEnvironmentAsync(
            new Dictionary<string, string?> { ["Authentication__Jwt__SigningKey"] = ValidSigningKey },
            false,
            "token", "mint", "--sub", "bob", "--role", "User");

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(result.StdOut);
    }

    [Fact]
    public async Task TokenMint_DefaultsSubToDevUser()
    {
        var result = await Cli.RunAsync(_workspace, "token", "mint",
            "--signing-key", ValidSigningKey);

        Assert.Equal(0, result.ExitCode);

        var token = result.StdOut.Trim();
        var decoded = DecodeJwt(token);
        Assert.Equal("dev-user", decoded["sub"]);
    }

    [Fact]
    public async Task TokenMint_WithRoleAddsRoleClaim()
    {
        var result = await Cli.RunAsync(_workspace, "token", "mint",
            "--signing-key", ValidSigningKey,
            "--sub", "alice",
            "--role", "Admin");

        Assert.Equal(0, result.ExitCode);

        var token = result.StdOut.Trim();
        var decoded = DecodeJwt(token);
        if (decoded["role"] is List<string> roleList)
        {
            Assert.Contains("Admin", roleList);
        }
        else
        {
            Assert.Equal("Admin", decoded["role"]);
        }
    }

    [Fact]
    public async Task TokenMint_WithMultipleRolesAddsAllRoles()
    {
        var result = await Cli.RunAsync(_workspace, "token", "mint",
            "--signing-key", ValidSigningKey,
            "--sub", "charlie",
            "--role", "Admin",
            "--role", "User",
            "--role", "Viewer");

        Assert.Equal(0, result.ExitCode);

        var token = result.StdOut.Trim();
        var decoded = DecodeJwt(token);
        var roleList = decoded["role"] as List<string>;
        Assert.NotNull(roleList);
        Assert.Contains("Admin", roleList);
        Assert.Contains("User", roleList);
        Assert.Contains("Viewer", roleList);
    }

    [Fact]
    public async Task TokenMint_WithTenantAddsTenantIdClaim()
    {
        var result = await Cli.RunAsync(_workspace, "token", "mint",
            "--signing-key", ValidSigningKey,
            "--sub", "alice",
            "--tenant", "acme");

        Assert.Equal(0, result.ExitCode);

        var token = result.StdOut.Trim();
        var decoded = DecodeJwt(token);
        Assert.Equal("acme", decoded["tenant_id"]);
    }

    [Fact]
    public async Task TokenMint_WithAudienceAddsAudClaim()
    {
        var result = await Cli.RunAsync(_workspace, "token", "mint",
            "--signing-key", ValidSigningKey,
            "--sub", "alice",
            "--audience", "myapi");

        Assert.Equal(0, result.ExitCode);

        var token = result.StdOut.Trim();
        var decoded = DecodeJwt(token);
        Assert.Equal("myapi", decoded["aud"]);
    }

    [Fact]
    public async Task TokenMint_WithIssuerAddsIssClaim()
    {
        var result = await Cli.RunAsync(_workspace, "token", "mint",
            "--signing-key", ValidSigningKey,
            "--sub", "alice",
            "--issuer", "myservice");

        Assert.Equal(0, result.ExitCode);

        var token = result.StdOut.Trim();
        var decoded = DecodeJwt(token);
        Assert.Equal("myservice", decoded["iss"]);
    }

    [Fact]
    public async Task TokenMint_WithGroupsAddsGroupsClaim()
    {
        var result = await Cli.RunAsync(_workspace, "token", "mint",
            "--signing-key", ValidSigningKey,
            "--sub", "alice",
            "--group", "finance",
            "--group", "operations");

        Assert.Equal(0, result.ExitCode);

        var token = result.StdOut.Trim();
        var decoded = DecodeJwt(token);
        var groupsList = decoded["groups"] as List<string>;
        Assert.NotNull(groupsList);
        Assert.Contains("finance", groupsList);
        Assert.Contains("operations", groupsList);
    }

    [Fact]
    public async Task TokenMint_WithScopesAddsScopeClaim()
    {
        var result = await Cli.RunAsync(_workspace, "token", "mint",
            "--signing-key", ValidSigningKey,
            "--sub", "alice",
            "--scope", "view:financial",
            "--scope", "view:pii");

        Assert.Equal(0, result.ExitCode);

        var token = result.StdOut.Trim();
        var decoded = DecodeJwt(token);
        var scopeList = decoded["scope"] as List<string>;
        Assert.NotNull(scopeList);
        Assert.Contains("view:financial", scopeList);
        Assert.Contains("view:pii", scopeList);
    }

    [Fact]
    public async Task TokenMint_DefaultsExpiresInToOneHour()
    {
        var beforeGeneration = DateTimeOffset.UtcNow;
        var result = await Cli.RunAsync(_workspace, "token", "mint",
            "--signing-key", ValidSigningKey,
            "--sub", "alice");
        var afterGeneration = DateTimeOffset.UtcNow;

        Assert.Equal(0, result.ExitCode);

        var token = result.StdOut.Trim();
        var decoded = DecodeJwt(token);
        var expiration = long.Parse(decoded["exp"]?.ToString() ?? "0");
        var expirationTime = DateTimeOffset.FromUnixTimeSeconds(expiration);

        // The expiration should be approximately 1 hour from now (allowing 5 seconds for test execution)
        var expectedExpiration = DateTimeOffset.UtcNow.AddHours(1);
        var timeDifference = Math.Abs((expirationTime - expectedExpiration).TotalSeconds);
        Assert.True(timeDifference < 5, $"Expiration time differs from expected by {timeDifference} seconds");
    }

    [Fact]
    public async Task TokenMint_HonorsCustomExpiresInDuration()
    {
        var result = await Cli.RunAsync(_workspace, "token", "mint",
            "--signing-key", ValidSigningKey,
            "--sub", "alice",
            "--expires-in", "30m");

        Assert.Equal(0, result.ExitCode);

        var token = result.StdOut.Trim();
        var decoded = DecodeJwt(token);
        var expiration = long.Parse(decoded["exp"]?.ToString() ?? "0");
        var iatTime = long.Parse(decoded["iat"]?.ToString() ?? "0");

        var durationSeconds = expiration - iatTime;
        Assert.Equal(1800, durationSeconds); // 30 minutes = 1800 seconds
    }

    [Fact]
    public async Task TokenMint_WithPrettyPrintsDecodedHeaderAndPayload()
    {
        var result = await Cli.RunAsync(_workspace, "token", "mint",
            "--signing-key", ValidSigningKey,
            "--sub", "alice",
            "--role", "Admin",
            "--pretty");

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(result.StdOut);
        Assert.NotEmpty(result.StdErr);

        // StdOut should contain the JWT, StdErr should contain pretty JSON
        var token = result.StdOut.Trim();
        Assert.Contains(".", token);

        Assert.Contains("header", result.StdErr);
        Assert.Contains("payload", result.StdErr);
        Assert.Contains("HS256", result.StdErr);
    }

    private static Dictionary<string, object> DecodeJwt(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
            throw new InvalidOperationException("Invalid JWT format");

        var payload = parts[1];
        // Add padding if needed
        var paddingNeeded = (4 - (payload.Length % 4)) % 4;
        payload += new string('=', paddingNeeded);

        var decodedBytes = Convert.FromBase64String(payload);
        var json = System.Text.Encoding.UTF8.GetString(decodedBytes);

        var doc = System.Text.Json.JsonDocument.Parse(json);
        var result = new Dictionary<string, object>();

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                result[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
            else if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                result[prop.Name] = prop.Value.GetInt64();
            }
            else if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in prop.Value.EnumerateArray())
                {
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                        list.Add(item.GetString() ?? string.Empty);
                }
                result[prop.Name] = list;
            }
            else
            {
                result[prop.Name] = prop.Value;
            }
        }

        return result;
    }
}
