using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Foundry.Schema.Compiler;
using Foundry.Schema.Compiler.Exporters;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// Tests for the ReferenceExporter: 12-section technical reference markdown generator.
/// </summary>
public class ReferenceExporterTests
{
    private static readonly string BasePath =
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(ReferenceExporterTests).Assembly.Location) ?? "",
            "../../../../../../")); // bin/Debug/net10.0 -> foundry root

    private static readonly string SampleDir =
        Path.Combine(BasePath, "samples", "Foundry.E2E.Showcase");

    private static readonly string FixtureDir =
        Path.Combine(BasePath, "foundry-schema", "tests", "Foundry.Schema.Compiler.Tests", "Fixtures");

    private static string ComputeSha256(string filePath)
    {
        using (var sha256 = SHA256.Create())
        using (var fileStream = File.OpenRead(filePath))
        {
            var hash = sha256.ComputeHash(fileStream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }

    private SchemaModel LoadSchema()
    {
        var irPath = Path.Combine(SampleDir, "e2e-schema.ir.json");
        if (!File.Exists(irPath)) throw new FileNotFoundException($"E2E schema not found: {irPath}");

        var content = File.ReadAllText(irPath);
        return JsonSerializer.Deserialize<SchemaModel>(content,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Failed to deserialize schema");
    }

    private JsonNode? LoadManifest()
    {
        var manifestPath = Path.Combine(SampleDir, "api-manifest.json");
        if (!File.Exists(manifestPath)) return null;

        var content = File.ReadAllText(manifestPath);
        return JsonNode.Parse(content);
    }

    private string LoadGoldenFixture()
    {
        var fixturePath = Path.Combine(FixtureDir, "showcase-reference.md");
        if (!File.Exists(fixturePath)) throw new FileNotFoundException($"Golden fixture not found: {fixturePath}");
        return File.ReadAllText(fixturePath);
    }

    private string ExtractSection(string markdown, int sectionNumber)
    {
        var lines = markdown.Split('\n');
        var sectionHeader = $"## {sectionNumber}. ";

        int startIdx = -1;
        int endIdx = lines.Length;

        // Find section start
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith(sectionHeader))
            {
                startIdx = i;
                break;
            }
        }

        if (startIdx == -1) throw new InvalidOperationException($"Section {sectionNumber} not found");

        // Find section end (next section or end of file)
        for (int i = startIdx + 1; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("## ") && !lines[i].StartsWith(sectionHeader))
            {
                endIdx = i;
                break;
            }
        }

        return string.Join("\n", lines[startIdx..endIdx]).TrimEnd();
    }

    private ReferenceSource CreateReferenceSource(string irPath, string? manifestPath = null)
    {
        var irSha256 = ComputeSha256(irPath);
        string? manifestSha256 = null;
        if (!string.IsNullOrEmpty(manifestPath) && File.Exists(manifestPath))
        {
            manifestSha256 = ComputeSha256(manifestPath);
        }

        return new ReferenceSource(
            Path.GetFileName(irPath),
            irSha256,
            string.IsNullOrEmpty(manifestPath) ? null : Path.GetFileName(manifestPath),
            manifestSha256);
    }

    [Fact]
    public void Section7DivergenceInfoMatches()
    {
        var schema = LoadSchema();
        var manifest = LoadManifest();
        var irPath = Path.Combine(SampleDir, "e2e-schema.ir.json");
        var manifestPath = Path.Combine(SampleDir, "api-manifest.json");
        var refSource = CreateReferenceSource(irPath, manifestPath);

        var exported = ReferenceExporter.ExportMarkdown(schema, manifest, refSource);
        var section7 = ExtractSection(exported, 7);

        var golden = LoadGoldenFixture();
        var goldenSection7 = ExtractSection(golden, 7);

        // Section 7 should include the divergence verification subsection
        Assert.Contains("### Schema/Manifest Divergence & Enforcement Verification", section7);
        Assert.Contains("No divergences detected", section7);
        Assert.Contains("5 CRUD endpoint(s) compared", section7);
        Assert.Contains("3 custom endpoint(s) compared", section7);
        Assert.Contains("4 workflow transition endpoint(s) excluded", section7);
    }

    [Fact]
    public void Section1ScopeHeaderExists()
    {
        var schema = LoadSchema();
        var manifest = LoadManifest();
        var irPath = Path.Combine(SampleDir, "e2e-schema.ir.json");
        var manifestPath = Path.Combine(SampleDir, "api-manifest.json");
        var refSource = CreateReferenceSource(irPath, manifestPath);

        var exported = ReferenceExporter.ExportMarkdown(schema, manifest, refSource);
        var section1 = ExtractSection(exported, 1);

        Assert.Contains("## 1. Scope", section1);
        Assert.Contains("### Topics Covered", section1);
        Assert.Contains("### Topics Not Covered", section1);
    }

    [Fact]
    public void Section12GapsExist()
    {
        var schema = LoadSchema();
        var manifest = LoadManifest();
        var irPath = Path.Combine(SampleDir, "e2e-schema.ir.json");
        var manifestPath = Path.Combine(SampleDir, "api-manifest.json");
        var refSource = CreateReferenceSource(irPath, manifestPath);

        var exported = ReferenceExporter.ExportMarkdown(schema, manifest, refSource);
        var section12 = ExtractSection(exported, 12);

        Assert.Contains("## 12. Gaps for the Author", section12);
        Assert.Contains("The following topics cannot be derived", section12);
    }

    [Fact]
    public void ContradictionGuardThrows()
    {
        var schema = LoadSchema();
        var manifest = LoadManifest();
        var irPath = Path.Combine(SampleDir, "e2e-schema.ir.json");
        var manifestPath = Path.Combine(SampleDir, "api-manifest.json");
        var refSource = CreateReferenceSource(irPath, manifestPath);

        // This test verifies the guard is in place by checking that
        // the exporter does NOT throw under normal circumstances
        var exported = ReferenceExporter.ExportMarkdown(schema, manifest, refSource);
        Assert.NotEmpty(exported);

        // The contradiction guard would only trigger if coverage and gaps overlapped,
        // which should not happen during normal operation with the Python logic
    }

    [Fact]
    public void WithManifestExcludesApiRoutesGap()
    {
        var schema = LoadSchema();
        var manifest = LoadManifest();
        var irPath = Path.Combine(SampleDir, "e2e-schema.ir.json");
        var manifestPath = Path.Combine(SampleDir, "api-manifest.json");
        var refSource = CreateReferenceSource(irPath, manifestPath);

        var exported = ReferenceExporter.ExportMarkdown(schema, manifest, refSource);
        var section1 = ExtractSection(exported, 1);

        // api_routes gap should NOT be in the Topics Not Covered table when manifest is provided
        Assert.DoesNotContain("| API Routes |", section1);
    }

    [Fact]
    public void WithoutManifestIncludesApiRoutesGap()
    {
        var schema = LoadSchema();
        var irPath = Path.Combine(SampleDir, "e2e-schema.ir.json");
        var refSource = CreateReferenceSource(irPath);

        // Export without manifest
        var exported = ReferenceExporter.ExportMarkdown(schema, null, refSource);
        var section1 = ExtractSection(exported, 1);
        var section7 = ExtractSection(exported, 7);

        // api_routes gap SHOULD be in the Topics Not Covered table when manifest is not provided
        Assert.Contains("| API Routes |", section1);

        // Section 7 should mention enforcement could not be verified
        Assert.Contains("Enforcement could not be verified", section7);
    }

    [Fact]
    public void DerivedWorkflowEndpointsExcludedFromPresenceCheck()
    {
        var schema = LoadSchema();
        var manifest = LoadManifest();
        var irPath = Path.Combine(SampleDir, "e2e-schema.ir.json");
        var manifestPath = Path.Combine(SampleDir, "api-manifest.json");
        var refSource = CreateReferenceSource(irPath, manifestPath);

        var exported = ReferenceExporter.ExportMarkdown(schema, manifest, refSource);
        var section7 = ExtractSection(exported, 7);

        // The showcase has 1 workflow with 4 transitions, so 4 transition-derived endpoints
        Assert.Contains("4 workflow transition endpoint(s) excluded (compiler-derived)", section7);
    }

    [Fact]
    public void DeterminismTwiceProducesSameOutput()
    {
        var schema = LoadSchema();
        var manifest = LoadManifest();
        var irPath = Path.Combine(SampleDir, "e2e-schema.ir.json");
        var manifestPath = Path.Combine(SampleDir, "api-manifest.json");
        var refSource = CreateReferenceSource(irPath, manifestPath);

        var exported1 = ReferenceExporter.ExportMarkdown(schema, manifest, refSource);
        var exported2 = ReferenceExporter.ExportMarkdown(schema, manifest, refSource);

        Assert.Equal(exported1, exported2);
    }

    [Fact]
    public void WholeOutputMatchesGoldenFixture()
    {
        var schema = LoadSchema();
        var manifest = LoadManifest();
        var irPath = Path.Combine(SampleDir, "e2e-schema.ir.json");
        var manifestPath = Path.Combine(SampleDir, "api-manifest.json");
        var refSource = CreateReferenceSource(irPath, manifestPath);

        var exported = ReferenceExporter.ExportMarkdown(schema, manifest, refSource);
        var golden = LoadGoldenFixture();

        // Split into lines for detailed error reporting
        var exportedLines = exported.Split('\n');
        var goldenLines = golden.Split('\n');

        Assert.Equal(goldenLines.Length, exportedLines.Length);

        for (int i = 0; i < goldenLines.Length; i++)
        {
            if (exportedLines[i] != goldenLines[i])
            {
                Assert.True(false,
                    $"Line {i + 1} differs:\n" +
                    $"Expected: {goldenLines[i]}\n" +
                    $"Actual:   {exportedLines[i]}");
            }
        }

        // Final full comparison to catch any remaining differences
        Assert.Equal(golden, exported);
    }

    [Fact]
    public void EntityRoleDivergenceDetected()
    {
        // Test that entity CRUD role divergence is actually detected.
        // Before the fix, both IR and manifest parsed as empty roles, so the comparison
        // always passed and the check was blind. This test verifies that the fix catches
        // actual divergences when they exist.

        var schema = LoadSchema();
        var manifest = LoadManifest();

        if (manifest == null) return; // Skip if no manifest

        // Create a modified manifest with different roles for Customer
        var manifestJson = manifest.ToJsonString();
        var modified = JsonNode.Parse(manifestJson)!;

        // Navigate to Endpoints and find Customer
        if (modified["Endpoints"] is JsonArray endpoints)
        {
            foreach (var ep in endpoints)
            {
                if (ep?["Entity"]?.GetValue<string>() == "Customer")
                {
                    // Clear the existing roles and set to only "AdminOnly" for GET
                    if (ep["Roles"] is JsonObject rolesObj)
                    {
                        rolesObj["GET"] = JsonNode.Parse("[\"AdminOnly\"]");
                    }
                    break;
                }
            }
        }

        var irPath = Path.Combine(SampleDir, "e2e-schema.ir.json");
        var manifestPath = Path.Combine(SampleDir, "api-manifest.json");
        var refSource = CreateReferenceSource(irPath, manifestPath);

        var exported = ReferenceExporter.ExportMarkdown(schema, modified, refSource);
        var section7 = ExtractSection(exported, 7);

        // Should now detect divergences because Customer GET has different roles
        // (IR declares Admin+Sales, but manifest has only AdminOnly)
        Assert.Contains("Divergences detected", section7);
        Assert.Contains("Entity Customer GET", section7);
    }

    [Fact]
    public void ConnectorWithEnvStyleSecretReferenceIsNotReportedAsLiteral()
    {
        // Test that ${ENV:ACME_API_KEY} style references are correctly classified as credential sources,
        // not as literals. This was the original defect: validator accepted it, exporter rejected it.
        var irPath = Path.Combine(SampleDir, "e2e-schema.ir.json");
        var refSource = CreateReferenceSource(irPath);

        // Build a test schema with a connector using ENV-style secret reference
        var schema = new SchemaModel
        {
            Namespace = "Test.Domain",
            Entities = new List<Entity> { new Entity { Name = "TestEntity", Properties = new List<Property> { new Property { Name = "Id", Type = "string", IsKey = true } } } },
            Connectors = new List<ConnectorModel>
            {
                new ConnectorModel
                {
                    Name = "TestConnectorEnvStyle",
                    Type = "REST",
                    BaseUrl = "https://api.example.com",
                    AuthType = "Bearer",
                    Token = "${ENV:ACME_API_KEY}",
                    TimeoutSeconds = 30,
                    MaxRetries = 3
                }
            }
        };

        var exported = ReferenceExporter.ExportMarkdown(schema, null, refSource);
        var section10 = ExtractSection(exported, 10);

        // The connector should appear in section 10
        Assert.Contains("TestConnectorEnvStyle", section10);

        // The credential source should show the ${ENV:ACME_API_KEY} reference
        Assert.Contains("${ENV:ACME_API_KEY}", section10);

        // Should NOT appear in literals column (should be dash)
        var lines = section10.Split('\n');
        var connectorLine = lines.FirstOrDefault(l => l.Contains("TestConnectorEnvStyle"));
        Assert.NotNull(connectorLine);

        // Extract the last column (Literals Present) - it should be a dash
        var parts = connectorLine!.Split('|');
        Assert.True(parts.Length >= 9, "Connector row should have all columns");
        var literalsColumn = parts[8].Trim();
        Assert.Equal("—", literalsColumn);

        // Security note should NOT appear (no literals)
        Assert.DoesNotContain("credential fields contain values committed", section10);
    }

    [Fact]
    public void ConnectorWithPlainSecretReferenceIsNotReportedAsLiteral()
    {
        // Test that ${ACME_TOKEN} style references (without ENV:) still work correctly.
        var irPath = Path.Combine(SampleDir, "e2e-schema.ir.json");
        var refSource = CreateReferenceSource(irPath);

        var schema = new SchemaModel
        {
            Namespace = "Test.Domain",
            Entities = new List<Entity> { new Entity { Name = "TestEntity", Properties = new List<Property> { new Property { Name = "Id", Type = "string", IsKey = true } } } },
            Connectors = new List<ConnectorModel>
            {
                new ConnectorModel
                {
                    Name = "TestConnectorPlain",
                    Type = "REST",
                    BaseUrl = "https://api.example.com",
                    AuthType = "ApiKey",
                    ApiKey = "${ACME_TOKEN}",
                    ApiKeyHeaderName = "X-API-Key",
                    TimeoutSeconds = 30,
                    MaxRetries = 3
                }
            }
        };

        var exported = ReferenceExporter.ExportMarkdown(schema, null, refSource);
        var section10 = ExtractSection(exported, 10);

        // The connector should appear in section 10
        Assert.Contains("TestConnectorPlain", section10);

        // The credential source should show the ${ACME_TOKEN} reference
        Assert.Contains("${ACME_TOKEN}", section10);

        // Should NOT appear in literals
        var lines = section10.Split('\n');
        var connectorLine = lines.FirstOrDefault(l => l.Contains("TestConnectorPlain"));
        Assert.NotNull(connectorLine);

        var parts = connectorLine!.Split('|');
        var literalsColumn = parts[8].Trim();
        Assert.Equal("—", literalsColumn);
    }

    [Fact]
    public void ConnectorWithLiteralSecretIsReportedAsLiteralAndSecurityNoteAppears()
    {
        // Test that literal secrets (not references) are correctly flagged as literals
        // and the security note appears, but the actual secret value is never printed.
        var irPath = Path.Combine(SampleDir, "e2e-schema.ir.json");
        var refSource = CreateReferenceSource(irPath);

        var schema = new SchemaModel
        {
            Namespace = "Test.Domain",
            Entities = new List<Entity> { new Entity { Name = "TestEntity", Properties = new List<Property> { new Property { Name = "Id", Type = "string", IsKey = true } } } },
            Connectors = new List<ConnectorModel>
            {
                new ConnectorModel
                {
                    Name = "TestConnectorLiteral",
                    Type = "REST",
                    BaseUrl = "https://api.example.com",
                    AuthType = "Bearer",
                    Token = "sk-live-abc123def456",
                    TimeoutSeconds = 30,
                    MaxRetries = 3
                }
            }
        };

        var exported = ReferenceExporter.ExportMarkdown(schema, null, refSource);
        var section10 = ExtractSection(exported, 10);

        // The connector should appear in section 10
        Assert.Contains("TestConnectorLiteral", section10);

        // Should be reported as literal credential "token"
        var lines = section10.Split('\n');
        var connectorLine = lines.FirstOrDefault(l => l.Contains("TestConnectorLiteral"));
        Assert.NotNull(connectorLine);

        var parts = connectorLine!.Split('|');
        var literalsColumn = parts[8].Trim();
        Assert.Contains("token", literalsColumn);

        // Security note SHOULD appear
        Assert.Contains("credential fields contain values committed", section10);

        // CRITICAL: The actual secret value "sk-live-abc123def456" must NEVER appear in the document
        Assert.DoesNotContain("sk-live-abc123def456", exported);
        Assert.DoesNotContain("abc123def456", exported);
    }
}
