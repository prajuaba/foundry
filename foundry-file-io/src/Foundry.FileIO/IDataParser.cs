using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Foundry.FileIO;

/// <summary>
/// Defines the contract for parsing files of type TOut in a streaming, memory-efficient manner.
/// </summary>
public interface IDataParser<TOut>
{
    /// <summary>
    /// Parses the file stream asynchronously row-by-row to prevent high memory usage.
    /// </summary>
    IAsyncEnumerable<TOut> ParseAsync(Stream fileStream, CancellationToken ct = default);
}
