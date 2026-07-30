using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Foundry.Schema.Backend.Tests;

/// <summary>
/// The Studio backend's two writing endpoints, driven over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// This backend had no tests and no gate that ran it. It compiled as part of the solution, and
/// nothing had ever sent it a request — which is how an incomplete path-traversal guard came to sit
/// in <c>/api/save-pocos</c>: the output <em>directory</em> was resolved and checked, and the file
/// names inside it went straight into <c>Path.Combine</c>. A key of
/// <c>../../../../../../tmp/x</c> wrote outside the workspace, and the response reported success
/// naming the directory it had not written to.
/// </para>
/// <para>
/// Studio reaches these endpoints from a browser, so the request body is attacker-shaped input in
/// the ordinary case, not the exotic one.
/// </para>
/// </remarks>
public class SaveEndpointTests : IClassFixture<BackendFactory>, IDisposable
{
    private readonly BackendFactory _factory;
    private readonly HttpClient _client;

    /// <summary>A directory inside the workspace root the backend is confined to.</summary>
    private readonly string _outputDir;

    public SaveEndpointTests(BackendFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _outputDir = Path.Combine(factory.WorkspaceRoot, "out-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_outputDir, recursive: true); } catch { /* best effort */ }
    }

    private Task<HttpResponseMessage> SavePocos(Dictionary<string, string> files, string? outputPath = null)
        => _client.PostAsJsonAsync("/api/save-pocos", new
        {
            OutputPath = outputPath ?? _outputDir,
            Files = files,
        });

    // ── The traversal ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("../escaped.cs")]
    [InlineData("../../escaped.cs")]
    [InlineData("../../../../../../../../tmp/escaped.cs")]
    [InlineData("subdir/../../escaped.cs")]
    public async Task AFileNameThatClimbsOutOfTheDirectoryIsRefused(string name)
    {
        var response = await SavePocos(new Dictionary<string, string> { [name] = "escaped" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnAbsoluteFileNameIsRefused()
    {
        // Path.Combine returns its second argument outright when that argument is rooted, so an
        // absolute name replaced the validated directory rather than being placed under it.
        //
        // The target is unique per run. A fixed name outside the workspace is not cleaned up by
        // anything -- and cannot be, since the whole point is that the endpoint must not create it --
        // so one run against a vulnerable build leaves a file that fails every run afterwards. That
        // happened here while proving the fix: the assertion held, and then held against the wrong
        // cause.
        var absolute = Path.Combine(Path.GetTempPath(), $"foundry-absolute-{Guid.NewGuid():N}.cs");

        var response = await SavePocos(new Dictionary<string, string> { [absolute] = "escaped" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(absolute));
    }

    [Fact]
    public async Task NothingIsWrittenWhenOneNameInTheBatchIsRefused()
    {
        // Destinations are resolved before anything is written. A batch that half-lands and is then
        // refused leaves the workspace in a state nobody asked for.
        var response = await SavePocos(new Dictionary<string, string>
        {
            ["Good.cs"] = "fine",
            ["../bad.cs"] = "escaped",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(_outputDir, "Good.cs")));
    }

    [Fact]
    public async Task AnOutputDirectoryOutsideTheWorkspaceIsRefused()
    {
        var response = await SavePocos(
            new Dictionary<string, string> { ["X.cs"] = "x" },
            outputPath: Path.GetTempPath());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ASiblingDirectoryWhoseNameSharesThePrefixIsRefused()
    {
        // A bare StartsWith accepts '/work/foundry-evil' against a root of '/work/foundry'. The
        // separator is what makes it a containment check rather than a string test.
        var sibling = _factory.WorkspaceRoot + "-evil";

        var response = await SavePocos(
            new Dictionary<string, string> { ["X.cs"] = "x" }, outputPath: sibling);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(Directory.Exists(sibling));
    }

    // ── What must still work ────────────────────────────────────────────────

    [Fact]
    public async Task AnOrdinaryFileIsWritten()
    {
        // The control. Without it every assertion above passes against an endpoint that refuses
        // everything, which is not a fix.
        var response = await SavePocos(new Dictionary<string, string> { ["Order.cs"] = "// order" });

        response.EnsureSuccessStatusCode();
        Assert.Equal("// order", File.ReadAllText(Path.Combine(_outputDir, "Order.cs")));
    }

    [Fact]
    public async Task ASubdirectoryInTheNameIsAllowedAndCreated()
    {
        // The compiler emits into subdirectories, so 'Commands/SubmitOrderCommand.cs' is an
        // ordinary key. Refusing every separator would have been a fix that broke the feature.
        var response = await SavePocos(new Dictionary<string, string>
        {
            ["Commands/SubmitOrderCommand.cs"] = "// command",
        });

        response.EnsureSuccessStatusCode();
        Assert.True(File.Exists(Path.Combine(_outputDir, "Commands", "SubmitOrderCommand.cs")));
    }

    [Fact]
    public async Task TheResponseReportsWhatWasActuallyWritten()
    {
        // It used to report the requested directory whatever happened, so a file that landed in /tmp
        // was announced as having been saved into the workspace.
        var response = await SavePocos(new Dictionary<string, string> { ["Order.cs"] = "// order" });

        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var written = body.RootElement.GetProperty("files").EnumerateArray()
            .Select(e => e.GetString()).ToList();

        Assert.Equal([Path.Combine(_outputDir, "Order.cs")], written);
    }

    // ── The manifest writer, which was already correct ──────────────────────

    [Fact]
    public async Task TheManifestIsDerivedFromTheIrAndWritten()
    {
        var target = Path.Combine(_outputDir, "api-manifest.json");

        var response = await _client.PostAsJsonAsync("/api/save-manifest", new
        {
            OutputPath = target,
            Schema = new
            {
                Namespace = "Sales.Domain",
                Entities = new[]
                {
                    new
                    {
                        Name = "Order",
                        ApiEnabledMethods = new[] { "GET" },
                        Properties = new[] { new { Name = "Id", Type = "ObjectId", IsKey = true } },
                    },
                },
            },
        });

        response.EnsureSuccessStatusCode();

        using var manifest = JsonDocument.Parse(File.ReadAllText(target));
        var endpoint = manifest.RootElement.GetProperty("Endpoints")[0];

        Assert.Equal("Order", endpoint.GetProperty("Entity").GetString());

        // The compiler's own route, not a second opinion about what it would be.
        Assert.Equal("/api/orders", endpoint.GetProperty("Route").GetString());
    }

    [Fact]
    public async Task AManifestPathOutsideTheWorkspaceIsRefused()
    {
        var response = await _client.PostAsJsonAsync("/api/save-manifest", new
        {
            OutputPath = Path.Combine(Path.GetTempPath(), "escaped-manifest.json"),
            Schema = new { Namespace = "X", Entities = Array.Empty<object>() },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
