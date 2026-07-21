using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Foundry.Cli;

class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        var workspace = FindWorkspace();
        var dotnetPath = "/Users/prajuab/.dotnet/dotnet";

        switch (args[0].ToLowerInvariant())
        {
            case "schema":
                if (args.Length < 2)
                {
                    PrintHelp();
                    return 1;
                }
                if (args[1].Equals("build", StringComparison.OrdinalIgnoreCase))
                {
                    var compilerProj = Path.Combine(workspace, "foundry-schema", "compiler", "Foundry.Schema.Compiler.csproj");
                    var extraArgs = string.Join(" ", args.Skip(2));
                    Console.WriteLine($"[Foundry CLI] Building schema using compiler: {compilerProj}...");
                    return RunProcess(dotnetPath, $"run --project \"{compilerProj}\" -- {extraArgs}", workspace);
                }
                else if (args[1].Equals("studio", StringComparison.OrdinalIgnoreCase))
                {
                    var scriptPath = Path.Combine(workspace, "foundry-schema", "start-studio.sh");
                    Console.WriteLine($"[Foundry CLI] Starting studio IDE via script: {scriptPath}...");
                    return RunProcess("bash", $"\"{scriptPath}\"", Path.Combine(workspace, "foundry-schema"));
                }
                else
                {
                    PrintHelp();
                    return 1;
                }

            case "api":
                if (args.Length < 2 || !args[1].Equals("start", StringComparison.OrdinalIgnoreCase))
                {
                    PrintHelp();
                    return 1;
                }
                var apiProj = Path.Combine(workspace, "foundry-api", "samples", "Foundry.Api.Sample", "Foundry.Api.Sample.csproj");
                Console.WriteLine($"[Foundry CLI] Starting backend service: {apiProj}...");
                return RunProcess(dotnetPath, $"run --project \"{apiProj}\"", workspace);

            case "test":
                Console.WriteLine("[Foundry CLI] Running test suites...");
                
                var testProjects = new[]
                {
                    Path.Combine(workspace, "foundry-mongo", "tests", "FoundryMongo.Tests", "FoundryMongo.Tests.csproj"),
                    Path.Combine(workspace, "foundry-api", "tests", "Foundry.Api.Tests", "Foundry.Api.Tests.csproj"),
                    Path.Combine(workspace, "foundry-schema", "tests", "Foundry.Schema.Compiler.Tests", "Foundry.Schema.Compiler.Tests.csproj"),
                    Path.Combine(workspace, "foundry-integration-tests", "Foundry.IntegrationTests.csproj")
                };

                int overallExitCode = 0;
                foreach (var proj in testProjects)
                {
                    if (File.Exists(proj))
                    {
                        Console.WriteLine($"\n[Foundry CLI] Executing tests for: {Path.GetFileName(proj)}...");
                        var exitCode = RunProcess(dotnetPath, $"test \"{proj}\"", workspace);
                        if (exitCode != 0)
                        {
                            overallExitCode = exitCode;
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[Warning] Test project not found: {proj}");
                    }
                }
                return overallExitCode;

            default:
                PrintHelp();
                return 1;
        }
    }

    static string FindWorkspace()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, "foundry-schema")) && Directory.Exists(Path.Combine(dir, "foundry-api")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        return "/Users/prajuab/Workspace/foundry";
    }

    static int RunProcess(string filename, string arguments, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = filename,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null) return -1;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Error] Failed to execute process: {ex.Message}");
            return -1;
        }
    }

    static void PrintHelp()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=================================================================");
        Console.WriteLine("                  FOUNDRY UNIFIED DEVELOPER CLI                  ");
        Console.WriteLine("=================================================================");
        Console.ResetColor();
        Console.WriteLine("Usage:");
        Console.WriteLine("  foundry <command> [sub-command] [arguments]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  schema build [args]   ");
        Console.ResetColor();
        Console.WriteLine("Runs the schema compiler to generate POCO classes.");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  schema studio         ");
        Console.ResetColor();
        Console.WriteLine("Starts the React schema editor frontend + .NET backend.");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  api start             ");
        Console.ResetColor();
        Console.WriteLine("Launches the dynamic backend service host.");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  test                  ");
        Console.ResetColor();
        Console.WriteLine("Runs all project unit and integration test suites.");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=================================================================");
        Console.ResetColor();
    }
}
