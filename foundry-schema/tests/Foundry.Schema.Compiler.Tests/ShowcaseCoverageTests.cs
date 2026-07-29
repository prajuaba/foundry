using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// The E2E showcase exercises the whole IR, and its committed output matches its schema.
/// </summary>
/// <remarks>
/// <para>
/// The showcase is the only artefact that claims to demonstrate the framework, and for a long time
/// it demonstrated a third of it: 34 of the 100 declarable IR fields, with the rest — workflows,
/// DTOs, connectors, multi-tenancy, partitioning, real-time, file IO, caching, method gating —
/// appearing nowhere. Worse, its C# was hand-written beside a schema nothing compiled, so the two
/// had drifted: an entity the code did not have, a Kafka topic it never published to, an endpoint
/// the schema said was role-restricted and the code left anonymous.
/// </para>
/// <para>
/// Widening it once fixes that once. These two gates are what stop it happening again: the first
/// fails when the IR grows a construct the showcase does not use, the second when the committed
/// output stops matching the schema it came from. Both are cheap — neither runs a build.
/// </para>
/// </remarks>
public class ShowcaseCoverageTests
{
    /// <summary>
    /// Fields deliberately not exercised, each with the reason.
    /// </summary>
    /// <remarks>
    /// Empty, and that is the point: adding a name here is a decision someone has to write down and
    /// defend in review, rather than a gap that accumulates silently. The same guard shape as
    /// <c>RepairableWarnings</c>.
    /// </remarks>
    private static readonly Dictionary<string, string> DeliberatelyUnexercised = new(StringComparer.Ordinal);

    private static string? RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Foundry.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName;
    }

    private static string ShowcaseDirectory(string root)
        => Path.Combine(root, "samples", "Foundry.E2E.Showcase");

    private static SchemaModel LoadShowcase(string root)
        => JsonSerializer.Deserialize<SchemaModel>(
            File.ReadAllText(Path.Combine(ShowcaseDirectory(root), "e2e-schema.ir.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    // ── The IR surface ──────────────────────────────────────────────────────

    /// <summary>Every property name the normative IR schema declares, anywhere in it.</summary>
    private static HashSet<string> DeclarableFields(string root)
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, ".foundry", "skill", "foundry.ir.schema.json")));

        var declarable = new HashSet<string>(StringComparer.Ordinal);

        // The root's own properties, and each named definition's — and no deeper.
        //
        // Not a recursive walk for every member called "properties": an entity has an IR field of
        // that exact name, so recursing collected the JSON Schema keywords describing *it*
        // ("type", "items") as though they were declarable IR fields.
        void CollectFrom(JsonElement schema)
        {
            if (schema.ValueKind != JsonValueKind.Object) return;
            if (!schema.TryGetProperty("properties", out var properties)) return;
            if (properties.ValueKind != JsonValueKind.Object) return;

            foreach (var field in properties.EnumerateObject()) declarable.Add(field.Name);
        }

        CollectFrom(doc.RootElement);

        foreach (var key in new[] { "$defs", "definitions" })
        {
            if (!doc.RootElement.TryGetProperty(key, out var defs)) continue;
            foreach (var definition in defs.EnumerateObject()) CollectFrom(definition.Value);
        }

        return declarable;
    }

    /// <summary>Every property name the showcase document actually uses.</summary>
    private static HashSet<string> FieldsUsedByShowcase(string root)
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(ShowcaseDirectory(root), "e2e-schema.ir.json")));

        var used = new HashSet<string>(StringComparer.Ordinal);

        void Collect(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var member in node.EnumerateObject())
                {
                    used.Add(member.Name);
                    Collect(member.Value);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray()) Collect(item);
            }
        }

        Collect(doc.RootElement);
        return used;
    }

    [Fact]
    public void TheShowcaseExercisesEveryDeclarableIrField()
    {
        var root = RepositoryRoot();
        if (root is null) return; // Not running from the repo.

        var missing = DeclarableFields(root)
            .Except(FieldsUsedByShowcase(root))
            .Where(f => !DeliberatelyUnexercised.ContainsKey(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "The IR declares fields the showcase never uses, so nothing compiles or runs them:\n  "
            + string.Join("\n  ", missing)
            + "\n\nAdd them to samples/Foundry.E2E.Showcase/e2e-schema.ir.json, or record why not in "
            + nameof(DeliberatelyUnexercised) + ".");
    }

    [Fact]
    public void TheAllowlistStaysEmpty()
    {
        // Not a tautology: it fails the moment someone adds a name, which is the point. An exemption
        // should be argued for in review rather than merged as a one-line diff nobody reads.
        Assert.Empty(DeliberatelyUnexercised);
    }

    // ── Committed output matches the schema ─────────────────────────────────

    [Fact]
    public void TheCommittedGeneratedCodeMatchesTheSchema()
    {
        var root = RepositoryRoot();
        if (root is null) return;

        var generatedDir = Path.Combine(ShowcaseDirectory(root), "Generated");
        var stale = new List<string>();

        foreach (var file in PocoGenerator.GenerateFiles(LoadShowcase(root)))
        {
            var path = Path.Combine(generatedDir, file.Path + ".cs");

            // Scaffolds hold the showcase's own business logic and are written once, so their
            // content is deliberately not the compiler's any more. Their existence still is.
            if (file.Kind == EmitKind.Scaffold)
            {
                if (!File.Exists(path)) stale.Add($"{file.Path}.cs (scaffold missing)");
                continue;
            }

            if (!File.Exists(path))
            {
                stale.Add($"{file.Path}.cs (missing)");
            }
            else if (!string.Equals(File.ReadAllText(path), file.Content, StringComparison.Ordinal))
            {
                stale.Add($"{file.Path}.cs (differs)");
            }
        }

        Assert.True(stale.Count == 0,
            "The showcase's committed output no longer matches its schema:\n  "
            + string.Join("\n  ", stale)
            + "\n\nRegenerate it:\n"
            + "  dotnet foundry.dll schema build -i samples/Foundry.E2E.Showcase/e2e-schema.ir.json \\\n"
            + "      -o samples/Foundry.E2E.Showcase/Generated \\\n"
            + "      --manifest samples/Foundry.E2E.Showcase/api-manifest.json");
    }

    [Fact]
    public void TheCommittedManifestMatchesTheSchema()
    {
        var root = RepositoryRoot();
        if (root is null) return;

        var committed = File.ReadAllText(Path.Combine(ShowcaseDirectory(root), "api-manifest.json"));

        Assert.Equal(ApiManifestGenerator.Generate(LoadShowcase(root)), committed);
    }

    [Fact]
    public void NothingUnderGeneratedIsOrphaned()
    {
        // The other direction: a file the compiler no longer emits keeps compiling, so a removed
        // construct leaves working code behind that nothing in the schema accounts for. That is how
        // the rival GraphQL surface would have survived being deleted.
        var root = RepositoryRoot();
        if (root is null) return;

        var generatedDir = Path.Combine(ShowcaseDirectory(root), "Generated");
        var expected = PocoGenerator.GenerateFiles(LoadShowcase(root))
            .Select(f => Path.GetFullPath(Path.Combine(generatedDir, f.Path + ".cs")))
            .ToHashSet(StringComparer.Ordinal);

        var orphans = Directory.GetFiles(generatedDir, "*.cs", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(p => !expected.Contains(p))
            .Select(p => Path.GetRelativePath(generatedDir, p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.True(orphans.Count == 0,
            "Files under Generated/ that the compiler does not emit:\n  " + string.Join("\n  ", orphans));
    }
}
