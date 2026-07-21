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
    /// Sanitizes filenames to eliminate potential path traversal inputs.
    /// </summary>
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
            .ToArray());

        if (string.IsNullOrWhiteSpace(cleanName))
        {
            return Guid.NewGuid().ToString("N");
        }

        return cleanName;
    }
}
