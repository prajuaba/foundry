using System.IO.Compression;
using System.Text;
using Foundry.FileIO;
using Xunit;

namespace Foundry.FileIO.Tests;

/// <summary>
/// <see cref="ExcelDataParser{TOut}"/> against real workbooks.
/// </summary>
/// <remarks>
/// <para>
/// <c>ParseAsync</c> had never been executed by any test. The cell-conversion suite says the
/// end-to-end path "is covered by the integration suite"; it is not — the integration test with that
/// shape drives the <em>CSV</em> parser. The comment made the gap look deliberate, which is why it
/// lasted.
/// </para>
/// <para>
/// Workbooks are built here rather than committed as fixtures so the input to each test is visible
/// beside its assertion. A minimal .xlsx is a zip of five XML parts, and using inline strings avoids
/// the shared-string table.
/// </para>
/// </remarks>
public class ExcelWorkbookTests
{
    private sealed class Row
    {
        public string Name { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Amount { get; set; }
    }

    /// <summary>Builds a single-sheet workbook from rows of cell text.</summary>
    private static Stream Workbook(params string[][] rows)
    {
        var sheet = new StringBuilder();
        sheet.Append("""<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");

        for (var r = 0; r < rows.Length; r++)
        {
            sheet.Append($"<row r=\"{r + 1}\">");
            for (var c = 0; c < rows[r].Length; c++)
            {
                var reference = $"{(char)('A' + c)}{r + 1}";
                var text = System.Security.SecurityElement.Escape(rows[r][c]);
                sheet.Append($"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{text}</t></is></c>");
            }
            sheet.Append("</row>");
        }

        sheet.Append("</sheetData></worksheet>");

        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>""");

            Write(archive, "_rels/.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");

            Write(archive, "xl/workbook.xml", """<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>""");

            Write(archive, "xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");

            Write(archive, "xl/worksheets/sheet1.xml", sheet.ToString());
        }

        buffer.Position = 0;
        return buffer;
    }

    private static void Write(ZipArchive archive, string path, string content)
    {
        using var stream = archive.CreateEntry(path).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static async Task<List<Row>> ParseAsync(Stream workbook, ExcelDataParser<Row>? parser = null)
    {
        var rows = new List<Row>();
        await foreach (var row in (parser ?? new ExcelDataParser<Row>()).ParseAsync(workbook))
        {
            rows.Add(row);
        }
        return rows;
    }

    // ── The path works at all ───────────────────────────────────────────────

    [Fact]
    public async Task AWorkbookIsParsedIntoTypedRows()
    {
        var rows = await ParseAsync(Workbook(
            ["Name", "Quantity", "Amount"],
            ["Widget", "3", "42.50"],
            ["Gadget", "1", "9.99"]));

        Assert.Equal(2, rows.Count);
        Assert.Equal("Widget", rows[0].Name);
        Assert.Equal(3, rows[0].Quantity);
        Assert.Equal(42.50m, rows[0].Amount);
        Assert.Equal("Gadget", rows[1].Name);
    }

    [Fact]
    public async Task HeaderMatchingIsCaseInsensitive()
    {
        var rows = await ParseAsync(Workbook(
            ["name", "QUANTITY", "AmOuNt"],
            ["Widget", "3", "42.50"]));

        Assert.Equal("Widget", Assert.Single(rows).Name);
    }

    [Fact]
    public async Task SurroundingWhitespaceInAHeaderIsIgnored()
    {
        // Spreadsheets authored by hand routinely carry it, and a header that fails to match leaves
        // the whole column silently unread.
        var rows = await ParseAsync(Workbook(
            ["  Name  ", "Quantity", "Amount"],
            ["Widget", "3", "42.50"]));

        Assert.Equal("Widget", Assert.Single(rows).Name);
    }

    [Fact]
    public async Task AColumnTheModelDoesNotDeclareIsIgnored()
    {
        var rows = await ParseAsync(Workbook(
            ["Name", "Quantity", "Amount", "InternalNotes"],
            ["Widget", "3", "42.50", "ignore me"]));

        Assert.Equal("Widget", Assert.Single(rows).Name);
    }

    // ── A column the file does not supply ───────────────────────────────────

    [Fact]
    public async Task AMissingColumnLeavesEveryRowAtTheDefault()
    {
        // Documented because it is the behaviour, and it is the shape this codebase keeps producing:
        // the row count is exactly what the user expected and the data is wrong.
        var rows = await ParseAsync(Workbook(
            ["Name", "Amount"],
            ["Widget", "42.50"]));

        var row = Assert.Single(rows);
        Assert.Equal("Widget", row.Name);
        Assert.Equal(0, row.Quantity);
    }

    [Fact]
    public async Task AMissingColumnIsReportedSoACallerCanRefuseTheImport()
    {
        // The parser cannot decide that an absent column is an error -- plenty of imports fill only
        // some fields. It can refuse to be silent about it.
        var parser = new ExcelDataParser<Row>();

        await ParseAsync(Workbook(["Name", "Amount"], ["Widget", "42.50"]), parser);

        Assert.Equal("Quantity", Assert.Single(parser.UnmatchedProperties));
    }

    [Fact]
    public async Task AFileSupplyingEveryColumnReportsNothingUnmatched()
    {
        var parser = new ExcelDataParser<Row>();

        await ParseAsync(Workbook(["Name", "Quantity", "Amount"], ["Widget", "3", "42.50"]), parser);

        Assert.Empty(parser.UnmatchedProperties);
    }

    [Fact]
    public async Task AnEmptyWorkbookYieldsNoRows()
    {
        Assert.Empty(await ParseAsync(Workbook()));
    }

    [Fact]
    public async Task AHeaderRowWithNoDataYieldsNoRows()
    {
        Assert.Empty(await ParseAsync(Workbook(["Name", "Quantity", "Amount"])));
    }

    // ── Input that is not a workbook ────────────────────────────────────────

    [Fact]
    public async Task AStreamThatIsNotASpreadsheetIsRejected()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Name,Quantity\nWidget,3"));

        await Assert.ThrowsAnyAsync<Exception>(() => ParseAsync(stream));
    }

    // ── Conversion failures, end to end ─────────────────────────────────────

    [Fact]
    public async Task AnUnconvertibleCellFailsTheImportAndNamesTheRow()
    {
        // The row number is what makes a failed import actionable, and it counts the header as row 1
        // so it matches what the user sees in the spreadsheet.
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ParseAsync(Workbook(
            ["Name", "Quantity", "Amount"],
            ["Widget", "3", "42.50"],
            ["Gadget", "not-a-number", "9.99"])));

        Assert.Contains("Row 3", error.Message);
        Assert.Contains("Quantity", error.Message);
        Assert.Contains("not-a-number", error.Message);
    }

    [Fact]
    public async Task SkippingUnconvertibleValuesCollectsThemInsteadOfFailing()
    {
        var parser = new ExcelDataParser<Row> { SkipUnconvertibleValues = true };

        var rows = await ParseAsync(Workbook(
            ["Name", "Quantity", "Amount"],
            ["Widget", "not-a-number", "42.50"]), parser);

        var row = Assert.Single(rows);
        Assert.Equal("Widget", row.Name);
        Assert.Equal(0, row.Quantity);
        Assert.Equal(42.50m, row.Amount);

        var failure = Assert.Single(parser.UnconvertibleValues);
        Assert.Equal(2, failure.Row);
        Assert.Equal("Quantity", failure.PropertyName);
    }
}
