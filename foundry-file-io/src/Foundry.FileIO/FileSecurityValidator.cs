using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Foundry.FileIO;

/// <summary>
/// Provides security checking and sanitation utilities for uploaded files.
/// </summary>
public sealed class FileSecurityValidator
{
    private static readonly Dictionary<string, byte[]> MagicNumbers = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".png", [0x89, 0x50, 0x4E, 0x47] },
        { ".jpg", [0xFF, 0xD8, 0xFF] },
        { ".jpeg", [0xFF, 0xD8, 0xFF] },
        { ".gif", [0x47, 0x49, 0x46, 0x38] },
        { ".pdf", [0x25, 0x50, 0x44, 0x46] },
        { ".zip", [0x50, 0x4B, 0x03, 0x04] },
        { ".xlsx", [0x50, 0x4B, 0x03, 0x04] }, // Office Open XML zip-based
        { ".xls", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1] }
    };

    /// <summary>
    /// Verifies if a file stream's magic byte header matches its declared filename extension.
    /// </summary>
    public bool VerifySignature(string fileName, Stream fileStream)
    {
        ArgumentNullException.ThrowIfNull(fileStream);
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!MagicNumbers.TryGetValue(ext, out var expectedBytes) || expectedBytes.Length == 0)
        {
            // Permitted format without signature checks (e.g. CSV or TXT)
            return true;
        }

        if (!fileStream.CanRead) return false;

        byte[] header = new byte[expectedBytes.Length];
        
        long originalPosition = 0;
        if (fileStream.CanSeek)
        {
            originalPosition = fileStream.Position;
            fileStream.Position = 0;
        }

        try
        {
            fileStream.ReadExactly(header, 0, header.Length);
        }
        catch (EndOfStreamException)
        {
            if (fileStream.CanSeek)
            {
                fileStream.Position = originalPosition;
            }
            return false;
        }

        if (fileStream.CanSeek)
        {
            fileStream.Position = originalPosition;
        }

        return header.SequenceEqual(expectedBytes);
    }

    /// <summary>
    /// Maximum length of the returned name, in characters.
    /// </summary>
    /// <remarks>
    /// Most filesystems cap a single path component at 255 bytes. Without a cap the name is accepted
    /// here and then fails at write time with an IOException from inside the storage layer, far from
    /// the upload that caused it.
    /// </remarks>
    private const int MaxFileNameLength = 255;

    /// <summary>
    /// Sanitizes filenames to eliminate potential path traversal inputs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The result is always a single path component, never a traversal token. Stripping directory
    /// separators and invalid characters is not sufficient on its own: <c>Path.GetFileName("..")</c>
    /// returns <c>".."</c>, and <c>'.'</c> is not an invalid filename character, so a name consisting
    /// only of dots used to pass through unchanged — and <c>Path.Combine(uploadDirectory, "..")</c>
    /// resolves to the parent directory.
    /// </para>
    /// <para>
    /// A name that reduces to nothing usable is replaced with a generated one rather than rejected,
    /// so an upload with a hostile name still succeeds under a safe name.
    /// </para>
    /// </remarks>
    public string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return Guid.NewGuid().ToString("N");
        }

        var normalized = fileName.Replace('\\', '/');
        var nameOnly = Path.GetFileName(normalized);
        var invalidChars = Path.GetInvalidFileNameChars();

        var cleanName = new string(nameOnly
            .Where(c => !invalidChars.Contains(c))
            .ToArray())
            .Trim();

        // "." and ".." are directory references, not filenames, and any all-dots name is treated the
        // same way: there is no legitimate upload named "..." and several filesystems normalise it.
        if (cleanName.Length == 0 || cleanName.All(c => c == '.'))
        {
            return Guid.NewGuid().ToString("N");
        }

        if (cleanName.Length > MaxFileNameLength)
        {
            // Truncate the stem and keep the extension: downstream code keys content handling off the
            // extension, so dropping it would change how the file is treated.
            var extension = Path.GetExtension(cleanName);
            if (extension.Length >= MaxFileNameLength) extension = string.Empty;

            var stemLength = MaxFileNameLength - extension.Length;
            cleanName = cleanName.Substring(0, stemLength) + extension;
        }

        return cleanName;
    }
}
