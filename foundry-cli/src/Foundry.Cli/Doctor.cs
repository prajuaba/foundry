using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Foundry.Cli;

/// <summary>How a single environment check turned out.</summary>
public enum CheckStatus
{
    /// <summary>Present and working, or purely informational.</summary>
    Ok,

    /// <summary>Absent, but only some commands need it.</summary>
    Warning,

    /// <summary>Absent, and the core workflow cannot run without it.</summary>
    Failed
}

/// <summary>One environment check and what it found.</summary>
/// <param name="Name">Short label, e.g. "MongoDB".</param>
/// <param name="Status">Whether this blocks work, hinders it, or is fine.</param>
/// <param name="Detail">What was actually observed — a version, an endpoint, an error.</param>
/// <param name="Remedy">What to do about it, when there is something to do.</param>
public sealed record DiagnosticCheck(string Name, CheckStatus Status, string Detail, string? Remedy = null);

/// <summary>
/// Checks that the local machine can actually do Foundry work.
/// </summary>
/// <remarks>
/// <para>
/// This command used to print the .NET version, the OS description and its own assembly version and
/// then declare "Local environment is fully healthy for Foundry development!" — unconditionally. It
/// probed nothing. It could not fail. On a machine with no .NET SDK, no Docker and no database it
/// reported perfect health, which is worse than having no doctor at all: an environment check that
/// cannot report a problem is a check that costs a user real time before they learn it told them
/// nothing.
/// </para>
/// <para>
/// Every check below observes something outside this process, and each reports what it observed
/// rather than that it ran. Only a missing .NET SDK is fatal, because that is what a scaffolded
/// project needs to build; a database or a broker is needed to *run* an application, not to model
/// one, so their absence is a warning and the command still exits 0.
/// </para>
/// </remarks>
public static class Doctor
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Runs every check. Probes are independent, so they run concurrently.</summary>
    public static async Task<IReadOnlyList<DiagnosticCheck>> RunAsync(CancellationToken ct = default)
    {
        var host = Environment.GetEnvironmentVariable("FOUNDRY_OLLAMA_HOST") ?? "http://localhost:11434";
        var (mongoHost, mongoPort) = MongoEndpoint();

        var checks = new List<DiagnosticCheck> { Platform(), StudioBundle() };

        var probes = new[]
        {
            DotnetSdkAsync(ct),
            NodeAsync(ct),
            DockerAsync(ct),
            TcpAsync("MongoDB", mongoHost, mongoPort,
                "Not required to model or compile a schema; required to run an application.",
                "docker compose up -d", ct),
            TcpAsync("Kafka", "localhost", 9092,
                "Only entities with kafkaOutboxEnabled need a broker.",
                "docker compose up -d", ct),
            OllamaAsync(host, ct)
        };

        checks.AddRange(await Task.WhenAll(probes));
        return checks;
    }

    /// <summary>Non-zero when any check failed, so `foundry doctor` can gate a setup script.</summary>
    public static int ExitCodeFor(IReadOnlyList<DiagnosticCheck> checks)
        => checks.Any(c => c.Status == CheckStatus.Failed) ? 1 : 0;

    /// <summary>Prints the checks and returns the exit code.</summary>
    public static int Render(IReadOnlyList<DiagnosticCheck> checks)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=================================================================");
        Console.WriteLine("              FOUNDRY ENVIRONMENT DIAGNOSTICS DOCTOR             ");
        Console.WriteLine("=================================================================");
        Console.ResetColor();
        Console.WriteLine();

        var width = checks.Max(c => c.Name.Length);

        foreach (var check in checks)
        {
            var (glyph, colour) = check.Status switch
            {
                CheckStatus.Ok => ("✓", ConsoleColor.Green),
                CheckStatus.Warning => ("!", ConsoleColor.Yellow),
                _ => ("✗", ConsoleColor.Red)
            };

            Console.ForegroundColor = colour;
            Console.Write($"  {glyph} ");
            Console.ResetColor();
            Console.WriteLine($"{check.Name.PadRight(width)}  {check.Detail}");

            if (!string.IsNullOrEmpty(check.Remedy))
            {
                Console.WriteLine($"    {new string(' ', width)}  {check.Remedy}");
            }
        }

        var failed = checks.Count(c => c.Status == CheckStatus.Failed);
        var warned = checks.Count(c => c.Status == CheckStatus.Warning);

        Console.WriteLine();

        if (failed > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  {failed} required item(s) missing, {warned} optional. Foundry cannot build projects here.");
            Console.ResetColor();
            return 1;
        }

        if (warned > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Ready to model, compile and generate. {warned} optional item(s) absent — see above.");
            Console.ResetColor();
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  Everything this checks for is present.");
        Console.ResetColor();
        return 0;
    }

    private static DiagnosticCheck Platform() => new(
        "Platform",
        CheckStatus.Ok,
        $"{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.ProcessArchitecture}), "
        + $"CLI {Assembly.GetExecutingAssembly().GetName().Version}, runtime {Environment.Version}");

    /// <summary>
    /// Whether this build of the CLI carries the Studio UI.
    /// </summary>
    /// <remarks>
    /// The bundle is embedded as a resource and a build without it only warns, so `foundry studio`
    /// is missing from some binaries and present in others with nothing on the outside to say which.
    /// Asking the assembly is the only reliable answer.
    /// </remarks>
    private static DiagnosticCheck StudioBundle()
    {
        var present = Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .Any(n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase));

        return present
            ? new("Studio bundle", CheckStatus.Ok, "embedded in this build; 'foundry studio' will serve it")
            : new("Studio bundle", CheckStatus.Warning,
                "not embedded in this build — 'foundry studio' will refuse to start",
                "cd foundry-studio && npm ci && npm run build, then rebuild the CLI");
    }

    private static async Task<DiagnosticCheck> DotnetSdkAsync(CancellationToken ct)
    {
        // Resolved against PATH explicitly rather than by handing "dotnet" to Process.Start.
        //
        // When this CLI is run as `dotnet foundry.dll`, Process.Start("dotnet") finds the host that
        // launched it whatever PATH says -- so the probe reported a healthy SDK on a machine where
        // `dotnet build` in a shell would fail, and no PATH could make it say otherwise. A check
        // that cannot produce a negative is the thing this whole command was rewritten to stop
        // being. PATH is also the right question: it is what the user's own shell will search.
        var executable = FindOnPath("dotnet");

        if (executable is null)
        {
            // Fatal: `foundry new` scaffolds a project and `foundry api` runs one. Both need an SDK,
            // even though the published CLI is self-contained and runs without one.
            return new("dotnet SDK", CheckStatus.Failed,
                "not found on PATH — scaffolded projects cannot be built or run",
                "Install the .NET 10 SDK: https://dotnet.microsoft.com/download");
        }

        var (ok, output) = await RunProcessAsync(executable, "--version", ct);

        return ok
            ? new("dotnet SDK", CheckStatus.Ok, output.Trim())
            : new("dotnet SDK", CheckStatus.Failed,
                $"{executable} is on PATH but 'dotnet --version' failed: {Summarise(output)}",
                "Reinstall the .NET 10 SDK: https://dotnet.microsoft.com/download");
    }

    private static async Task<DiagnosticCheck> NodeAsync(CancellationToken ct)
    {
        var executable = FindOnPath("node");
        var (ok, output) = executable is null
            ? (false, "")
            : await RunProcessAsync(executable, "--version", ct);

        return ok
            ? new("Node.js", CheckStatus.Ok, output.Trim() + " (needed to build the Studio bundle)")
            : new("Node.js", CheckStatus.Warning,
                "not found — only needed to rebuild the Studio UI or the VS Code extension",
                "Install Node 20 or later");
    }

    private static async Task<DiagnosticCheck> DockerAsync(CancellationToken ct)
    {
        var executable = FindOnPath("docker");
        var (ok, _) = executable is null
            ? (false, "")
            : await RunProcessAsync(executable, "info --format {{.ServerVersion}}", ct);

        return ok
            ? new("Docker", CheckStatus.Ok, "daemon responding")
            : new("Docker", CheckStatus.Warning,
                "not running or not installed — the compose stack cannot be started",
                "Start Docker, then: docker compose up -d");
    }

    /// <summary>The first match for <paramref name="executable"/> on PATH, or null.</summary>
    private static string? FindOnPath(string executable)
    {
        string[] names = OperatingSystem.IsWindows()
            ? [executable + ".exe", executable + ".cmd", executable + ".bat", executable]
            : [executable];

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory.Trim(), name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    /// <summary>First non-empty line of a tool's output, for a one-line report.</summary>
    private static string Summarise(string output)
    {
        var line = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        return string.IsNullOrEmpty(line) ? "no output" : line;
    }

    private static async Task<DiagnosticCheck> OllamaAsync(string host, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = ProbeTimeout };
            var body = await http.GetStringAsync($"{host.TrimEnd('/')}/api/tags", ct);

            var count = 0;
            using (var document = JsonDocument.Parse(body))
            {
                if (document.RootElement.TryGetProperty("models", out var models)
                    && models.ValueKind == JsonValueKind.Array)
                {
                    count = models.GetArrayLength();
                }
            }

            var model = Environment.GetEnvironmentVariable("FOUNDRY_OLLAMA_MODEL") ?? "qwen3-coder:30b";
            var hasModel = body.Contains(model, StringComparison.OrdinalIgnoreCase);

            return hasModel
                ? new("Ollama", CheckStatus.Ok, $"{host} — {count} model(s), including {model}")
                : new("Ollama", CheckStatus.Warning,
                    $"{host} — {count} model(s), but '{model}' is not among them",
                    $"ollama pull {model}");
        }
        catch (Exception ex)
        {
            return new("Ollama", CheckStatus.Warning,
                $"{host} not reachable ({ex.GetType().Name}) — 'foundry ai' and 'foundry eval' need it",
                "Start Ollama, or set FOUNDRY_OLLAMA_HOST");
        }
    }

    private static async Task<DiagnosticCheck> TcpAsync(
        string name, string host, int port, string note, string remedy, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);

            await client.ConnectAsync(host, port, timeout.Token);
            return new(name, CheckStatus.Ok, $"accepting connections on {host}:{port}");
        }
        catch
        {
            return new(name, CheckStatus.Warning, $"nothing listening on {host}:{port} — {note}", remedy);
        }
    }

    /// <summary>The host and port Foundry would connect to, honouring MONGODB_CONNECTION.</summary>
    private static (string Host, int Port) MongoEndpoint()
    {
        var connection = Environment.GetEnvironmentVariable("MONGODB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return ("localhost", 27017);

        var text = connection.Trim();
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) text = text[(scheme + 3)..];

        var credentials = text.LastIndexOf('@');
        if (credentials >= 0) text = text[(credentials + 1)..];

        // A seed list may name several hosts; the first is enough to tell whether a server is up.
        var end = text.IndexOfAny([',', '/', '?']);
        if (end >= 0) text = text[..end];

        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var port))
        {
            return (text[..colon], port);
        }

        return (text.Length > 0 ? text : "localhost", 27017);
    }

    private static async Task<(bool Ok, string Output)> RunProcessAsync(
        string fileName, string arguments, CancellationToken ct)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });

            if (process is null) return (false, "");

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);

            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode == 0, await stdout + await stderr);
        }
        catch (Exception ex)
        {
            // Win32Exception when the executable is not on PATH; OperationCanceledException on a
            // hung probe. Both mean the same thing here: it is not usable.
            return (false, ex.Message);
        }
    }
}
