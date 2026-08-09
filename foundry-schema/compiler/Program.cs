using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Foundry.Schema.Compiler
{
    public class Program
    {
        /// <summary>
        /// Compiles a schema into C# and returns a process exit code.
        /// </summary>
        /// <remarks>
        /// Returns <c>int</c> rather than <c>void</c> so in-process callers such as
        /// <c>foundry new</c> can tell whether compilation actually succeeded. It previously set
        /// <see cref="Environment.ExitCode"/> and returned void, which the standalone process
        /// honoured but an in-process caller could not observe -- so the scaffolder continued
        /// happily after a failed compile and reported a ready-to-run project.
        /// </remarks>
        public static int Main(string[] args)
        {
            string? inputPath = null;
            string? outputPath = null;
            string? manifestPath = null;
            bool prune = true;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--no-prune")
                {
                    prune = false;
                    continue;
                }

                if (args[i] == "--input" || args[i] == "-i")
                {
                    if (i + 1 < args.Length)
                        inputPath = args[++i];
                    else
                    {
                        Console.WriteLine("Error: --input requires a file path.");
                        PrintUsage();
                        return 1;
                    }
                }
                else if (args[i] == "--output" || args[i] == "-o")
                {
                    if (i + 1 < args.Length)
                        outputPath = args[++i];
                    else
                    {
                        Console.WriteLine("Error: --output requires a directory path.");
                        PrintUsage();
                        return 1;
                    }
                }
                else if (args[i] == "--manifest" || args[i] == "-m")
                {
                    if (i + 1 < args.Length)
                        manifestPath = args[++i];
                    else
                    {
                        Console.WriteLine("Error: --manifest requires a file path.");
                        PrintUsage();
                        return 1;
                    }
                }
            }

            if (string.IsNullOrEmpty(inputPath) || string.IsNullOrEmpty(outputPath))
            {
                Console.WriteLine("Error: Both --input and --output are required.");
                PrintUsage();
                return 1;
            }

            if (!File.Exists(inputPath))
            {
                Console.WriteLine($"Error: Input file '{inputPath}' does not exist.");
                return 1;
            }

            try
            {
                var json = File.ReadAllText(inputPath);

                // Catch problems that survive deserialisation. A Studio canvas file has no
                // 'entities' property, so it deserialises into a structurally valid but empty
                // model — the compiler used to report success while emitting nothing at all.
                var bag = new DiagnosticBag();
                SchemaValidator.ValidateRawDocument(json, bag);

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var schema = JsonSerializer.Deserialize<SchemaModel>(json, options);

                bag.AddRange(SchemaValidator.Validate(schema).Items);

                if (bag.Items.Count > 0)
                {
                    Console.WriteLine(bag.Render());
                    Console.WriteLine();
                }

                if (bag.HasErrors)
                {
                    Console.WriteLine($"Error: schema validation failed with {bag.ErrorCount} error(s). No files were written.");
                    return 1;
                }

                var generatedFiles = PocoGenerator.GenerateFiles(schema!);

                Directory.CreateDirectory(outputPath);

                int written = 0, preserved = 0;

                foreach (var file in generatedFiles)
                {
                    var filePath = Path.Combine(outputPath, $"{file.Path}.cs");
                    var parentDir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }

                    // Scaffolds hold hand-written business logic. Writing one that already
                    // exists would destroy the developer's work, so they are written once
                    // and then left alone for the lifetime of the project.
                    if (file.Kind == EmitKind.Scaffold && File.Exists(filePath))
                    {
                        preserved++;
                        Console.WriteLine($"Preserved: {filePath} (scaffold already exists)");
                        continue;
                    }

                    File.WriteAllText(filePath, file.Content);
                    written++;
                    Console.WriteLine($"{(file.Kind == EmitKind.Scaffold ? "Scaffolded" : "Generated")}: {filePath}");
                }

                var removed = prune
                    ? PruneOrphans(outputPath!, generatedFiles)
                    : 0;

                // api-manifest.json is what makes a compiled domain serve anything: the REST surface
                // is emitted by the Foundry.Api.SourceGenerators analyser from this file, and with no
                // manifest the analyser emits empty registrations and every entity route answers 404.
                //
                // It used to be written only by `foundry new`, so compiling a schema into an existing
                // project -- the documented way to do exactly that -- produced entities, handlers,
                // rules and Kafka consumers, and an application with no API. Two paths that must
                // agree, one of which silently omitted a piece; the fix is that there is now one.
                var manifestTarget = manifestPath ?? Path.Combine(outputPath, "api-manifest.json");
                var manifestDir = Path.GetDirectoryName(manifestTarget);
                if (!string.IsNullOrEmpty(manifestDir)) Directory.CreateDirectory(manifestDir);
                File.WriteAllText(manifestTarget, ApiManifestGenerator.Generate(schema!));
                Console.WriteLine($"Generated: {manifestTarget}");
                written++;

                var warnings = bag.WarningCount > 0 ? $", {bag.WarningCount} warning(s)" : "";
                var pruned = removed > 0 ? $", {removed} orphan(s) removed" : "";
                Console.WriteLine($"Success: {written} file(s) written, {preserved} scaffold(s) preserved{pruned}{warnings}.");
                return 0;
            }
            catch (UnsafeSchemaValueException ex)
            {
                // Validation should have caught this. Reaching here means a gap in the
                // validator, so fail loudly rather than emit the value.
                Console.WriteLine($"Error: refusing to emit unsafe schema value. {ex.Message}");
                return 1;
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"Error: Failed to deserialize JSON. {ex.Message}");
                return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: An unexpected error occurred. {ex.Message}");
                return 1;
            }
        }

        /// <summary>
        /// Deletes generated files under <paramref name="outputPath"/> that this compile did not
        /// emit, and any directory left empty by doing so. Returns how many files were removed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Without this, removing a construct from a schema left its C# behind, still compiling and
        /// still wired up: a deleted entity kept its record, a renamed one had two, and moving the
        /// emit layout under a folder left the whole previous layout in place. The compiler already
        /// knew what it emits -- a test asserts the showcase has no orphans -- and only the person
        /// running it was expected to act on the difference.
        /// </para>
        /// <para>
        /// What it will delete is deliberately narrow: a file must carry the
        /// <c>// &lt;auto-generated/&gt;</c> marker this compiler writes. That marker is the whole
        /// safety argument, because a file carrying it is reproducible by definition -- anything
        /// deleted here comes back on the next compile if the schema still calls for it.
        /// </para>
        /// <para>
        /// Three things are therefore never touched, and each would be someone's lost work:
        /// <b>scaffolds</b>, which carry the <c>&lt;foundry-scaffold/&gt;</c> marker instead and hold
        /// the business logic a schema cannot state; <b><c>*.Custom.cs</c></b>, the documented place
        /// for hand-written partials, which the header itself points developers to; and any file
        /// with neither marker, which this compiler did not write and has no business removing.
        /// </para>
        /// </remarks>
        static int PruneOrphans(string outputPath, IReadOnlyList<GeneratedFile> generatedFiles)
        {
            if (!Directory.Exists(outputPath)) return 0;

            var emitted = generatedFiles
                .Select(f => Path.GetFullPath(Path.Combine(outputPath, $"{f.Path}.cs")))
                .ToHashSet(StringComparer.Ordinal);

            var removed = 0;

            foreach (var path in Directory.GetFiles(outputPath, "*.cs", SearchOption.AllDirectories))
            {
                var full = Path.GetFullPath(path);
                if (emitted.Contains(full)) continue;

                if (full.EndsWith(".Custom.cs", StringComparison.OrdinalIgnoreCase)) continue;

                // Read only the head: enough to see a marker, and it keeps a stray large file in
                // the output directory from being loaded in full just to be left alone.
                string head;
                try
                {
                    using var reader = new StreamReader(full);
                    var buffer = new char[256];
                    head = new string(buffer, 0, reader.Read(buffer, 0, buffer.Length));
                }
                catch (IOException)
                {
                    continue;
                }

                if (!head.StartsWith("// <auto-generated/>", StringComparison.Ordinal)) continue;

                try
                {
                    File.Delete(full);
                    removed++;
                    Console.WriteLine($"Removed: {path} (no longer emitted by the schema)");
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"Warning: could not remove orphaned '{path}'. {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    Console.WriteLine($"Warning: could not remove orphaned '{path}'. {ex.Message}");
                }
            }

            if (removed > 0) RemoveEmptyDirectories(outputPath);

            return removed;
        }

        /// <summary>
        /// Removes directories under <paramref name="root"/> that pruning left with nothing in
        /// them, deepest first. <paramref name="root"/> itself is kept.
        /// </summary>
        static void RemoveEmptyDirectories(string root)
        {
            foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
                }
                catch (IOException)
                {
                    // A directory that will not go is not worth failing a compile over.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("Usage: Foundry.Schema.Compiler --input <schema.json> --output <directory> [--manifest <file>]");
            Console.WriteLine("  --input, -i     : Path to the JSON schema file");
            Console.WriteLine("  --output, -o    : Output directory path");
            Console.WriteLine("  --manifest, -m  : Where to write api-manifest.json (default: <output>/api-manifest.json)");
            Console.WriteLine("  --no-prune      : Keep generated files the schema no longer emits (default: remove them)");
        }
    }
}