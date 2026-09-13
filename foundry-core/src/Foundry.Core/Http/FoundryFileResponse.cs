using System;

namespace Foundry.Core.Http;

/// <summary>
/// A handler returns this instead of a payload when the endpoint's response is a file download
/// rather than JSON. The endpoint layer cannot build the bytes itself because Foundry.Api does not
/// reference Foundry.FileIO, which is why the handler produces them.
/// </summary>
public sealed record FoundryFileResponse
{
    /// <summary>The file bytes.</summary>
    public required byte[] Content { get; init; }

    /// <summary>The download file name, e.g. "resource-utilization-20260913.csv".</summary>
    public required string FileName { get; init; }

    /// <summary>The media type, e.g. "text/csv".</summary>
    public required string MediaType { get; init; }
}
