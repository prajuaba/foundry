using System.Diagnostics;
using System.Text;

namespace Foundry.Cli.Tests;

/// <summary>
/// Locates the built CLI and runs it as a process.
/// </summary>
/// <remarks>
/// The CLI's contract is its exit code and its stdio, not its object model: CI treats a non-zero
/// exit as a failed gate, and the VS Code extension speaks LSP over stdin/stdout. Calling into the
/// assembly would test neither, so these tests invoke the real binary.
/// <para>
/// If the CLI is not built, this fails with an explicit message rather than skipping. A gate that
/// silently skips when its subject is missing is indistinguishable from a passing one, which is the
/// failure mode this codebase has most of.
/// </para>
/// </remarks>
public static class Cli
{
    private static readonly Lazy<string> AssemblyPath = new(Locate);

    private static string Locate()
    {
        // The test assembly sits at .../foundry-cli/tests/Foundry.Cli.Tests/bin/<Config>/net10.0/,
        // so the configuration is read from its own path rather than assumed.
        var baseDirectory = AppContext.BaseDirectory;
        var configuration = baseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}")
            ? "Release"
            : "Debug";

        var repositoryRoot = FindRepositoryRoot()
            ?? throw new InvalidOperationException(
                $"Could not locate the Foundry repository root from {baseDirectory}.");

        var candidate = Path.Combine(
            repositoryRoot, "foundry-cli", "src", "Foundry.Cli", "bin", configuration, "net10.0", "foundry.dll");

        if (!File.Exists(candidate))
        {
            throw new InvalidOperationException(
                $"The CLI is not built at {candidate}. Build the solution first: "
                + "dotnet build Foundry.slnx");
        }

        return candidate;
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "foundry-cli"))
                && Directory.Exists(Path.Combine(directory.FullName, "foundry-schema")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        return null;
    }

    /// <summary>The result of one CLI invocation.</summary>
    public sealed record Result(int ExitCode, string StdOut, string StdErr)
    {
        /// <summary>Both streams together, for assertions that do not care which one carried a message.</summary>
        public string Output => StdOut + StdErr;
    }

    /// <summary>Runs the CLI with the given arguments and waits for it to exit.</summary>
    public static async Task<Result> RunAsync(string? workingDirectory = null, params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add(AssemblyPath.Value);
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start 'dotnet'.");

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);

        return new Result(process.ExitCode, await stdOutTask, await stdErrTask);
    }

    /// <summary>
    /// Runs the CLI writing <paramref name="stdinBytes"/> to its standard input, and returns the raw
    /// bytes it wrote to standard output.
    /// </summary>
    /// <remarks>
    /// Bytes rather than text throughout. The LSP base protocol frames messages by byte count, and
    /// the defect this exists to guard against was precisely a byte/char confusion — reading the
    /// response as a string would hide it.
    /// </remarks>
    public static async Task<byte[]> RunWithStdinAsync(byte[] stdinBytes, params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add(AssemblyPath.Value);
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start 'dotnet'.");

        var stdOut = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(stdOut);

        await process.StandardInput.BaseStream.WriteAsync(stdinBytes);
        await process.StandardInput.BaseStream.FlushAsync();
        process.StandardInput.Close();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        await copyTask;

        return stdOut.ToArray();
    }

    /// <summary>Builds an LSP frame: byte-accurate Content-Length header plus body.</summary>
    public static byte[] LspFrame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        var frame = new byte[header.Length + body.Length];
        header.CopyTo(frame, 0);
        body.CopyTo(frame, header.Length);
        return frame;
    }
}
