using Xunit;

namespace Foundry.Cli.Tests;

/// <summary>
/// Tests for the 'foundry verify' command.
/// </summary>
/// <remarks>
/// The verify command gates CI pipelines by detecting divergences between an IR schema
/// and a committed api-manifest.json. This test suite validates:
/// 1. Correct exit codes for all scenarios (0, 1, 2)
/// 2. Proper error messages when required arguments are missing
/// 3. Correct behavior with --strict flag
/// 4. Proper output formatting
/// </remarks>
public class VerifyCommandTests
{
    /// <summary>
    /// Locates the repository root by walking up from the test binary directory
    /// until it finds Foundry.slnx. Fails loudly if not found (unlike ShowcaseCoverageTests.cs:41,
    /// which silently returns null).
    /// </summary>
    /// <remarks>
    /// Pattern derived from ShowcaseCoverageTests.cs:41. Unlike that implementation,
    /// we do not silently skip when the root cannot be found, because these tests
    /// verify exit codes for a CI gate. Silent skips would ship untested code.
    /// </remarks>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Foundry.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate repository root (Foundry.slnx not found walking up from " +
            $"{AppContext.BaseDirectory}). Tests require being run from within the repository.");
    }

    private static string ShowcaseSchemaPath(string root)
        => Path.Combine(root, "samples", "Foundry.E2E.Showcase", "e2e-schema.ir.json");

    private static string ShowcaseManifestPath(string root)
        => Path.Combine(root, "samples", "Foundry.E2E.Showcase", "api-manifest.json");
    /// <summary>
    /// Test 1: Matching IR and manifest → exit 0, summary names the counts.
    /// </summary>
    [Fact]
    public async Task VerifyWithMatchingIrAndManifest_ReturnsZero()
    {
        var root = RepositoryRoot();
        // Use the showcase which has matching IR and manifest
        var irPath = ShowcaseSchemaPath(root);
        var manifestPath = ShowcaseManifestPath(root);

        var result = await Cli.RunAsync(null, "verify", "-i", irPath, "-m", manifestPath);

        Assert.Equal(0, result.ExitCode);
        // Was "No enforcement gaps", which this check cannot establish: it compares two files the
        // same compiler wrote and never observes the running system. Enforcement is --enforcement.
        Assert.Contains("Manifest matches the IR", result.Output);
        Assert.Contains("CRUD endpoint", result.Output);
        Assert.Contains("custom endpoint", result.Output);
    }

    /// <summary>
    /// Test 2: Manifest with an emptied `Roles` array → exit 1, finding names entity.
    /// </summary>
    [Fact]
    public async Task VerifyWithEmptiedRolesOnEntity_ReturnsOne()
    {
        var root = RepositoryRoot();
        var irPath = ShowcaseSchemaPath(root);
        var originalManifestPath = ShowcaseManifestPath(root);

        // Copy manifest to /tmp and modify it
        var tempManifestPath = Path.Combine(Path.GetTempPath(), $"manifest_{Guid.NewGuid()}.json");
        File.Copy(originalManifestPath, tempManifestPath, overwrite: true);

        try
        {
            // Read and modify manifest - remove POST roles from Customer entity
            var manifestContent = File.ReadAllText(tempManifestPath);

            // Replace the POST roles array with an empty one for Customer entity
            manifestContent = manifestContent.Replace(
                "        \"POST\": [\n          \"Admin\"\n        ],",
                "        \"POST\": [],");

            File.WriteAllText(tempManifestPath, manifestContent);

            var result = await Cli.RunAsync(null, "verify", "-i", irPath, "-m", tempManifestPath);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("enforcement", result.Output.ToLower());
            Assert.Contains("customer", result.Output.ToLower());
        }
        finally
        {
            if (File.Exists(tempManifestPath))
                File.Delete(tempManifestPath);
        }
    }

    /// <summary>
    /// Test 3: Missing `-m` → exit **2**, message says enforcement was not verified.
    /// </summary>
    [Fact]
    public async Task VerifyWithoutManifestArgument_ReturnsTwo()
    {
        var root = RepositoryRoot();
        var irPath = ShowcaseSchemaPath(root);

        var result = await Cli.RunAsync(null, "verify", "-i", irPath);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("-m is required for the manifest check", result.Output.ToLower());
    }

    /// <summary>
    /// Test 4: `-m` pointing at a nonexistent file → exit 2.
    /// </summary>
    [Fact]
    public async Task VerifyWithNonexistentManifestFile_ReturnsTwo()
    {
        var root = RepositoryRoot();
        var irPath = ShowcaseSchemaPath(root);
        var fakePath = "/tmp/nonexistent_manifest_xyz.json";

        var result = await Cli.RunAsync(null, "verify", "-i", irPath, "-m", fakePath);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("not found", result.Output.ToLower());
    }

    /// <summary>
    /// Test 5: A manifest whose only difference is a business rule → exit **0** by default, exit **1** with `--strict`.
    /// </summary>
    [Fact]
    public async Task VerifyWithBusinessRuleDifference_ExitZeroByDefault()
    {
        var root = RepositoryRoot();
        var irPath = ShowcaseSchemaPath(root);
        var originalManifestPath = ShowcaseManifestPath(root);

        var tempManifestPath = Path.Combine(Path.GetTempPath(), $"manifest_{Guid.NewGuid()}.json");
        File.Copy(originalManifestPath, tempManifestPath, overwrite: true);

        try
        {
            // Modify only business rules (documentation inconsistency, not enforcement gap)
            var manifestContent = File.ReadAllText(tempManifestPath);
            // Add a business rule that isn't in the IR
            manifestContent = manifestContent.Replace(
                "\"BusinessRules\": {}",
                "\"BusinessRules\": { \"POST\": [\"ExtraRule\"] }");

            File.WriteAllText(tempManifestPath, manifestContent);

            // Default (non-strict) should pass
            var resultDefault = await Cli.RunAsync(null, "verify", "-i", irPath, "-m", tempManifestPath);
            Assert.Equal(0, resultDefault.ExitCode);

            // Strict mode should fail
            var resultStrict = await Cli.RunAsync(null, "verify", "-i", irPath, "-m", tempManifestPath, "--strict");
            Assert.Equal(1, resultStrict.ExitCode);
        }
        finally
        {
            if (File.Exists(tempManifestPath))
                File.Delete(tempManifestPath);
        }
    }

    /// <summary>
    /// Test 6: An unparseable manifest → exit 2.
    /// </summary>
    [Fact]
    public async Task VerifyWithUnparseableManifest_ReturnsTwo()
    {
        var root = RepositoryRoot();
        var irPath = ShowcaseSchemaPath(root);
        var tempManifestPath = Path.Combine(Path.GetTempPath(), $"manifest_{Guid.NewGuid()}.json");

        try
        {
            // Write invalid JSON
            File.WriteAllText(tempManifestPath, "{ this is not valid json }");

            var result = await Cli.RunAsync(null, "verify", "-i", irPath, "-m", tempManifestPath);

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("malformed", result.Output.ToLower());
        }
        finally
        {
            if (File.Exists(tempManifestPath))
                File.Delete(tempManifestPath);
        }
    }

    /// <summary>
    /// Missing `-i` argument → exit 2.
    /// </summary>
    [Fact]
    public async Task VerifyWithoutIrArgument_ReturnsTwo()
    {
        var root = RepositoryRoot();
        var manifestPath = ShowcaseManifestPath(root);

        var result = await Cli.RunAsync(null, "verify", "-m", manifestPath);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("-i is required", result.Output.ToLower());
    }

    /// <summary>
    /// `-i` pointing at a nonexistent file → exit 2.
    /// </summary>
    [Fact]
    public async Task VerifyWithNonexistentIrFile_ReturnsTwo()
    {
        var root = RepositoryRoot();
        var fakeIr = "/tmp/nonexistent_schema_xyz.json";
        var manifestPath = ShowcaseManifestPath(root);

        var result = await Cli.RunAsync(null, "verify", "-i", fakeIr, "-m", manifestPath);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("not found", result.Output.ToLower());
    }

    /// <summary>
    /// Test help text includes the verify command.
    /// </summary>
    [Fact]
    public async Task HelpIncludesVerifyCommand()
    {
        var result = await Cli.RunAsync();
        Assert.Contains("verify", result.Output.ToLower());
    }
}
