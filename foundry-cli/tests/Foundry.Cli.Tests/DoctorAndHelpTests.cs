using Xunit;

namespace Foundry.Cli.Tests;

/// <summary>
/// Two commands that were telling users things that were not true.
/// </summary>
/// <remarks>
/// <para>
/// <c>foundry doctor</c> printed the .NET version, the OS string and its own assembly version and
/// then declared the environment "fully healthy" — unconditionally, having probed nothing. It could
/// not fail, so on a machine with no SDK and no database it said the same thing as on a working one.
/// </para>
/// <para>
/// <c>--help</c> listed twelve of the seventeen commands. <c>schema</c>, <c>api</c>, <c>sdk</c>,
/// <c>test</c> and <c>lsp</c> all worked and none were mentioned, so the compile command — the
/// centre of the whole toolchain — was discoverable only by reading the source.
/// </para>
/// </remarks>
public class DoctorAndHelpTests
{
    // Every command the CLI accepts. Written out here rather than derived from the help text,
    // because a test that reads its expectations out of the thing under test cannot fail.
    private static readonly string[] AllCommands =
    [
        "new", "schema", "validate", "migrate", "export", "sdk", "test",
        "api", "studio", "lsp", "generate", "doctor", "ai", "ai-spec", "eval", "version"
    ];

    [Fact]
    public async Task HelpListsEveryCommand()
    {
        var result = await Cli.RunAsync();

        Assert.Equal(1, result.ExitCode);

        foreach (var command in AllCommands)
        {
            Assert.True(
                result.Output.Contains($" {command} ") || result.Output.Contains($" {command}\n"),
                $"'{command}' is accepted by the CLI but is not listed in --help.\n\n{result.Output}");
        }
    }

    /// <summary>The five that were missing, named individually so a regression says which.</summary>
    [Theory]
    [InlineData("schema")]
    [InlineData("api")]
    [InlineData("sdk")]
    [InlineData("test")]
    [InlineData("lsp")]
    public async Task HelpListsTheCommandsThatUsedToBeUndocumented(string command)
    {
        var result = await Cli.RunAsync();
        Assert.Contains($" {command} ", result.Output);
    }

    [Fact]
    public async Task AnUnknownCommandIsRejectedByName()
    {
        var result = await Cli.RunAsync(null, "definitely-not-a-command");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unknown command", result.Output);
        Assert.Contains("definitely-not-a-command", result.Output);
    }

    [Fact]
    public async Task DoctorNamesEverySubsystemItChecks()
    {
        var result = await Cli.RunAsync(null, "doctor");

        foreach (var subsystem in new[] { "dotnet SDK", "MongoDB", "Kafka", "Docker", "Node.js", "Ollama", "Studio bundle" })
        {
            Assert.Contains(subsystem, result.Output);
        }
    }

    /// <summary>
    /// Doctor reports what it observed, not a fixed string.
    /// </summary>
    /// <remarks>
    /// Pointed at a port nothing listens on, it has to say so. This is the check that would have
    /// failed against the old implementation, which printed the same cheerful summary regardless.
    /// </remarks>
    [Fact]
    public async Task DoctorReportsAnUnreachableDependency()
    {
        var result = await Cli.RunWithEnvironmentAsync(
            new Dictionary<string, string?> { ["FOUNDRY_OLLAMA_HOST"] = "http://127.0.0.1:9" },
            clearPath: false,
            "doctor");

        Assert.Contains("Ollama", result.Output);
        Assert.Contains("not reachable", result.Output);

        // Absent infrastructure is not fatal: a schema can still be modelled, validated and compiled.
        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>
    /// Doctor fails when a required prerequisite is missing.
    /// </summary>
    /// <remarks>
    /// The whole point of the command. With no <c>dotnet</c> on PATH a scaffolded project cannot be
    /// built or run, and saying so with a non-zero exit is what lets `foundry doctor` gate a setup
    /// script. The CLI host is still launched by absolute path, so this tests the probe rather than
    /// the ability to start.
    /// </remarks>
    [Fact]
    public async Task DoctorFailsWhenTheDotnetSdkIsMissing()
    {
        var result = await Cli.RunWithEnvironmentAsync(
            new Dictionary<string, string?>(),
            clearPath: true,
            "doctor");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("dotnet SDK", result.Output);
        Assert.Contains("not found on PATH", result.Output);
        Assert.Contains("required item(s) missing", result.Output);
    }

    [Fact]
    public async Task DoctorNeverClaimsHealthItHasNotChecked()
    {
        var result = await Cli.RunWithEnvironmentAsync(
            new Dictionary<string, string?>(),
            clearPath: true,
            "doctor");

        // The exact sentence the old implementation printed on every machine, healthy or not.
        Assert.DoesNotContain("fully healthy", result.Output);
    }
}
