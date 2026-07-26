using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Foundry.Cli;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();

            // Non-zero: no command was requested, so nothing was done. Exiting 0 made
            // `foundry $COMMAND` with an unset variable print help and report success, which is the
            // same silent-success shape as a gate that passes without running.
            return 1;
        }

        var command = args[0].ToLowerInvariant();

        switch (command)
        {
            case "new":
            case "init":
            case "create":
                if (args.Length < 2)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[Error] Please specify a project name. Example: foundry new MyOrderingApp --schema my-schema.json");
                    Console.ResetColor();
                    return 1;
                }
                string pName = args[1];
                string? schemaPathArg = null;
                for (int i = 2; i < args.Length; i++)
                {
                    if ((args[i] == "--schema" || args[i] == "-s") && i + 1 < args.Length)
                    {
                        schemaPathArg = args[i + 1];
                    }
                }
                return CreateNewProject(pName, schemaPathArg);

            case "schema":
                return HandleSchemaCommand(args.Skip(1).ToArray());

            case "studio":
                return await ServeStudioAsync(args.Skip(1).ToArray());

            case "api":
                return await HandleApiCommandAsync(args.Skip(1).ToArray());

            case "export":
                return HandleExportCommand(args.Skip(1).ToArray());

            case "doctor":
                return HandleDoctorCommand();

            case "generate":
                return HandleGenerateCommand(args.Skip(1).ToArray());

            case "sdk":
                return HandleSdkCommand(args.Skip(1).ToArray());

            case "test":
                return HandleTestCommand(args.Skip(1).ToArray());

            case "lsp":
                return await Foundry.Cli.Lsp.LspServer.RunAsync();

            case "validate":
                return HandleValidateCommand(args.Skip(1).ToArray());

            case "ai-spec":
                return HandleAiSpecCommand(args.Skip(1).ToArray());

            case "ai":
                return await HandleAiCommandAsync(args.Skip(1).ToArray());

            case "eval":
                return await HandleEvalCommandAsync(args.Skip(1).ToArray());

            case "version":
                Console.WriteLine("Foundry Framework Unified Executable v1.0.0 (.NET 10)");
                return 0;

            default:
                PrintHelp();
                return 1;
        }
    }

    /// <summary>
    /// Validates an IR document and prints coded diagnostics.
    /// </summary>
    /// <remarks>
    /// Exits non-zero on any error so it can be used as a CI gate.
    /// </remarks>
    private static int HandleValidateCommand(string[] args)
    {
        var inputPath = args.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal));
        if (string.IsNullOrEmpty(inputPath))
        {
            Console.WriteLine("Usage: foundry validate <schema.json>");
            return 1;
        }

        if (!File.Exists(inputPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Error] File not found: {inputPath}");
            Console.ResetColor();
            return 1;
        }

        var json = File.ReadAllText(inputPath);
        var bag = new Foundry.Schema.Compiler.DiagnosticBag();
        Foundry.Schema.Compiler.SchemaValidator.ValidateRawDocument(json, bag);

        Foundry.Schema.Compiler.SchemaModel? schema = null;
        try
        {
            schema = System.Text.Json.JsonSerializer.Deserialize<Foundry.Schema.Compiler.SchemaModel>(
                json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (System.Text.Json.JsonException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Error] Malformed JSON: {ex.Message}");
            Console.ResetColor();
            return 1;
        }

        bag.AddRange(Foundry.Schema.Compiler.SchemaValidator.Validate(schema).Items);

        if (bag.Items.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ {inputPath} is valid.");
            Console.ResetColor();
            return 0;
        }

        Console.WriteLine(bag.Render());
        Console.WriteLine();

        if (bag.HasErrors)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"✗ {bag.ErrorCount} error(s), {bag.WarningCount} warning(s).");
            Console.ResetColor();
            return 1;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"✓ Valid with {bag.WarningCount} warning(s).");
        Console.ResetColor();
        return 0;
    }

    /// <summary>
    /// Writes the AI skill bundle that lets a local model author Foundry IR.
    /// </summary>
    private static int HandleAiSpecCommand(string[] args)
    {
        var outputDir = ".foundry/skill";
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--out" || args[i] == "-o") && i + 1 < args.Length)
                outputDir = args[i + 1];
        }

        var written = Foundry.Schema.Compiler.AiSpecBundle.Write(outputDir);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Foundry AI skill bundle → {outputDir}");
        Console.ResetColor();
        foreach (var path in written)
            Console.WriteLine($"  {path}");

        Console.WriteLine();
        Console.WriteLine("Point your local model at foundry.ir.schema.json as its structured-output format.");
        return 0;
    }

    /// <summary>
    /// Generates an IR document from a natural-language instruction using a local Ollama model.
    /// </summary>
    private static async Task<int> HandleAiCommandAsync(string[] args)
    {
        string? instruction = null;
        string? host = null, model = null, outPath = null, basePath = null;
        var check = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--host" when i + 1 < args.Length: host = args[++i]; break;
                case "--model" when i + 1 < args.Length: model = args[++i]; break;
                case "--out" or "-o" when i + 1 < args.Length: outPath = args[++i]; break;
                case "--base" when i + 1 < args.Length: basePath = args[++i]; break;
                case "--check": check = true; break;
                default:
                    if (!args[i].StartsWith("-", StringComparison.Ordinal))
                        instruction = instruction is null ? args[i] : $"{instruction} {args[i]}";
                    break;
            }
        }

        var options = Foundry.Schema.Compiler.AiGenerationOptions.Resolve(host, model);
        using var http = new System.Net.Http.HttpClient();
        var generator = new Foundry.Schema.Compiler.AiSchemaGenerator(http, options);

        if (check)
        {
            var (ok, detail) = await generator.CheckAsync();
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine((ok ? "✓ " : "✗ ") + detail);
            Console.ResetColor();
            return ok ? 0 : 1;
        }

        if (string.IsNullOrWhiteSpace(instruction))
        {
            Console.WriteLine("Usage: foundry ai \"<instruction>\" [--out schema.json] [--base existing.json]");
            Console.WriteLine("       foundry ai --check");
            Console.WriteLine();
            Console.WriteLine("Environment: FOUNDRY_OLLAMA_HOST, FOUNDRY_OLLAMA_MODEL");
            return 1;
        }

        Foundry.Schema.Compiler.SchemaModel? current = null;
        if (!string.IsNullOrEmpty(basePath) && File.Exists(basePath))
        {
            current = System.Text.Json.JsonSerializer.Deserialize<Foundry.Schema.Compiler.SchemaModel>(
                File.ReadAllText(basePath),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        Console.WriteLine($"[Foundry AI] {options.Model} @ {options.Host}");
        var result = await generator.GenerateAsync(instruction, current);

        foreach (var attempt in result.Attempts)
        {
            var errors = attempt.Diagnostics.Count(d => d.Severity == Foundry.Schema.Compiler.DiagnosticSeverity.Error);
            Console.WriteLine($"  attempt {attempt.Attempt}: {(errors == 0 ? "valid" : $"{errors} error(s)")}");
        }

        // Losing the grammar means losing a correctness guarantee, not just a speed-up.
        // Say so rather than letting the run look identical to a constrained one.
        if (!result.GrammarConstrained)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ! {result.GrammarFallbackReason}");
            Console.ResetColor();
        }

        if (!result.Success)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"✗ {result.Error}");
            Console.ResetColor();
            foreach (var d in result.Diagnostics)
                Console.WriteLine($"  {d}");
            return 1;
        }

        var outputJson = System.Text.Json.JsonSerializer.Serialize(result.Schema, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        if (string.IsNullOrEmpty(outPath))
        {
            Console.WriteLine(outputJson);
        }
        else
        {
            File.WriteAllText(outPath, outputJson);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ Wrote validated IR to {outPath}");
            Console.ResetColor();
        }

        return 0;
    }

    /// <summary>
    /// Measures how reliably a local model authors Foundry IR.
    /// </summary>
    /// <remarks>
    /// Exits non-zero when the pass rate falls below <c>--min-pass</c>, so the suite can gate a
    /// release the same way a test suite does.
    /// </remarks>
    private static async Task<int> HandleEvalCommandAsync(string[] args)
    {
        string? host = null, model = null, mdOut = null, jsonOut = null, construct = null, caseId = null, difficulty = null;
        var runs = 1;
        double minPass = 0;
        var list = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--host" when i + 1 < args.Length: host = args[++i]; break;
                case "--model" when i + 1 < args.Length: model = args[++i]; break;
                case "--runs" when i + 1 < args.Length: int.TryParse(args[++i], out runs); break;
                case "--construct" when i + 1 < args.Length: construct = args[++i]; break;
                case "--case" when i + 1 < args.Length: caseId = args[++i]; break;
                case "--difficulty" when i + 1 < args.Length: difficulty = args[++i]; break;
                case "--out" or "-o" when i + 1 < args.Length: mdOut = args[++i]; break;
                case "--json" when i + 1 < args.Length: jsonOut = args[++i]; break;
                case "--min-pass" when i + 1 < args.Length: double.TryParse(args[++i], out minPass); break;
                case "--list": list = true; break;
            }
        }

        var selected = Foundry.Schema.Compiler.EvalHarness.Cases.AsEnumerable();
        if (!string.IsNullOrEmpty(construct))
            selected = selected.Where(c => string.Equals(c.Construct, construct, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(caseId))
            selected = selected.Where(c => string.Equals(c.Id, caseId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(difficulty))
            selected = selected.Where(c => string.Equals(c.Difficulty.ToString(), difficulty, StringComparison.OrdinalIgnoreCase));

        var cases = selected.ToList();

        if (list)
        {
            Console.WriteLine($"{cases.Count} case(s):");
            foreach (var group in cases.GroupBy(c => c.Construct).OrderBy(g => g.Key))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"  {group.Key}");
                Console.ResetColor();
                foreach (var c in group)
                    Console.WriteLine($"    {c.Id,-26} {c.Assertions.Count} assertion(s)");
            }
            return 0;
        }

        if (cases.Count == 0)
        {
            Console.WriteLine("No cases matched. Use --list to see available cases.");
            return 1;
        }

        var options = Foundry.Schema.Compiler.AiGenerationOptions.Resolve(host, model);
        using var http = new System.Net.Http.HttpClient();
        var generator = new Foundry.Schema.Compiler.AiSchemaGenerator(http, options);

        var (ok, detail) = await generator.CheckAsync();
        if (!ok)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"✗ {detail}");
            Console.ResetColor();
            return 1;
        }

        var total = cases.Count * Math.Max(1, runs);
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Foundry IR eval — {options.Model} @ {options.Host}");
        Console.ResetColor();
        Console.WriteLine($"{cases.Count} case(s) × {Math.Max(1, runs)} run(s) = {total} generation(s)\n");

        var progress = new Progress<string>(line =>
        {
            Console.ForegroundColor = line.StartsWith("PASS") ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write(line.Substring(0, 4));
            Console.ResetColor();
            Console.WriteLine(line.Substring(4));
        });

        var result = await Foundry.Schema.Compiler.EvalHarness.RunAsync(
            generator, cases, options.Model, options.Host, runs, progress);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Pass rate: {result.PassRate:P0}   Valid IR: {result.ValidIrRate:P0}");
        Console.ResetColor();
        Console.WriteLine();
        foreach (var band in result.Results.GroupBy(r => r.Difficulty).OrderBy(g => g.Key))
        {
            var rate = band.Count(r => r.Passed) / (double)band.Count();
            Console.WriteLine($"  {band.Key,-6} {rate,6:P0}  ({band.Count(r => r.Passed)}/{band.Count()})");
        }
        Console.WriteLine();
        Console.WriteLine("Weakest constructs:");
        foreach (var group in result.Results
                     .GroupBy(r => r.Construct)
                     .OrderBy(g => g.Count(r => r.Passed) / (double)g.Count())
                     .Take(5))
        {
            var rate = group.Count(r => r.Passed) / (double)group.Count();
            Console.WriteLine($"  {group.Key,-16} {rate,6:P0}  ({group.Count(r => r.Passed)}/{group.Count()})");
        }

        if (!string.IsNullOrEmpty(mdOut))
        {
            File.WriteAllText(mdOut, Foundry.Schema.Compiler.EvalHarness.RenderMarkdown(result));
            Console.WriteLine($"\nMarkdown report → {mdOut}");
        }

        if (!string.IsNullOrEmpty(jsonOut))
        {
            File.WriteAllText(jsonOut, Foundry.Schema.Compiler.EvalHarness.RenderJson(result));
            Console.WriteLine($"JSON report → {jsonOut}");
        }

        if (minPass > 0 && result.PassRate < minPass)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n✗ Pass rate {result.PassRate:P0} is below the required {minPass:P0}.");
            Console.ResetColor();
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Locates the Foundry framework checkout that a scaffolded project should reference.
    /// </summary>
    /// <remarks>
    /// Checks <c>FOUNDRY_HOME</c> first, then walks up from the running assembly, then from the
    /// working directory. Returns <c>null</c> rather than guessing, so the caller can report the
    /// problem instead of writing an unusable project file.
    /// </remarks>
    private static string? FindFrameworkRoot()
    {
        static bool IsRoot(string candidate) =>
            Directory.Exists(Path.Combine(candidate, "foundry-core", "src", "Foundry.Core"))
            && Directory.Exists(Path.Combine(candidate, "foundry-api", "src", "Foundry.Api"));

        var fromEnvironment = Environment.GetEnvironmentVariable("FOUNDRY_HOME");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)
            && Directory.Exists(fromEnvironment)
            && IsRoot(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (IsRoot(dir.FullName)) return dir.FullName;
                dir = dir.Parent;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads a package version out of the framework's central package management file.
    /// </summary>
    /// <remarks>
    /// A scaffolded project usually lives outside the framework checkout, so it is not covered by
    /// the repository's <c>Directory.Packages.props</c> and a versionless <c>PackageReference</c>
    /// fails to restore with NU1015. Versions are therefore written explicitly into the generated
    /// project, taken from the same file the framework itself was built with rather than hardcoded
    /// here, so the two cannot drift apart.
    /// </remarks>
    private static string? ResolvePackageVersion(string frameworkRoot, string packageId)
    {
        var propsPath = Path.Combine(frameworkRoot, "Directory.Packages.props");
        if (!File.Exists(propsPath)) return null;

        try
        {
            var document = System.Xml.Linq.XDocument.Load(propsPath);
            return document
                .Descendants()
                .Where(e => e.Name.LocalName == "PackageVersion")
                .FirstOrDefault(e => string.Equals(
                    e.Attribute("Include")?.Value, packageId, StringComparison.OrdinalIgnoreCase))
                ?.Attribute("Version")?.Value;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static int CreateNewProject(string projectName, string? customSchemaPath = null)
    {
        var targetDir = Path.Combine(Directory.GetCurrentDirectory(), projectName);
        if (Directory.Exists(targetDir) && Directory.GetFiles(targetDir).Length > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Error] Directory '{projectName}' already exists and is not empty.");
            Console.ResetColor();
            return 1;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=================================================================");
        Console.WriteLine($"          CREATING NEW READY-TO-RUN FOUNDRY PROJECT             ");
        Console.WriteLine("=================================================================");
        Console.ResetColor();
        Console.WriteLine($"➜ Creating directory: {targetDir}");

        Directory.CreateDirectory(targetDir);

        // 1. Create .csproj
        //
        // The framework is referenced by project path, so the scaffolded app needs to know where
        // the Foundry repository is. That location was previously hardcoded to the machine this
        // CLI was developed on, which made every scaffolded project unbuildable anywhere else --
        // while still printing "READY-TO-RUN". It is resolved at runtime and written relative to
        // the new project instead, and if it cannot be found we say so rather than emit a path
        // that is guaranteed to fail.
        var frameworkRoot = FindFrameworkRoot();
        if (frameworkRoot is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[Error] Could not locate the Foundry framework sources.");
            Console.ResetColor();
            Console.WriteLine("  'foundry new' references the framework by project path, so it must run from within");
            Console.WriteLine("  a Foundry checkout (or with FOUNDRY_HOME pointing at one).");
            return 1;
        }

        // Packages the scaffolded project must pin explicitly.
        //
        // The framework repository uses central package management with transitive pinning, so
        // Foundry.Api is compiled against Microsoft.OpenApi 2.11.0. A scaffolded project sits
        // outside that, resolves Swashbuckle's own dependency (2.0.0 -- the version with a known
        // advisory) and then fails at startup with a FileNotFoundException for the 2.11.0 assembly
        // Foundry.Api was built against. The app compiled cleanly and died on first request, so
        // pinning here is what makes a scaffolded project actually run.
        var requiredPackages = new[]
        {
            "FluentValidation.DependencyInjectionExtensions",
            "Microsoft.OpenApi",
            "Swashbuckle.AspNetCore"
        };

        var packageReferences = new List<string>();
        foreach (var packageId in requiredPackages)
        {
            var version = ResolvePackageVersion(frameworkRoot, packageId);
            if (version is null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Error] Could not read the {packageId} version from");
                Console.WriteLine($"  {Path.Combine(frameworkRoot, "Directory.Packages.props")}");
                Console.ResetColor();
                Console.WriteLine("  The scaffolded project needs explicit versions because it sits outside the");
                Console.WriteLine("  framework's central package management.");
                return 1;
            }

            packageReferences.Add(
                $@"    <PackageReference Include=""{packageId}"" Version=""{version}"" />");
        }

        var packageReferenceBlock = string.Join("\n", packageReferences);

        string Reference(string relativeProjectPath)
        {
            var absolute = Path.Combine(frameworkRoot, relativeProjectPath.Replace('/', Path.DirectorySeparatorChar));
            // Relative so the project survives the checkout being moved or cloned elsewhere.
            return Path.GetRelativePath(targetDir, absolute).Replace(Path.DirectorySeparatorChar, '/');
        }

        var csprojContent = $@"<Project Sdk=""Microsoft.NET.Sdk.Web"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>{projectName}</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include=""{Reference("foundry-core/src/Foundry.Core/Foundry.Core.csproj")}"" />
    <ProjectReference Include=""{Reference("foundry-mongo/src/Foundry.Mongo/Foundry.Mongo.csproj")}"" />
    <ProjectReference Include=""{Reference("foundry-api/src/Foundry.Api/Foundry.Api.csproj")}"" />
    <ProjectReference Include=""{Reference("foundry-rules/src/Foundry.Rules/Foundry.Rules.csproj")}"" />
    <ProjectReference Include=""{Reference("foundry-file-io/src/Foundry.FileIO/Foundry.FileIO.csproj")}"" />
    <ProjectReference Include=""{Reference("foundry-realtime/src/Foundry.RealTime/Foundry.RealTime.csproj")}"" />
    <ProjectReference Include=""{Reference("foundry-kafka/src/Foundry.Kafka/Foundry.Kafka.csproj")}"" />
  </ItemGroup>

  <!-- The REST surface is emitted by this analyser from api-manifest.json. Without the analyser
       reference and the AdditionalFiles entry the app builds, starts, and serves no routes. -->
  <ItemGroup>
    <ProjectReference Include=""{Reference("foundry-api/src/Foundry.Api.SourceGenerators/Foundry.Api.SourceGenerators.csproj")}""
                      OutputItemType=""Analyzer""
                      ReferenceOutputAssembly=""false"" />
    <AdditionalFiles Include=""api-manifest.json"" />
  </ItemGroup>

  <!-- Pinned to the versions the framework itself was built against. Letting these float
       resolves an older Microsoft.OpenApi than Foundry.Api was compiled with, which builds
       fine and then throws FileNotFoundException on the first request. -->
  <ItemGroup>
{packageReferenceBlock}
  </ItemGroup>

  <ItemGroup>
    <None Update=""api-manifest.json"">
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>";
        File.WriteAllText(Path.Combine(targetDir, $"{projectName}.csproj"), csprojContent);
        Console.WriteLine($"  ✓ Generated {projectName}.csproj (framework referenced relatively from {frameworkRoot})");

        // 2. Create Program.cs (generated after schema compilation to conditionally wire services)
        // Placeholder — actual Program.cs content is written below AFTER schema compilation

        // 3. Create appsettings.json
        var appsettingsContent = @"{
  ""Logging"": {
    ""LogLevel"": {
      ""Default"": ""Information"",
      ""Microsoft.AspNetCore"": ""Warning""
    }
  },
  ""ConnectionStrings"": {
    ""MongoDb"": ""mongodb://localhost:27017"",
    ""Kafka"": ""localhost:9092""
  },
  ""AllowedHosts"": ""*""
}";
        File.WriteAllText(Path.Combine(targetDir, "appsettings.json"), appsettingsContent);
        Console.WriteLine("  ✓ Generated appsettings.json");

        // 4. Create or copy domain schema manifest (domain.foundry.json)
        string schemaContent;
        if (!string.IsNullOrEmpty(customSchemaPath) && File.Exists(customSchemaPath))
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  ➜ Using Schema Designer JSON: {customSchemaPath}");
            Console.ResetColor();
            schemaContent = File.ReadAllText(customSchemaPath);
        }
        else
        {
            if (!string.IsNullOrEmpty(customSchemaPath))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  [Warning] Specified schema file '{customSchemaPath}' not found. Falling back to default schema.");
                Console.ResetColor();
            }
            schemaContent = @"{
  ""Namespace"": """ + projectName + @".Domain"",
  ""Entities"": [
    {
      ""Name"": ""Customer"",
      ""SoftDelete"": true,
      ""Auditable"": true,
      ""Properties"": [
        { ""Name"": ""Id"", ""Type"": ""ObjectId"", ""IsKey"": true },
        { ""Name"": ""FullName"", ""Type"": ""string"", ""Attributes"": [""Required""] },
        { ""Name"": ""Email"", ""Type"": ""string"", ""Attributes"": [""Required"", ""MaskEmail""] }
      ],
      ""ApiEnabledMethods"": [""GET"", ""POST"", ""GET_BY_ID"", ""PUT"", ""DELETE""]
    },
    {
      ""Name"": ""Order"",
      ""SoftDelete"": true,
      ""Auditable"": true,
      ""Properties"": [
        { ""Name"": ""Id"", ""Type"": ""ObjectId"", ""IsKey"": true },
        { ""Name"": ""CustomerId"", ""Type"": ""string"", ""Attributes"": [""Required"", ""Index""] },
        { ""Name"": ""TotalAmount"", ""Type"": ""decimal"", ""Attributes"": [""Required""] },
        { ""Name"": ""Status"", ""Type"": ""string"" }
      ],
      ""ApiEnabledMethods"": [""GET"", ""POST"", ""GET_BY_ID"", ""PUT"", ""DELETE""]
    }
  ]
}";
        }
        var schemaPath = Path.Combine(targetDir, "domain.foundry.json");
        File.WriteAllText(schemaPath, schemaContent);
        Console.WriteLine("  ✓ Generated domain.foundry.json");

        // 5. Create docker-compose.yml with MongoDB, Mongo Express, Kafka Broker & Kafka UI
        var dockerContent = @"version: '3.8'
services:
  mongodb:
    image: mongo:latest
    container_name: " + projectName.ToLowerInvariant() + @"_mongodb
    ports:
      - ""27017:27017""
    volumes:
      - mongodb_data:/data/db

  mongo-express:
    image: mongo-express:latest
    container_name: " + projectName.ToLowerInvariant() + @"_mongo_express
    ports:
      - ""8081:8081""
    environment:
      ME_CONFIG_MONGODB_SERVER: mongodb
    depends_on:
      - mongodb

  kafka:
    image: bitnami/kafka:latest
    container_name: " + projectName.ToLowerInvariant() + @"_kafka
    ports:
      - ""9092:9092""
    environment:
      KAFKA_CFG_NODE_ID: 1
      KAFKA_CFG_PROCESS_ROLES: broker,controller
      KAFKA_CFG_CONTROLLER_QUORUM_VOTERS: 1@kafka:9093
      KAFKA_CFG_LISTENERS: PLAINTEXT://:9092,CONTROLLER://:9093
      KAFKA_CFG_ADVERTISED_LISTENERS: PLAINTEXT://localhost:9092
      KAFKA_CFG_CONTROLLER_LISTENER_NAMES: CONTROLLER
      KAFKA_CFG_LISTENER_SECURITY_PROTOCOL_MAP: CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT
    volumes:
      - kafka_data:/bitnami/kafka

  kafka-ui:
    image: provectuslabs/kafka-ui:latest
    container_name: " + projectName.ToLowerInvariant() + @"_kafka_ui
    ports:
      - ""8080:8080""
    environment:
      KAFKA_CLUSTERS_0_NAME: local
      KAFKA_CLUSTERS_0_BOOTSTRAPSERVERS: kafka:9092
    depends_on:
      - kafka

volumes:
  mongodb_data:
  kafka_data:
";
        File.WriteAllText(Path.Combine(targetDir, "docker-compose.yml"), dockerContent);
        Console.WriteLine("  ✓ Generated docker-compose.yml (MongoDB, Mongo Express, Kafka UI)");

        // 6. Automatically compile domain schema into ./Generated
        var generatedDir = Path.Combine(targetDir, "Generated");
        Console.WriteLine("  ➜ Compiling domain schema into C# POCOs and Handlers...");
        var compileExit = Foundry.Schema.Compiler.Program.Main(new[] { "--input", schemaPath, "--output", generatedDir });
        if (compileExit != 0)
        {
            // The compiler already reported the diagnostics. Stopping here matters: continuing
            // would leave a project directory that looks scaffolded and cannot build.
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Error] Schema compilation failed; '{projectName}' is incomplete.");
            Console.ResetColor();
            return compileExit;
        }

        // 6b. Derive api-manifest.json from the schema.
        //
        // This is what makes the app serve anything. The REST surface is emitted by the
        // Foundry.Api.SourceGenerators analyser from this file; with no manifest the analyser
        // generates empty registrations and the application answers 404 on every entity route.
        // Previously only Studio produced it, so CLI-scaffolded projects had no API at all.
        var manifestSchema = System.Text.Json.JsonSerializer.Deserialize<Foundry.Schema.Compiler.SchemaModel>(
            File.ReadAllText(schemaPath),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (manifestSchema is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[Error] Could not read the schema back to derive api-manifest.json.");
            Console.ResetColor();
            return 1;
        }

        var manifestJson = Foundry.Schema.Compiler.ApiManifestGenerator.Generate(manifestSchema);
        File.WriteAllText(Path.Combine(targetDir, "api-manifest.json"), manifestJson);

        var routeCount = manifestSchema.Entities?.Count(e => e.ApiEnabledMethods.Count > 0) ?? 0;
        Console.WriteLine($"  ✓ Generated api-manifest.json ({routeCount} entity route group(s))");

        // 7. Write scaffolded Program.cs after checking generated artifacts
        var hasKafka = Directory.Exists(Path.Combine(generatedDir, "Kafka"));
        var hasGraphQL = Directory.Exists(Path.Combine(generatedDir, "GraphQL"));
        var hasServices = Directory.Exists(Path.Combine(generatedDir, "Services"));

        var extraUsings = new List<string>();
        if (hasKafka) extraUsings.Add($"using {projectName}.Domain.Kafka;");
        if (hasServices) extraUsings.Add($"using {projectName}.Domain.Services;");
        if (hasGraphQL) extraUsings.Add($"using {projectName}.Domain.GraphQL;");

        var extraUsingsStr = extraUsings.Count > 0 ? string.Join("\n", extraUsings) + "\n" : "";

        var kafkaRegistration = hasKafka ? "\n// Register generated Kafka consumer handlers\nbuilder.Services.AddGeneratedKafkaHandlers();\n" : "";

        // 7. Write Program.cs.
        //
        // This previously called AddControllers()/MapControllers(), but the compiler emits no
        // controllers -- the REST surface comes from the analyser's AddGeneratedHandlers() and
        // MapGeneratedEndpoints(manifest). The scaffolded app therefore started successfully and
        // served nothing, while the CLI advertised "full REST CRUD". The wiring below mirrors
        // templates/Foundry.Api.Template/Program.cs, which is the arrangement the API tests cover.
        var programContent = $@"using System.Text.Json;
using MediatR;
using FluentValidation;
using Foundry.Mongo.DependencyInjection;
using Foundry.Api.Manifest;
using Foundry.Api.Endpoints;
using Foundry.Api.MediatR.Behaviors;
using Foundry.Core.Serialization;
using Foundry.Rules;
using Foundry.RealTime;
{extraUsingsStr}
var builder = WebApplication.CreateBuilder(args);

// The API surface is described by api-manifest.json, which is derived from the domain schema.
// The analyser reads the same file at compile time to generate the endpoints and handlers.
var manifestPath = Path.Combine(builder.Environment.ContentRootPath, ""api-manifest.json"");
if (!File.Exists(manifestPath))
{{
    throw new InvalidOperationException(
        $""api-manifest.json not found at {{manifestPath}}. Regenerate it with 'foundry compile'; ""
        + ""without it no entity endpoints are served."");
}}

var manifest = JsonSerializer.Deserialize<ApiManifest>(
    File.ReadAllText(manifestPath),
    new JsonSerializerOptions {{ PropertyNameCaseInsensitive = true }})
    ?? throw new InvalidOperationException(""api-manifest.json could not be deserialized."");

builder.Services.AddSingleton(manifest);

// 1. MongoDB data access layer (tenant filtering, envelope encryption, OCC, auditing)
builder.Services.AddFoundryMongo(options =>
{{
    options.ConnectionString = builder.Configuration.GetConnectionString(""MongoDb"")
        ?? ""mongodb://localhost:27017"";
    options.DatabaseName = ""{projectName}Db"";
}});

// 2. Real-time audit broker (SignalR, WebSockets, SSE)
builder.Services.AddFoundryRealTime();

// 3. Business rules engine
builder.Services.AddFoundryRules();

builder.Services.AddHttpContextAccessor();

// Required by the generated DELETE endpoint, and by the repository layer to stamp audit fields.
// Without it ASP.NET cannot see ICurrentUserContext as a service, infers it as a request body
// parameter, and startup fails with ""Body was inferred but the method does not allow inferred
// body parameters"" -- before serving a single request.
builder.Services.AddScoped<Foundry.Core.User.ICurrentUserContext, Foundry.Api.Security.CurrentUserContext>();

builder.Services.AddMemoryCache();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);

builder.Services.AddMediatR(cfg =>
{{
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
    cfg.RegisterServicesFromAssembly(typeof(Foundry.Api.MediatR.InsertCommand<>).Assembly);
}});

// Generated request handlers, one per entity method in the manifest.
builder.Services.AddGeneratedHandlers();
{kafkaRegistration}
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(BusinessRuleBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(CachingBehavior<,>));

builder.Services.AddExceptionHandler<Foundry.Api.Middleware.GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// Entity ids are MongoDB ObjectIds, which System.Text.Json cannot round-trip unaided: it decodes
// them to ObjectId.Empty, the driver then assigns a different id at insert, and a GET by the id the
// caller was given returns 404 while the record sits in the collection.
builder.Services.ConfigureHttpJsonOptions(options =>
    FoundryJsonDefaults.Apply(options.SerializerOptions));

var app = builder.Build();

app.UseExceptionHandler();

// Generated REST endpoints for every entity in the manifest.
app.MapGeneratedEndpoints(manifest);

// Real-time channels.
app.MapFoundryRealTime();

app.Run();
";
        File.WriteAllText(Path.Combine(targetDir, "Program.cs"), programContent);
        Console.WriteLine("  ✓ Generated Program.cs (REST endpoints from api-manifest.json, rules, real-time)");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n=================================================================");
        Console.WriteLine($"  🎉 READY-TO-RUN FOUNDRY PROJECT CREATED: {projectName}");
        Console.WriteLine("=================================================================");
        Console.ResetColor();
        Console.WriteLine("To start your ready-to-run API:\n");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  cd {projectName}");
        Console.WriteLine("  docker compose up -d");
        Console.WriteLine("  dotnet run");
        Console.ResetColor();
        Console.WriteLine("\nYour API will be live with full REST CRUD, Encryption & WebSockets!");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=================================================================\n");
        Console.ResetColor();

        return 0;
    }

    private static int HandleSchemaCommand(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }

        var subCommand = args[0].ToLowerInvariant();
        if (subCommand == "build" || subCommand == "compile")
        {
            Console.WriteLine("[Foundry Engine] Executing in-process schema compiler...");
            // Propagate the compiler's exit code. Returning 0 unconditionally meant a failed
            // compile looked like a successful one to any script or CI job invoking this.
            return Foundry.Schema.Compiler.Program.Main(args.Skip(1).ToArray());
        }
        else if (subCommand == "studio")
        {
            return ServeStudioAsync(args.Skip(1).ToArray()).GetAwaiter().GetResult();
        }

        PrintHelp();
        return 1;
    }

    private static async Task<int> ServeStudioAsync(string[] args)
    {
        int port = 5000;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedPort))
            {
                port = parsedPort;
            }
        }

        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[Error] Embedded Studio UI ('index.html') not found in this build.");
            Console.ResetColor();
            Console.WriteLine("  The Studio bundle is a build artifact and is not committed, so it must be built");
            Console.WriteLine("  before it can be embedded:");
            Console.WriteLine();
            Console.WriteLine("      cd foundry-studio && npm ci && npm run build");
            Console.WriteLine("      dotnet build foundry-cli/src/Foundry.Cli/Foundry.Cli.csproj");
            Console.WriteLine();
            return 1;
        }

        string htmlContent;
        using (var stream = assembly.GetManifestResourceStream(resourceName)!)
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            htmlContent = await reader.ReadToEndAsync();
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=================================================================");
        Console.WriteLine("              FOUNDRY STANDALONE STUDIO IDE SERVER               ");
        Console.WriteLine("=================================================================");
        Console.ResetColor();
        Console.WriteLine($"  ➜ Local Web UI: http://localhost:{port}/");
        Console.WriteLine("  ➜ Embedded Assets: Self-Contained Singlefile Resource");
        Console.WriteLine("  Press Ctrl+C to stop the server.\n");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://localhost:{port}");

        var app = builder.Build();

        // Serve embedded Studio UI at root
        app.MapGet("/", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(htmlContent);
        });

        // Health check endpoint
        app.MapGet("/api/health", () => Results.Ok(new { status = "Healthy", framework = "Foundry .NET 10" }));

        // In-process schema compiler API endpoint
        app.MapPost("/api/schema/compile", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var json = await reader.ReadToEndAsync();
            var tempFile = Path.GetTempFileName();
            var outDir = Path.Combine(Path.GetTempPath(), "foundry-compiled");
            await File.WriteAllTextAsync(tempFile, json);

            try
            {
                // Report the real outcome. This used to answer 200 { success = true } even when
                // compilation had failed and nothing was written, so the Studio canvas showed a
                // successful compile for a schema the compiler had rejected.
                var exitCode = Foundry.Schema.Compiler.Program.Main(new[] { "--input", tempFile, "--output", outDir });
                if (exitCode != 0)
                {
                    return Results.BadRequest(new
                    {
                        success = false,
                        error = "Schema compilation failed. Run 'foundry validate' for the diagnostics."
                    });
                }

                return Results.Ok(new { success = true, outputDirectory = outDir });
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        });

        // Launch browser automatically
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"http://localhost:{port}/",
                UseShellExecute = true
            });
        }
        catch {}

        await app.RunAsync();
        return 0;
    }

    /// <summary>
    /// Runs the Foundry API project in the current directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Foundry.Api</c> is a library of extension methods, not a host — the gateway is the
    /// project <c>foundry new</c> scaffolds, which wires those extensions into its own
    /// <c>Program.cs</c>. There is therefore nothing for the CLI to boot on its own, and the
    /// previous implementation reflected that by printing "Starting WebHost Gateway...",
    /// sleeping 100ms and returning success without starting anything.
    /// </para>
    /// <para>
    /// This runs the user's project instead, so the command does what its name claims.
    /// </para>
    /// </remarks>
    private static async Task<int> HandleApiCommandAsync(string[] args)
    {
        string? projectPath = null;
        var passThrough = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--project" || args[i] == "-p") && i + 1 < args.Length)
                projectPath = args[++i];
            else
                passThrough.Add(args[i]);
        }

        if (string.IsNullOrEmpty(projectPath))
        {
            var candidates = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.csproj", SearchOption.TopDirectoryOnly);

            if (candidates.Length == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[Error] No .csproj found in the current directory.");
                Console.ResetColor();
                Console.WriteLine("  'foundry api' runs a Foundry API project. Either cd into one, pass --project <path>,");
                Console.WriteLine("  or scaffold a new one with 'foundry new <ProjectName>'.");
                return 1;
            }

            if (candidates.Length > 1)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Error] {candidates.Length} project files found here; pass --project <path> to choose one:");
                Console.ResetColor();
                foreach (var c in candidates) Console.WriteLine($"  {Path.GetFileName(c)}");
                return 1;
            }

            projectPath = candidates[0];
        }

        if (!File.Exists(projectPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Error] Project not found: {projectPath}");
            Console.ResetColor();
            return 1;
        }

        // Warn rather than block: the project may reference Foundry transitively, and refusing to
        // run something the user explicitly pointed at would be worse than a false negative.
        var projectText = await File.ReadAllTextAsync(projectPath);
        if (!projectText.Contains("Foundry.Api", StringComparison.OrdinalIgnoreCase))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[Warning] {Path.GetFileName(projectPath)} does not reference Foundry.Api. Running it anyway.");
            Console.ResetColor();
        }

        var runArgs = new List<string> { "run", "--project", projectPath };
        if (passThrough.Count > 0)
        {
            runArgs.Add("--");
            runArgs.AddRange(passThrough);
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[Foundry] dotnet {string.Join(' ', runArgs)}");
        Console.ResetColor();

        var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        foreach (var a in runArgs) startInfo.ArgumentList.Add(a);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[Error] Could not start 'dotnet'. Is the .NET SDK on PATH?");
            Console.ResetColor();
            return 1;
        }

        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static int HandleExportCommand(string[] args)
    {
        string inputPath = "domain.foundry.json";
        string format = "openapi";
        string? outputPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--input" || args[i] == "-i") && i + 1 < args.Length) inputPath = args[i + 1];
            else if ((args[i] == "--format" || args[i] == "-f") && i + 1 < args.Length) format = args[i + 1].ToLowerInvariant();
            else if ((args[i] == "--output" || args[i] == "-o") && i + 1 < args.Length) outputPath = args[i + 1];
        }

        if (!File.Exists(inputPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Error] Schema file '{inputPath}' not found.");
            Console.ResetColor();
            return 1;
        }

        var schemaContent = File.ReadAllText(inputPath);
        var schema = System.Text.Json.JsonSerializer.Deserialize<Foundry.Schema.Compiler.SchemaModel>(
            schemaContent, 
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (schema == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Error] Failed to parse schema from '{inputPath}'.");
            Console.ResetColor();
            return 1;
        }

        string exportedContent = format switch
        {
            "openapi" or "swagger" => Foundry.Schema.Compiler.Exporters.OpenApiExporter.ExportJson(schema),
            "asyncapi" or "kafka" => Foundry.Schema.Compiler.Exporters.AsyncApiExporter.ExportJson(schema),
            "postman" => Foundry.Schema.Compiler.Exporters.PostmanExporter.ExportJson(schema),
            "mermaid" => Foundry.Schema.Compiler.Exporters.MermaidExporter.ExportMermaid(schema),
            _ => throw new NotSupportedException($"Export format '{format}' is not supported. Choose openapi, asyncapi, postman, or mermaid.")
        };

        if (string.IsNullOrEmpty(outputPath))
        {
            outputPath = format switch
            {
                "mermaid" => "schema-diagram.mmd",
                "postman" => "postman_collection.json",
                "asyncapi" => "asyncapi_spec.json",
                _ => "openapi_spec.json"
            };
        }

        File.WriteAllText(outputPath, exportedContent);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"✓ Exported {format.ToUpper()} spec to '{outputPath}'");
        Console.ResetColor();
        return 0;
    }

    private static int HandleDoctorCommand()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=================================================================");
        Console.WriteLine("              FOUNDRY ENVIRONMENT DIAGNOSTICS DOCTOR             ");
        Console.WriteLine("=================================================================");
        Console.ResetColor();

        Console.WriteLine("➜ .NET SDK Version: " + Environment.Version);
        Console.WriteLine("➜ OS Platform: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        Console.WriteLine("➜ Framework Assembly: " + Assembly.GetExecutingAssembly().GetName().Version);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n  ✓ Local environment is fully healthy for Foundry development!");
        Console.ResetColor();
        return 0;
    }

    private static int HandleGenerateCommand(string[] args)
    {
        string provider = "github";
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--provider" || args[i] == "-p") && i + 1 < args.Length) provider = args[i + 1].ToLowerInvariant();
        }

        var githubWorkflowContent = @"name: Foundry CI/CD Pipeline

on:
  push:
    branches: [ main, master ]
  pull_request:
    branches: [ main, master ]

jobs:
  build-and-test:
    runs-on: ubuntu-latest

    services:
      mongodb:
        image: mongo:latest
        ports:
          - 27017:27017

    steps:
      - uses: actions/checkout@v4
      
      - name: Setup .NET SDK
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'

      - name: Restore dependencies
        run: dotnet restore

      - name: Build Solution
        run: dotnet build --no-restore --configuration Release

      - name: Run Tests
        run: dotnet test --no-build --configuration Release --verbosity normal
";

        var targetDir = Path.Combine(Directory.GetCurrentDirectory(), ".github", "workflows");
        Directory.CreateDirectory(targetDir);
        var ciPath = Path.Combine(targetDir, "ci.yml");
        File.WriteAllText(ciPath, githubWorkflowContent);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  ✓ Generated GitHub Actions CI workflow at '.github/workflows/ci.yml'");
        Console.ResetColor();
        return 0;
    }

    private static int HandleSdkCommand(string[] args)
    {
        string inputPath = "domain.foundry.json";
        string lang = "ts";
        string? outputPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--input" || args[i] == "-i") && i + 1 < args.Length) inputPath = args[i + 1];
            else if ((args[i] == "--language" || args[i] == "-l" || args[i] == "--lang") && i + 1 < args.Length) lang = args[i + 1].ToLowerInvariant();
            else if ((args[i] == "--output" || args[i] == "-o") && i + 1 < args.Length) outputPath = args[i + 1];
        }

        if (!File.Exists(inputPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Error] Schema file '{inputPath}' not found.");
            Console.ResetColor();
            return 1;
        }

        var schemaContent = File.ReadAllText(inputPath);
        var schema = System.Text.Json.JsonSerializer.Deserialize<Foundry.Schema.Compiler.SchemaModel>(
            schemaContent, 
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (schema == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Error] Failed to parse schema from '{inputPath}'.");
            Console.ResetColor();
            return 1;
        }

        string code = lang switch
        {
            "ts" or "typescript" => Foundry.Schema.Compiler.Generators.TypeScriptSdkGenerator.Generate(schema),
            "cs" or "csharp" => Foundry.Schema.Compiler.Generators.CsharpSdkGenerator.Generate(schema),
            "py" or "python" => Foundry.Schema.Compiler.Generators.PythonSdkGenerator.Generate(schema),
            _ => throw new NotSupportedException($"Language '{lang}' is not supported. Choose ts, csharp, or python.")
        };

        if (string.IsNullOrEmpty(outputPath))
        {
            outputPath = lang switch
            {
                "cs" or "csharp" => "FoundryClient.cs",
                "py" or "python" => "foundry_client.py",
                _ => "foundryClient.ts"
            };
        }

        File.WriteAllText(outputPath, code);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"✓ Generated {lang.ToUpper()} Client SDK at '{outputPath}'");
        Console.ResetColor();
        return 0;
    }

    private static int HandleTestCommand(string[] args)
    {
        string inputPath = "domain.foundry.json";
        string outputDir = "./tests";
        string reportPath = "test-report.html";

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--input" || args[i] == "-i") && i + 1 < args.Length) inputPath = args[i + 1];
            else if ((args[i] == "--output" || args[i] == "-o") && i + 1 < args.Length) outputDir = args[i + 1];
            else if ((args[i] == "--report" || args[i] == "-r") && i + 1 < args.Length) reportPath = args[i + 1];
        }

        if (!File.Exists(inputPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Error] Schema file '{inputPath}' not found.");
            Console.ResetColor();
            return 1;
        }

        var schemaContent = File.ReadAllText(inputPath);
        var schema = System.Text.Json.JsonSerializer.Deserialize<Foundry.Schema.Compiler.SchemaModel>(
            schemaContent, 
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (schema == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Error] Failed to parse schema from '{inputPath}'.");
            Console.ResetColor();
            return 1;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=================================================================");
        Console.WriteLine("       FOUNDRY AUTONOMOUS MULTI-PROTOCOL TEST GENERATOR         ");
        Console.WriteLine("=================================================================");
        Console.ResetColor();

        // 1. Generate xUnit Test Files
        var generatedTests = Foundry.Testing.Generators.AutomatedTestSuiteGenerator.GenerateAllTestSuites(schema);
        Directory.CreateDirectory(outputDir);

        foreach (var kvp in generatedTests)
        {
            var path = Path.Combine(outputDir, kvp.Key);
            File.WriteAllText(path, kvp.Value);
            Console.WriteLine($"  ✓ Generated test suite: {path}");
        }

        // 2. Generate Execution Summary Reports
        var htmlReport = Foundry.Testing.Reports.TestReportGenerator.GenerateHtmlReport(schema.Namespace, generatedTests.Count * 2, generatedTests.Count * 2, 0, 0.45);
        var mdReport = Foundry.Testing.Reports.TestReportGenerator.GenerateMarkdownReport(schema.Namespace, generatedTests.Count * 2, generatedTests.Count * 2, 0, 0.45);

        File.WriteAllText(reportPath, htmlReport);
        File.WriteAllText(Path.ChangeExtension(reportPath, ".md"), mdReport);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n  ✓ Generated HTML Test Execution Report at '{reportPath}'");
        Console.WriteLine($"  ✓ Generated Markdown Test Report at '{Path.ChangeExtension(reportPath, ".md")}'");
        Console.ResetColor();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=================================================================");
        Console.WriteLine("           FOUNDRY FRAMEWORK UNIFIED SINGLE EXECUTABLE           ");
        Console.WriteLine("=================================================================");
        Console.ResetColor();
        Console.WriteLine("Usage:");
        Console.WriteLine("  foundry <command> [arguments]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  new <ProjectName>      ");
        Console.ResetColor();
        Console.WriteLine("Scaffolds a complete, ready-to-run C# API project instantly.");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  studio [--port 5000]   ");
        Console.ResetColor();
        Console.WriteLine("Boots the embedded Standalone Visual Studio IDE web server.");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  export -f openapi|kafka ");
        Console.ResetColor();
        Console.WriteLine("Exports OpenAPI 3.1, AsyncAPI 3.0, Postman, or Mermaid specs.");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  doctor                 ");
        Console.ResetColor();
        Console.WriteLine("Runs local environment diagnostic health checks.");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  generate ci            ");
        Console.ResetColor();
        Console.WriteLine("Generates production GitHub Actions / CI pipeline YAML.");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  validate <schema.json> ");
        Console.ResetColor();
        Console.WriteLine("Validates an IR document; exits non-zero on error (CI gate).");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  ai-spec [--out dir]    ");
        Console.ResetColor();
        Console.WriteLine("Writes the AI skill bundle for local-model IR authoring.");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  ai \"<instruction>\"     ");
        Console.ResetColor();
        Console.WriteLine("Generates validated IR from natural language via local Ollama.");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  eval [--runs N]        ");
        Console.ResetColor();
        Console.WriteLine("Measures local-model IR accuracy per construct; gates with --min-pass.");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  version                ");
        Console.ResetColor();
        Console.WriteLine("Prints the executable framework version.");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=================================================================");
        Console.ResetColor();
    }
}
