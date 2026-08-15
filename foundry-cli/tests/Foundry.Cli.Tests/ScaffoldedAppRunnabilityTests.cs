using Xunit;

namespace Foundry.Cli.Tests;

/// <summary>
/// What `foundry new` has to produce for `dotnet run` to actually start and do what the schema said.
/// </summary>
/// <remarks>
/// <para>
/// Every assertion here comes from rebuilding a real application from its schema alone, after
/// deleting the previous implementation. The generated code was correct and the build succeeded
/// cold; the application still could not start, twice over, and quietly did less than its schema
/// declared once it did.
/// </para>
/// <para>
/// The existing <see cref="ScaffoldedAppWiringTests"/> gate reflects over <c>AddFoundry*</c> and
/// <c>MapFoundry*</c> names, so none of this was visible to it: an illegal default database name,
/// a missing environment file and an uncalled generated entry point are not missing registrations.
/// </para>
/// </remarks>
public class ScaffoldedAppRunnabilityTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "foundry-runnability-" + Guid.NewGuid().ToString("N"));

    private readonly string _projectDir;

    /// <summary>
    /// A project name in the ordinary .NET convention — dotted. That is the case that broke.
    /// </summary>
    private const string ProjectName = "Contoso.Orders.Api";

    public ScaffoldedAppRunnabilityTests()
    {
        Directory.CreateDirectory(_workspace);

        // Exercises the declarations whose wiring was missing: an encrypted field, a Kafka outbox,
        // a real-time channel and a unique index.
        var schemaPath = Path.Combine(_workspace, "schema.ir.json");
        File.WriteAllText(schemaPath, """
        {
          "namespace": "Contoso.Orders.Api.Domain",
          "entities": [
            {
              "name": "Customer",
              "enableRealTime": true,
              "enableKafkaOutbox": true,
              "kafkaTopic": "customers",
              "properties": [
                { "name": "Id", "type": "ObjectId", "isKey": true },
                { "name": "Reference", "type": "string", "attributes": ["Required", "Unique"] },
                { "name": "Email", "type": "string", "attributes": ["Required", "Encrypt"] }
              ],
              "apiEnabledMethods": ["GET", "POST", "GET_BY_ID"]
            }
          ]
        }
        """);

        var result = Cli.RunAsync(_workspace, "new", ProjectName, "--schema", schemaPath)
            .GetAwaiter().GetResult();

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Scaffolding failed.\nExit: {result.ExitCode}\nOut: {result.StdOut}\nErr: {result.StdErr}");
        }

        _projectDir = Path.Combine(_workspace, ProjectName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best effort */ }
    }

    private string Read(params string[] relativeParts)
        => File.ReadAllText(Path.Combine(new[] { _projectDir }.Concat(relativeParts).ToArray()));

    // ---- F4: the default database name ----

    [Fact]
    public void TheDefaultDatabaseNameIsLegalForMongoDb()
    {
        // A dot is idiomatic in a .NET project name and illegal in a MongoDB database name. The
        // default was "{ProjectName}Db", so `foundry new Contoso.Orders.Api` produced an app that
        // threw ArgumentException out of AddFoundryMongo before serving a request -- after the CLI
        // had printed READY-TO-RUN.
        var program = Read("Program.cs");

        var start = program.IndexOf("options.DatabaseName", StringComparison.Ordinal);
        Assert.True(start >= 0, "Program.cs no longer sets options.DatabaseName.");
        var fallback = program.Substring(start, program.IndexOf(';', start) - start);

        Assert.DoesNotContain(".", fallback.Split("??").Last(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Contoso.Orders.Api", "contoso_orders_api")]
    [InlineData("Simple", "simple")]
    [InlineData("With-Dashes", "with_dashes")]
    [InlineData("...", "foundry")]
    public void ProjectNamesReduceToIdentifiersEveryConsumerAccepts(string projectName, string expected)
    {
        // MongoDB forbids / \ . " $ * < > : | ? and the null character; Docker container names
        // admit only [a-zA-Z0-9_.-]. Lowercase alphanumerics and underscores satisfy both.
        Assert.Equal(expected, Foundry.Cli.Program.SanitizeIdentifier(projectName));
    }

    [Fact]
    public void TheDatabaseNameIsAlsoWrittenWhereAnOperatorWouldLookForIt()
    {
        // Not only as a code fallback: the setting has to be visible in configuration, or changing
        // it means editing Program.cs.
        Assert.Contains("MongoDbSettings", Read("appsettings.json"), StringComparison.Ordinal);
    }

    // ---- F7: the environment that makes the generated secrets readable ----

    [Fact]
    public void LaunchSettingsSelectsTheDevelopmentEnvironment()
    {
        // `foundry new` generates a JWT signing key into appsettings.Development.json and then
        // printed `dotnet run` as the next step. With no launchSettings.json the environment
        // defaults to Production, that file is never loaded, and startup fails with
        // "No bearer token validation is configured at 'Authentication:Jwt'" -- the scaffolder
        // created a secret its own run command could not see.
        var launchSettings = Read("Properties", "launchSettings.json");

        Assert.Contains("ASPNETCORE_ENVIRONMENT", launchSettings, StringComparison.Ordinal);
        Assert.Contains("Development", launchSettings, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDevelopmentSecretsFileIsStillGitignored()
    {
        // It now holds a field-encryption key as well as a signing key, which makes committing it
        // worse, not better.
        Assert.Contains("appsettings.Development.json", Read(".gitignore"), StringComparison.Ordinal);
    }

    // ---- F6: a declared Encrypt needs a key ----

    [Fact]
    public void ASchemaThatEncryptsAFieldGetsAnEncryptionKeyWired()
    {
        // The compiler emitted the encrypted mapping and the host wired no provider, so the app
        // started and answered 500 to every write touching that entity -- discoverable only by
        // hitting it.
        Assert.Contains("options.EncryptionKey", Read("Program.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void ADevelopmentEncryptionKeyIsGeneratedAlongsideTheSigningKey()
    {
        var devSettings = Read("appsettings.Development.json");

        Assert.Contains("EncryptionKey", devSettings, StringComparison.Ordinal);

        // 32 bytes, because that is what AesEncryptionProvider requires; a shorter key fails at
        // startup rather than at first write.
        var key = System.Text.Json.JsonDocument.Parse(devSettings)
            .RootElement.GetProperty("MongoDbSettings").GetProperty("EncryptionKey").GetString();
        Assert.Equal(32, Convert.FromBase64String(key!).Length);
    }

    // ---- F5: generated entry points the host never called ----

    [Fact]
    public void DeclaredIndexesAreCreatedAtStartup()
    {
        // Diagnostics/IndexVerification.cs was generated and never invoked, so Unique, Indexed and
        // TextIndex existed only in the schema: a Unique property admitted duplicates and the
        // queries meant to be index-backed were collection scans.
        Assert.Contains("EnsureIndexesAsync", Read("Program.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheRealTimeSurfaceIsMappedExactlyOnce()
    {
        // RealTime/RealTimeConfiguration.cs reads like an unwired entry point -- generated, and
        // never named by Program.cs -- and it is not one: its whole body is a call to
        // MapFoundryRealTime(), which Program.cs already makes directly. Calling both registers
        // /realtime/sse twice, and the duplicate route match answers 500 where an anonymous caller
        // must get 401.
        //
        // That mistake was made, shipped to CI, and caught by the runtime smoke test. This asserts
        // the count rather than the presence of a name, so either half going missing fails.
        var program = Read("Program.cs");

        var mappings =
            CountOccurrences(program, "MapFoundryRealTime")
            + CountOccurrences(program, "MapGeneratedRealTimeEndpoints");

        Assert.Equal(1, mappings);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void AKafkaSchemaGetsAProducerAndAnOutboxWriter()
    {
        var program = Read("Program.cs");

        // Only the consumer handlers were registered: the app could receive events and send none,
        // and the outbox collection was never even created, so the topics stayed empty and the
        // generated consumers waited on messages nothing produced.
        Assert.Contains("AddGeneratedKafkaHandlers", program, StringComparison.Ordinal);
        Assert.Contains("AddFoundryKafkaProducer", program, StringComparison.Ordinal);
        Assert.Contains("OutboxDomainEventBehavior", program, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryGeneratedRegistrationEntryPointIsReferencedByTheHost()
    {
        // The general form of the defect, rather than one instance of it: the compiler emits an
        // entry point, the scaffolder writes Program.cs, and nothing connects them. Each of these
        // is a public method whose only purpose is to be called by the host.
        var program = Read("Program.cs");

        // Keyed by the generated file, so this asserts only about what this schema actually
        // produced -- a schema declaring no business rules should not be required to call
        // AddGeneratedBusinessRules, and demanding it would make the gate wrong in the other
        // direction.
        //
        // RealTime/RealTimeConfiguration.cs is deliberately absent from this list. It is generated
        // and never called, and that is correct: its body only calls MapFoundryRealTime(), which
        // Program.cs already calls directly, so wiring it too would double-register the route. Not
        // every uncalled generated file is a defect, which is exactly what makes this class of bug
        // hard to see -- and why the entry point is named per file rather than inferred.
        var entryPoints = new Dictionary<string, string>
        {
            [Path.Combine("Rules", "RuleRegistrations.cs")] = "AddGeneratedBusinessRules",
            [Path.Combine("Kafka", "KafkaRegistrations.cs")] = "AddGeneratedKafkaHandlers",
            [Path.Combine("Diagnostics", "IndexVerification.cs")] = "EnsureIndexesAsync",
        };

        var emitted = entryPoints
            .Where(e => File.Exists(Path.Combine(_projectDir, "Generated", e.Key)))
            .ToList();

        // If the schema stopped producing any of them the gate would pass while checking nothing,
        // which is the failure mode this whole class exists to prevent.
        Assert.True(emitted.Count >= 2,
            $"Expected this schema to emit at least two registration files; found {emitted.Count}.");

        var missing = emitted
            .Where(e => !program.Contains(e.Value, StringComparison.Ordinal))
            .Select(e => $"{e.Value} (from Generated/{e.Key})")
            .ToList();

        Assert.True(missing.Count == 0,
            "Generated entry points that nothing in the scaffolded host calls: " + string.Join(", ", missing));
    }

    // ---- F3: the quickstart the CLI prints has to work ----

    [Fact]
    public void NoComposeImageFloatsOnLatest()
    {
        // `docker compose up -d` is step 2 of the three lines this command prints on success. It
        // stopped working when Bitnami withdrew bitnami/kafka:latest -- and because Compose aborts
        // the whole pull set on a failure, MongoDB did not start either, so the quickstart could not
        // reach step 3 on a clean machine. A :latest tag means an upstream registry decides the day
        // that happens, not this repository.
        var compose = Read("docker-compose.yml");

        var floating = compose
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("image:", StringComparison.Ordinal)
                        && l.EndsWith(":latest", StringComparison.Ordinal))
            .ToList();

        Assert.True(floating.Count == 0, "Unpinned compose images: " + string.Join(", ", floating));
    }

    [Fact]
    public void EveryComposeImageCarriesAnExplicitTag()
    {
        var untagged = Read("docker-compose.yml")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("image:", StringComparison.Ordinal))
            .Where(l => !l["image:".Length..].Trim().Contains(':'))
            .ToList();

        Assert.True(untagged.Count == 0, "Compose images with no tag: " + string.Join(", ", untagged));
    }

    [Fact]
    public void TheQuickstartDoesNotClaimPort8080()
    {
        // 8080 is the most contended port on a developer machine, and a quickstart that fails to
        // bind it reads as the framework being broken.
        var hostPorts = Read("docker-compose.yml")
            .Split('\n')
            .Select(l => l.Trim().Trim('-', ' ', '"'))
            .Where(l => l.Contains(':') && l.Split(':').Length == 2)
            .Where(l => int.TryParse(l.Split(':')[0], out _))
            .Select(l => l.Split(':')[0])
            .ToList();

        Assert.DoesNotContain("8080", hostPorts);
    }
}
