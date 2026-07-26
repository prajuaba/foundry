using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CsvHelper;
using CsvHelper.Configuration;

namespace Foundry.FileIO;

/// <summary>
/// Memory-efficient CSV exporter using CsvHelper writing to output stream.
/// </summary>
public sealed class CsvDataExporter<TIn>
{
    private readonly CsvConfiguration _config;

    public CsvDataExporter(CsvConfiguration? config = null)
    {
        _config = config ?? new CsvConfiguration(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads records from dataStream and streams CSV bytes directly into outputStream.
    /// </summary>
    public async Task ExportAsync(IAsyncEnumerable<TIn> dataStream, Stream outputStream, CancellationToken ct = default)
    {
        using var writer = new StreamWriter(outputStream, leaveOpen: true);
        using var csv = new CsvWriter(writer, _config);

        // Exported data is user-controlled, and a spreadsheet evaluates any cell whose text begins
        // with =, +, @ or -. Registered as a string converter rather than applied per field so it
        // cannot be forgotten, and so numeric and date columns are untouched by construction.
        csv.Context.TypeConverterCache.AddConverter<string>(new FormulaSafeStringConverter());

        csv.WriteHeader<TIn>();
        await csv.NextRecordAsync();

        await foreach (var item in dataStream.WithCancellation(ct))
        {
            csv.WriteRecord(item);
            await csv.NextRecordAsync();
        }
    }
}
