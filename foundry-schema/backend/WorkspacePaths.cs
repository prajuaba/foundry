using System;
using System.IO;

namespace Foundry.Schema.Backend;

/// <summary>
/// Decides where the Studio backend is allowed to write. The single implementation of that rule.
/// </summary>
/// <remarks>
/// <para>
/// Both save endpoints validated the directory they were given and neither validated what went into
/// it. <c>/api/save-pocos</c> then did <c>Path.Combine(resolvedPath, file.Key)</c> with a key taken
/// straight from the request body, so a filename of <c>../../../../../../tmp/x</c> escaped the
/// workspace entirely — and the response still reported "Successfully saved 1 classes to:
/// &lt;the allowed directory&gt;". A guard that reads as protection and is not is worse than none.
/// </para>
/// <para>
/// Rejecting separators outright is not the fix: the compiler emits into subdirectories, so
/// <c>Commands/SubmitOrderCommand.cs</c> is an ordinary and legitimate key. The rule has to be that
/// the <em>resolved</em> destination stays inside the root, which is what this does.
/// </para>
/// </remarks>
public static class WorkspacePaths
{
    /// <summary>
    /// The directory tree the backend may write to.
    /// </summary>
    /// <remarks>
    /// Overridable with <c>FOUNDRY_WORKSPACE_ROOT</c>. The default is derived from the current
    /// directory, which means it moved with however the process happened to be launched — a boundary
    /// that depends on the caller's shell is not a boundary anyone can reason about.
    /// </remarks>
    public static string Root { get; } = ResolveRoot();

    private static string ResolveRoot()
    {
        var configured = Environment.GetEnvironmentVariable("FOUNDRY_WORKSPACE_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", ".."));
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> resolves to something inside <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// Compares with a trailing separator on the root. A bare <c>StartsWith</c> also accepts a
    /// sibling whose name merely begins with the root's — <c>/work/foundry-evil</c> passes a prefix
    /// test against <c>/work/foundry</c> — so the separator is what makes it a containment check
    /// rather than a string test.
    /// </remarks>
    public static bool IsInside(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(candidate);

        if (string.Equals(fullRoot, fullCandidate, StringComparison.OrdinalIgnoreCase)) return true;

        var withSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        return fullCandidate.StartsWith(withSeparator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a request's output directory, or explains why it is refused.
    /// </summary>
    public static bool TryResolveDirectory(string? requested, out string resolved, out string? error)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            resolved = string.Empty;
            error = "OutputPath is required.";
            return false;
        }

        resolved = Path.GetFullPath(requested);

        if (!IsInside(Root, resolved))
        {
            error = $"Output path must be within the workspace: {Root}";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Resolves one file inside an already-validated directory, or explains why it is refused.
    /// </summary>
    /// <param name="directory">A directory that has already passed <see cref="TryResolveDirectory"/>.</param>
    /// <param name="relativeName">The key from the request. May contain subdirectories.</param>
    public static bool TryResolveFile(string directory, string relativeName, out string resolved, out string? error)
    {
        resolved = string.Empty;

        if (string.IsNullOrWhiteSpace(relativeName))
        {
            error = "A file name is required.";
            return false;
        }

        // Path.Combine returns the second argument outright when it is rooted, so an absolute name
        // silently replaces the directory rather than being placed under it.
        if (Path.IsPathRooted(relativeName))
        {
            error = $"'{relativeName}' is an absolute path; file names are relative to the output directory.";
            return false;
        }

        resolved = Path.GetFullPath(Path.Combine(directory, relativeName));

        if (!IsInside(directory, resolved))
        {
            error = $"'{relativeName}' resolves outside the output directory.";
            return false;
        }

        error = null;
        return true;
    }
}
