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
    /// Test 1: Matching IR and manifest → exit 0, summary names the counts.
    /// </summary>
    [Fact]
    public async Task VerifyWithMatchingIrAndManifest_ReturnsZero()
    {
        // Use the showcase which has matching IR and manifest
        var irPath = "/home/neo/Workspace/foundry/samples/Foundry.E2E.Showcase/e2e-schema.ir.json";
        var manifestPath = "/home/neo/Workspace/foundry/samples/Foundry.E2E.Showcase/api-manifest.json";

        var result = await Cli.RunAsync(null, "verify", "-i", irPath, "-m", manifestPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("No enforcement gaps", result.Output);
        Assert.Contains("CRUD endpoint", result.Output);
        Assert.Contains("custom endpoint", result.Output);
    }

    /// <summary>
    /// Test 2: Manifest with an emptied `Roles` array → exit 1, finding names entity.
    /// </summary>
    [Fact]
    public async Task VerifyWithEmptiedRolesOnEntity_ReturnsOne()
    {
        var irPath = "/home/neo/Workspace/foundry/samples/Foundry.E2E.Showcase/e2e-schema.ir.json";
        var originalManifestPath = "/home/neo/Workspace/foundry/samples/Foundry.E2E.Showcase/api-manifest.json";

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
        var irPath = "/home/neo/Workspace/foundry/samples/Foundry.E2E.Showcase/e2e-schema.ir.json";

        var result = await Cli.RunAsync(null, "verify", "-i", irPath);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("enforcement cannot be verified", result.Output.ToLower());
    }

    /// <summary>
    /// Test 4: `-m` pointing at a nonexistent file → exit 2.
    /// </summary>
    [Fact]
    public async Task VerifyWithNonexistentManifestFile_ReturnsTwo()
    {
        var irPath = "/home/neo/Workspace/foundry/samples/Foundry.E2E.Showcase/e2e-schema.ir.json";
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
        var irPath = "/home/neo/Workspace/foundry/samples/Foundry.E2E.Showcase/e2e-schema.ir.json";
        var originalManifestPath = "/home/neo/Workspace/foundry/samples/Foundry.E2E.Showcase/api-manifest.json";

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
        var irPath = "/home/neo/Workspace/foundry/samples/Foundry.E2E.Showcase/e2e-schema.ir.json";
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
        var manifestPath = "/home/neo/Workspace/foundry/samples/Foundry.E2E.Showcase/api-manifest.json";

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
        var fakeIr = "/tmp/nonexistent_schema_xyz.json";
        var manifestPath = "/home/neo/Workspace/foundry/samples/Foundry.E2E.Showcase/api-manifest.json";

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
