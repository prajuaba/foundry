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

        // Read records
        while (reader.Read())
        {
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
                        try
                        {
                            var convertedVal = Convert.ChangeType(val, targetType);
                            prop.SetValue(item, convertedVal);
                        }
                        catch
                        {
                            try
                            {
                                var strVal = val.ToString();
                                if (strVal != null)
                                {
                                    if (targetType == typeof(Guid))
                                    {
                                        prop.SetValue(item, Guid.Parse(strVal));
                                    }
                                    else if (targetType == typeof(DateTime))
                                    {
                                        prop.SetValue(item, DateTime.Parse(strVal));
                                    }
                                    else
                                    {
                                        var converted = Convert.ChangeType(strVal, targetType);
                                        prop.SetValue(item, converted);
                                    }
                                }
                            }
                            catch
                            {
                                // Fail silently and leave property default
                            }
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
