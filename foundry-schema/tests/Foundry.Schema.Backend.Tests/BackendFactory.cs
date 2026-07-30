using Microsoft.AspNetCore.Mvc.Testing;

namespace Foundry.Schema.Backend.Tests;

/// <summary>
/// Hosts the Studio backend in-process, confined to a workspace root the test owns.
/// </summary>
/// <remarks>
/// <para>
/// The root the backend permits writes into is read once from <c>FOUNDRY_WORKSPACE_ROOT</c>, falling
/// back to a path derived from the current directory. Deriving it from the working directory is what
/// the backend used to do unconditionally, which made the boundary depend on however the process
/// happened to be launched — so a test that asserted "outside the workspace is refused" would have
/// been asserting something about the test runner's cwd.
/// </para>
/// <para>
/// Set here, before the host starts, so the tests exercise a boundary they can name.
/// </para>
/// </remarks>
public sealed class BackendFactory : WebApplicationFactory<Program>
{
    public string WorkspaceRoot { get; }

    public BackendFactory()
    {
        WorkspaceRoot = Path.Combine(
            Path.GetTempPath(), "foundry-backend-tests-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(WorkspaceRoot);

        // Read by a static initialiser, so it has to be in place before the host is built.
        Environment.SetEnvironmentVariable("FOUNDRY_WORKSPACE_ROOT", WorkspaceRoot);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;

        try { Directory.Delete(WorkspaceRoot, recursive: true); } catch { /* best effort */ }
        Environment.SetEnvironmentVariable("FOUNDRY_WORKSPACE_ROOT", null);
    }
}
