using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ExcelDataReader;

namespace Foundry.FileIO;

/// <summary>
/// Memory-efficient streaming Excel parser using ExcelDataReader with reflection mapping.
/// </summary>
public sealed class ExcelDataParser<TOut> : IDataParser<TOut> where TOut : class, new()
{
    static ExcelDataParser()
    {
        // Register CodePages encoding provider to support legacy Excel XLS/XLSX formats in .NET Core
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// When <c>true</c>, a cell that cannot be converted leaves its property at the default and the
    /// failure is recorded in <see cref="UnconvertibleValues"/> instead of throwing.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>false</c>: an import that silently substitutes zeros produces a row count the
    /// user expects and data they cannot trust. Opting in still surfaces every failure, rather than
    /// discarding them as the previous behaviour did.
    /// </remarks>
    public bool SkipUnconvertibleValues { get; init; }

    /// <summary>
    /// Cells that could not be converted, populated when <see cref="SkipUnconvertibleValues"/> is set.
    /// </summary>
    public IList<ExcelCellError> UnconvertibleValues { get; } = new List<ExcelCellError>();

    /// <summary>
    /// Writable properties of <typeparamref name="TOut"/> that no column in the file supplied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Populated once the header row has been read. A column absent from the spreadsheet is not an
    /// error the parser can decide on its own — plenty of imports legitimately fill only some fields
    /// — but it is silent, and silence here has the shape this codebase keeps producing: every row
    /// gets the type's default, the row count matches what the user expected, and nothing looks
    /// wrong. A caller that knows which columns matter can now check.
    /// </para>
    /// <para>
    /// Empty until <see cref="ParseAsync"/> has been enumerated at least as far as the header.
    /// </para>
    /// </remarks>
    public IReadOnlyCollection<string> UnmatchedProperties => _unmatchedProperties;

    private readonly List<string> _unmatchedProperties = new();

    /// <summary>
    /// Converts a cell value to <paramref name="targetType"/>, reporting failure rather than throwing.
    /// </summary>
    /// <remarks>
    /// Uses the invariant culture. <c>Convert.ChangeType</c> without an explicit culture reads the
    /// ambient one, so the same spreadsheet imported on two differently-configured servers produced
    /// different numbers.
    /// </remarks>
    internal static bool TryConvert(object value, Type targetType, out object? converted)
    {
        converted = null;
        if (value is null) return false;

        try
        {
            if (targetType.IsInstanceOfType(value))
            {
                converted = value;
                return true;
            }

            if (targetType.IsEnum)
            {
                converted = Enum.Parse(targetType, value.ToString() ?? string.Empty, ignoreCase: true);
                return true;
            }

            if (targetType == typeof(Guid))
            {
                converted = Guid.Parse(value.ToString() ?? string.Empty);
                return true;
            }

            if (targetType == typeof(DateTime))
            {
                converted = value is DateTime dateTime
                    ? dateTime
                    : DateTime.Parse(
                        value.ToString() ?? string.Empty,
                        System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }

            converted = Convert.ChangeType(
                value, targetType, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException
                                      or ArgumentException)
        {
            converted = null;
            return false;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TOut> ParseAsync(Stream fileStream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = ExcelReaderFactory.CreateReader(fileStream);

        // Read headers (first row)
        if (!reader.Read()) yield break;

        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < reader.FieldCount; i++)
        {
            var val = reader.GetValue(i)?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(val))
            {
                headers[val] = i;
            }
        }

        // Cache properties of TOut
        var properties = typeof(TOut).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToList();

        // Recorded rather than rejected. A file that supplies no column for a property leaves it at
        // the type's default on every row, which is indistinguishable from a file that supplied the
        // default -- so a caller that knows the column matters needs to be able to see it.
        _unmatchedProperties.Clear();
        _unmatchedProperties.AddRange(properties
            .Where(p => !headers.ContainsKey(p.Name))
            .Select(p => p.Name));

        // Read records
        // Row 1 is the header, so data starts at 2 -- matching what a user sees in the spreadsheet.
        var rowNumber = 1;

        while (reader.Read())
        {
            rowNumber++;
            ct.ThrowIfCancellationRequested();
            var item = new TOut();

            foreach (var prop in properties)
            {
                if (headers.TryGetValue(prop.Name, out var colIndex) && colIndex < reader.FieldCount)
                {
                    var val = reader.GetValue(colIndex);
                    if (val != null)
                    {
                        var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

                        if (TryConvert(val, targetType, out var converted))
                        {
                            prop.SetValue(item, converted);
                        }
                        else if (SkipUnconvertibleValues)
                        {
                            UnconvertibleValues.Add(new ExcelCellError(rowNumber, prop.Name, val.ToString(), targetType));
                        }
                        else
                        {
                            // Loudly, by default.
                            //
                            // This used to swallow the failure and leave the property at its default,
                            // so a spreadsheet cell of "1,234.00 USD" produced an imported row with a
                            // TotalAmount of 0 and reported success. For a bulk import that is data
                            // corruption presented as a clean result, and the row count matches what
                            // the user expected, so nothing looks wrong.
                            throw new InvalidDataException(
                                $"Row {rowNumber}, column '{prop.Name}': cannot convert "
                                + $"'{val}' to {targetType.Name}. Set SkipUnconvertibleValues to import "
                                + "the row anyway and collect the failures in UnconvertibleValues.");
                        }
                    }
                }
            }

            yield return item;
            
            // Allow thread pool yielding for high-volume streams
            await Task.Yield();
        }
    }
}
