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
            // A completely empty upload has nothing to import. The result of ReadAsync was previously
            // discarded, so ReadHeader then threw CsvHelper's "No header record was found" -- a
            // library exception surfacing from what is an ordinary user mistake. A file that has
            // content but an unusable header still fails, which is correct.
            if (!await csv.ReadAsync()) yield break;

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
