using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using CsvHelper;
using CsvHelper.Configuration;

namespace Foundry.FileIO;

/// <summary>
/// Memory-efficient CSV parser using CsvHelper.
/// </summary>
public sealed class CsvDataParser<TOut> : IDataParser<TOut>
{
    private readonly CsvConfiguration _config;

    public CsvDataParser(CsvConfiguration? config = null)
    {
        _config = config ?? new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            HeaderValidated = null
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TOut> ParseAsync(Stream fileStream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(fileStream, leaveOpen: true);
        using var csv = new CsvReader(reader, _config);

        if (_config.HasHeaderRecord)
        {
            await csv.ReadAsync();
            csv.ReadHeader();
        }

        while (await csv.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            var record = csv.GetRecord<TOut>();
            if (record != null)
            {
                yield return record;
            }
        }
    }
}
