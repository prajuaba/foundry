using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace Foundry.FileIO;

/// <summary>
/// Writes rows to a PDF: a titled, landscape A4 table with one column per property.
/// </summary>
/// <remarks>
/// <para>
/// The same column rules as <see cref="CsvDataExporter{TIn}"/> and <see cref="ExcelDataExporter{TIn}"/>
/// -- public readable properties in declaration order -- so a report reads the same in all three.
/// Headers are the property names split into words (<c>ScheduleVarianceDays</c> reads "Schedule
/// Variance Days"), because a PDF is read, not parsed: MigraDoc breaks lines only at spaces, and a
/// long PascalCase name would otherwise run out of its column.
/// </para>
/// <para>
/// Values are formatted as the .xlsx cells are: numbers right-aligned with grouping, dates as
/// <c>yyyy-MM-dd</c> in UTC (a Local-kind value is converted first), enums by name, an ObjectId as
/// its hex string, null as an empty cell. The header row repeats on every page.
/// </para>
/// <para>
/// Text is set in DejaVu Sans, embedded in this assembly, so a PDF renders identically on a
/// developer machine and in a container with no fonts at all. It is registered as PDFsharp's
/// fallback font resolver: a host that registers its own resolver keeps it, and this one answers
/// only what that one does not. DejaVu Sans covers Latin, Greek and Cyrillic; it has no Thai or CJK
/// glyphs, which render as empty boxes.
/// </para>
/// <para>
/// The document is built in memory, as the .xlsx one is. Callers today are report exports clamped
/// to a few thousand rows.
/// </para>
/// </remarks>
public sealed class PdfDataExporter<TIn>
{
    internal const string FontFamily = "DejaVu Sans";

    private static readonly PropertyInfo[] Columns = typeof(TIn)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
        .OrderBy(p => p.MetadataToken)
        .ToArray();

    private readonly string _title;

    /// <param name="title">Printed at the top of the first page and set as the document title.</param>
    public PdfDataExporter(string title)
    {
        _title = string.IsNullOrWhiteSpace(title) ? typeof(TIn).Name : title;
    }

    /// <summary>
    /// Reads records from <paramref name="dataStream"/> and writes the PDF to
    /// <paramref name="outputStream"/>, which is left open.
    /// </summary>
    public async Task ExportAsync(IAsyncEnumerable<TIn> dataStream, Stream outputStream, CancellationToken ct = default)
    {
        var rows = new List<TIn>();
        await foreach (var item in dataStream.WithCancellation(ct))
        {
            rows.Add(item);
        }

        EmbeddedFontResolver.EnsureRegistered();

        var renderer = new PdfDocumentRenderer { Document = BuildDocument(rows, DateTime.UtcNow) };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(outputStream, false);
    }

    /// <summary>The document before rendering: what the tests read, since rendered text is glyph-encoded.</summary>
    internal Document BuildDocument(IReadOnlyList<TIn> rows, DateTime generatedAtUtc)
    {
        var document = new Document();
        document.Info.Title = _title;
        document.Styles[StyleNames.Normal]!.Font.Name = FontFamily;

        // Narrower type for wide reports, so a 17-column row stays legible rather than one word per line.
        var fontSize = Columns.Length > 12 ? 6.5 : 8;
        document.Styles[StyleNames.Normal]!.Font.Size = fontSize;

        var section = document.AddSection();
        section.PageSetup = document.DefaultPageSetup.Clone();
        // Landscape A4 by its dimensions. Setting Orientation on a page setup that already carries
        // explicit A4 portrait sizes is ignored, and the first render came out portrait with the
        // last columns off the page.
        section.PageSetup.PageWidth = Unit.FromCentimeter(29.7);
        section.PageSetup.PageHeight = Unit.FromCentimeter(21.0);
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(1.5);
        section.PageSetup.TopMargin = section.PageSetup.BottomMargin = Unit.FromCentimeter(1.5);

        var title = section.AddParagraph(_title);
        title.Format.Font.Size = 13;
        title.Format.Font.Bold = true;

        var subtitle = section.AddParagraph(
            $"Generated {generatedAtUtc:yyyy-MM-dd HH:mm} UTC · {rows.Count} row{(rows.Count == 1 ? "" : "s")}");
        subtitle.Format.Font.Size = 8;
        subtitle.Format.SpaceAfter = Unit.FromPoint(8);

        if (Columns.Length == 0) return document;

        var table = section.AddTable();
        table.Borders.Width = 0.25;
        table.Borders.Color = Colors.LightGray;
        table.Format.Font.Size = fontSize;

        // A4 landscape is 29.7 cm wide; the margins take 3.
        var width = Unit.FromCentimeter(26.7 / Columns.Length);
        foreach (var property in Columns)
        {
            var column = table.AddColumn(width);
            column.Format.Alignment = IsNumeric(property.PropertyType) ? ParagraphAlignment.Right : ParagraphAlignment.Left;
        }

        var header = table.AddRow();
        header.HeadingFormat = true;
        header.Format.Font.Bold = true;
        header.Shading.Color = Colors.WhiteSmoke;
        for (var c = 0; c < Columns.Length; c++)
        {
            header.Cells[c].AddParagraph(Words(Columns[c].Name));
        }

        foreach (var item in rows)
        {
            var row = table.AddRow();
            for (var c = 0; c < Columns.Length; c++)
            {
                row.Cells[c].AddParagraph(Format(Columns[c].GetValue(item)));
            }
        }

        return document;
    }

    internal static string Words(string pascalCase)
    {
        var words = new StringBuilder(pascalCase.Length + 8);
        for (var i = 0; i < pascalCase.Length; i++)
        {
            var ch = pascalCase[i];
            var boundary = i > 0 && char.IsUpper(ch)
                && (char.IsLower(pascalCase[i - 1]) || (i + 1 < pascalCase.Length && char.IsLower(pascalCase[i + 1])));
            if (boundary) words.Append(' ');
            words.Append(ch);
        }
        return words.ToString();
    }

    internal static string Format(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        bool flag => flag ? "Yes" : "No",
        DateTime date => (date.Kind == DateTimeKind.Local ? date.ToUniversalTime() : date) is var utc
            && utc.TimeOfDay == TimeSpan.Zero
                ? utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        Enum member => member.ToString(),
        byte or sbyte or short or ushort or int or uint or long or ulong
            => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString("#,##0", CultureInfo.InvariantCulture),
        float or double or decimal
            => Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString("#,##0.##", CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static bool IsNumeric(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
            || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong)
            || t == typeof(float) || t == typeof(double) || t == typeof(decimal);
    }
}

/// <summary>
/// Serves the embedded DejaVu Sans to PDFsharp for any family it cannot otherwise resolve.
/// </summary>
/// <remarks>
/// PDFsharp's resolvers are process-wide and may be set once, with the same instance thereafter.
/// Registered as the fallback, never the primary, and only when no fallback is set yet, so a host
/// that configured its own fonts is not overridden.
/// </remarks>
internal sealed class EmbeddedFontResolver : IFontResolver
{
    private const string Regular = "DejaVuSans";
    private const string Bold = "DejaVuSans-Bold";

    private static readonly EmbeddedFontResolver Instance = new();
    private static readonly object Gate = new();

    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            GlobalFontSettings.FallbackFontResolver ??= Instance;
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
        // No italic face is embedded; PDFsharp slants the regular one when asked.
        => new(bold ? Bold : Regular, mustSimulateBold: false, mustSimulateItalic: italic);

    public byte[]? GetFont(string faceName)
    {
        using var stream = typeof(EmbeddedFontResolver).Assembly
            .GetManifestResourceStream($"Foundry.FileIO.Fonts.{faceName}.ttf");
        if (stream is null) return null;

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
