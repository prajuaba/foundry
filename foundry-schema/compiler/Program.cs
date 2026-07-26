using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Foundry.Schema.Compiler
{
    public class Program
    {
        public static void Main(string[] args)
        {
            string? inputPath = null;
            string? outputPath = null;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--input" || args[i] == "-i")
                {
                    if (i + 1 < args.Length)
                        inputPath = args[++i];
                    else
                    {
                        Console.WriteLine("Error: --input requires a file path.");
                        PrintUsage();
                        return;
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
                        return;
                    }
                }
            }

            if (string.IsNullOrEmpty(inputPath) || string.IsNullOrEmpty(outputPath))
            {
                Console.WriteLine("Error: Both --input and --output are required.");
                PrintUsage();
                return;
            }

            if (!File.Exists(inputPath))
            {
                Console.WriteLine($"Error: Input file '{inputPath}' does not exist.");
                return;
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
                    Environment.ExitCode = 1;
                    return;
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

                var warnings = bag.WarningCount > 0 ? $", {bag.WarningCount} warning(s)" : "";
                Console.WriteLine($"Success: {written} file(s) written, {preserved} scaffold(s) preserved{warnings}.");
            }
            catch (UnsafeSchemaValueException ex)
            {
                // Validation should have caught this. Reaching here means a gap in the
                // validator, so fail loudly rather than emit the value.
                Console.WriteLine($"Error: refusing to emit unsafe schema value. {ex.Message}");
                Environment.ExitCode = 1;
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"Error: Failed to deserialize JSON. {ex.Message}");
                Environment.ExitCode = 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: An unexpected error occurred. {ex.Message}");
                Environment.ExitCode = 1;
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("Usage: Foundry.Schema.Compiler --input <schema.json> --output <directory>");
            Console.WriteLine("  --input, -i     : Path to the JSON schema file");
            Console.WriteLine("  --output, -o    : Output directory path");
        }
    }
}