using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;

namespace Foundry.FileIO;

/// <summary>
/// Writes rows to a single-sheet .xlsx workbook: one header row of property names, one row per item.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <see cref="CsvDataExporter{TIn}"/>, with the same column rules so a report
/// reads the same in either format: public readable properties in declaration order, headed by
/// their names, which is also what <see cref="ExcelDataParser{TOut}"/> matches on the way back in.
/// </para>
/// <para>
/// Cells are typed rather than written as text. Numbers are numbers and dates are dates, so a
/// reader can sum and sort an export without converting it first -- the reason to ask for .xlsx
/// over .csv at all. Strings are written as text cells, which a spreadsheet displays and never
/// evaluates, so the formula-injection risk <see cref="FormulaSafeStringConverter"/> exists for in
/// CSV does not arise here and no apostrophe is added: the cell holds exactly the stored value.
/// </para>
/// <para>
/// An <c>ObjectId</c> is written as its 24-character hex string, as <see cref="ObjectIdConverter"/>
/// does for CSV; without it the id would be reflected into its internals or lost. A
/// <see cref="DateTime"/> whose Kind is Local is converted to UTC first, since the framework stores
/// UTC and the cell carries no zone of its own.
/// </para>
/// <para>
/// The workbook is built in memory before it is written, unlike the streaming CSV exporter. The
/// callers that exist -- report exports -- are clamped to a few thousand rows, where that is a few
/// megabytes. A caller exporting an unbounded set should use CSV.
/// </para>
/// </remarks>
public sealed class ExcelDataExporter<TIn>
{
    private static readonly PropertyInfo[] Columns = typeof(TIn)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
        .OrderBy(p => p.MetadataToken)
        .ToArray();

    // Excel's limit on a worksheet name, and the characters it refuses in one.
    private const int MaxSheetName = 31;
    private static readonly char[] SheetNameInvalid = ['[', ']', ':', '*', '?', '/', '\\'];

    /// <summary>
    /// Reads records from <paramref name="dataStream"/> and writes the workbook to
    /// <paramref name="outputStream"/>, which is left open.
    /// </summary>
    public async Task ExportAsync(IAsyncEnumerable<TIn> dataStream, Stream outputStream, CancellationToken ct = default)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SheetName());

        for (var c = 0; c < Columns.Length; c++)
        {
            sheet.Cell(1, c + 1).SetValue(Columns[c].Name);
        }

        var header = sheet.Row(1);
        header.Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);

        var row = 1;
        await foreach (var item in dataStream.WithCancellation(ct))
        {
            row++;
            for (var c = 0; c < Columns.Length; c++)
            {
                Write(sheet.Cell(row, c + 1), Columns[c].GetValue(item));
            }
        }

        // Sized from the first rows only: measuring every row of a large export costs far more than
        // a column that is a little narrow further down.
        if (Columns.Length > 0)
        {
            sheet.Columns(1, Columns.Length).AdjustToContents(1, Math.Min(row, 200));
        }

        workbook.SaveAs(outputStream);
    }

    private static string SheetName()
    {
        var name = new string(typeof(TIn).Name.Where(ch => Array.IndexOf(SheetNameInvalid, ch) < 0).ToArray());
        if (name.Length == 0) name = "Export";
        return name.Length > MaxSheetName ? name[..MaxSheetName] : name;
    }

    private static void Write(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                return;
            // A non-nullable date that was never set; written, it is a real-looking date in year 1.
            case DateTime unset when unset == DateTime.MinValue:
                return;
            case DateTimeOffset unset when unset == DateTimeOffset.MinValue:
                return;
            case string text:
                cell.SetValue(text);
                return;
            case bool flag:
                cell.SetValue(flag);
                return;
            case DateTime date:
                var utc = date.Kind == DateTimeKind.Local ? date.ToUniversalTime() : date;
                cell.SetValue(utc);
                cell.Style.DateFormat.Format = utc.TimeOfDay == TimeSpan.Zero ? "yyyy-mm-dd" : "yyyy-mm-dd hh:mm:ss";
                return;
            case DateTimeOffset offset:
                cell.SetValue(offset.UtcDateTime);
                cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
                return;
            case Enum member:
                cell.SetValue(member.ToString());
                return;
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                cell.SetValue(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                return;
            default:
                // ObjectId, Guid and anything else with a meaningful string form.
                cell.SetValue(value.ToString());
                return;
        }
    }
}
