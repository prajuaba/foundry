using System.Text;
using Foundry.FileIO;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MongoDB.Bson;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Foundry.FileIO.Tests;

/// <summary>
/// <see cref="PdfDataExporter{TIn}"/>: the table it builds, and the file it renders.
/// </summary>
/// <remarks>
/// Rendered PDF text is glyph-encoded, so reading it back needs a text extractor this repository
/// does not have. The content is therefore asserted on the document model the exporter builds --
/// deterministic and exactly what is rendered -- and the rendering on the bytes: a PDF that
/// PDFsharp can reopen, the page count, and the font actually embedded.
/// </remarks>
public class PdfExportTests
{
    public enum Health { Green, Amber, Red }

    public sealed class Row
    {
        public string ProjectCode { get; set; } = string.Empty;
        public int Count { get; set; }
        public decimal Budget { get; set; }
        public DateTime StartDate { get; set; }
        public Health ScheduleVarianceState { get; set; }
        public bool IsBillable { get; set; }
        public ObjectId Id { get; set; }
        public decimal? Maybe { get; set; }
    }

    private static readonly DateTime Generated = new(2026, 9, 27, 14, 30, 0, DateTimeKind.Utc);

    private static Row Sample(int i = 1) => new()
    {
        ProjectCode = $"P{i}", Count = 1200, Budget = 250000.5m,
        StartDate = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc),
        ScheduleVarianceState = Health.Amber, IsBillable = true, Id = ObjectId.Parse("65f0a1b2c3d4e5f607182930")
    };

    private static Table TableOf(Document document)
        => document.Sections[0]!.Elements.OfType<Table>().Single();

    private static string Text(Cell cell)
        => string.Concat(cell.Elements.OfType<Paragraph>().SelectMany(p => p.Elements.OfType<MigraDoc.DocumentObjectModel.Text>()).Select(t => t.Content));

    private static async IAsyncEnumerable<T> Stream<T>(IEnumerable<T> items)
    {
        await Task.Yield();
        foreach (var item in items) yield return item;
    }

    private static async Task<byte[]> Render(string title, IEnumerable<Row> rows)
    {
        using var output = new MemoryStream();
        await new PdfDataExporter<Row>(title).ExportAsync(Stream(rows), output);
        return output.ToArray();
    }

    [Fact]
    public void TheHeaderIsThePropertyNamesAsWordsAndRepeatsOnEveryPage()
    {
        var table = TableOf(new PdfDataExporter<Row>("Project Health").BuildDocument([Sample()], Generated));
        var header = table.Rows[0];

        Assert.True(header.HeadingFormat);
        Assert.Equal(
            new[] { "Project Code", "Count", "Budget", "Start Date", "Schedule Variance State", "Is Billable", "Id", "Maybe" },
            Enumerable.Range(0, table.Columns.Count).Select(c => Text(header.Cells[c])));
    }

    [Fact]
    public void ValuesAreFormattedForReadingAndNumbersAlignRight()
    {
        var table = TableOf(new PdfDataExporter<Row>("Project Health").BuildDocument([Sample()], Generated));
        var row = table.Rows[1];

        Assert.Equal(
            new[] { "P1", "1,200", "250,000.5", "2026-03-02", "Amber", "Yes", "65f0a1b2c3d4e5f607182930", "" },
            Enumerable.Range(0, table.Columns.Count).Select(c => Text(row.Cells[c])));
        Assert.Equal(ParagraphAlignment.Right, table.Columns[2].Format.Alignment);   // Budget
        Assert.Equal(ParagraphAlignment.Right, table.Columns[7].Format.Alignment);   // decimal?
        Assert.Equal(ParagraphAlignment.Left, table.Columns[0].Format.Alignment);    // text
    }

    [Fact]
    public void TheTitleAndGenerationTimeHeadThePage()
    {
        var document = new PdfDataExporter<Row>("Project Health").BuildDocument([Sample(), Sample(2)], Generated);
        var paragraphs = document.Sections[0]!.Elements.OfType<Paragraph>()
            .Select(p => string.Concat(p.Elements.OfType<MigraDoc.DocumentObjectModel.Text>().Select(t => t.Content)))
            .ToList();

        Assert.Equal("Project Health", paragraphs[0]);
        Assert.Equal("Generated 2026-09-27 14:30 UTC · 2 rows", paragraphs[1]);
        Assert.Equal("Project Health", document.Info.Title);
    }

    [Fact]
    public void ALocalTimeIsWrittenAsTheUtcInstantItNames()
    {
        var local = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Local);

        Assert.Equal(local.ToUniversalTime().ToString("yyyy-MM-dd HH:mm"), PdfDataExporter<Row>.Format(local));
    }

    [Fact]
    public async Task ItRendersAPdfThatOpensWithOnePagePerScreenfulOfRows()
    {
        var one = await Render("Project Health", [Sample()]);
        var many = await Render("Project Health", Enumerable.Range(1, 200).Select(Sample));

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(one, 0, 5));
        using var small = PdfReader.Open(new MemoryStream(one), PdfDocumentOpenMode.Import);
        using var large = PdfReader.Open(new MemoryStream(many), PdfDocumentOpenMode.Import);
        Assert.Equal(1, small.PageCount);
        Assert.True(large.PageCount > 1, $"200 rows should span pages, got {large.PageCount}");
    }

    [Fact]
    public async Task ThePageIsLandscapeSoEveryColumnFits()
    {
        // The first render was portrait: Orientation was set on a page setup that already carried
        // explicit A4 portrait dimensions, so it was ignored, and the last two columns ran off the
        // page. Nothing but the rendered page size shows it.
        var bytes = await Render("Project Health", [Sample()]);

        using var pdf = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        var page = pdf.Pages[0];
        Assert.True(page.Width.Point > page.Height.Point,
            $"expected landscape, got {page.Width.Point} x {page.Height.Point} pt");
    }

    [Fact]
    public async Task TheEmbeddedFontIsInTheFileSoItRendersWithoutSystemFonts()
    {
        var bytes = await Render("Project Health", [Sample()]);

        // Font dictionaries are not compressed, so the embedded face's name is visible in the bytes --
        // written the PDF way, with the space escaped as #20 behind a subset prefix.
        Assert.Contains("/FontFile2", Encoding.ASCII.GetString(bytes));
        Assert.Matches(@"/FontName/[A-Z]{6}\+DejaVu#20Sans", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public async Task NoRowsIsATitledPageWithAHeaderOnlyTable()
    {
        var bytes = await Render("Empty Report", []);

        using var pdf = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        Assert.Equal(1, pdf.PageCount);
        Assert.Equal(1, TableOf(new PdfDataExporter<Row>("Empty Report").BuildDocument([], Generated)).Rows.Count);
    }

    [Fact]
    public void ADateThatWasNeverSetIsBlankNotYearOne()
    {
        // A project with no baseline printed "0001-01-01" in Baseline End Date.
        Assert.Equal(string.Empty, PdfDataExporter<Row>.Format(DateTime.MinValue));
    }

    public sealed class Wide
    {
        public int A1 { get; set; } public int A2 { get; set; } public int A3 { get; set; } public int A4 { get; set; }
        public int A5 { get; set; } public int A6 { get; set; } public int A7 { get; set; } public int A8 { get; set; }
        public int A9 { get; set; } public int A10 { get; set; } public int A11 { get; set; } public int A12 { get; set; }
        public int A13 { get; set; } public int A14 { get; set; } public int A15 { get; set; } public int A16 { get; set; }
    }

    [Fact]
    public void AReportWiderThanFifteenColumnsIsSetSmallEnoughForADateToFitItsColumn()
    {
        var wide = new PdfDataExporter<Wide>("Wide").BuildDocument([new Wide()], Generated);
        var narrow = new PdfDataExporter<Row>("Narrow").BuildDocument([Sample()], Generated);

        Assert.Equal(6, wide.Styles[StyleNames.Normal]!.Font.Size.Point);
        Assert.Equal(8, narrow.Styles[StyleNames.Normal]!.Font.Size.Point);
    }

    public sealed class Mixed
    {
        public int N { get; set; }
        public string State { get; set; } = string.Empty;
    }

    [Fact]
    public void AColumnOfLongUnbreakableWordsIsWiderThanAColumnOfSmallNumbers()
    {
        // In a real 17-column report "NoPlannedEffort" ran past the table's edge: MigraDoc breaks
        // only at spaces, and every column was the same width however long its words were.
        var table = TableOf(new PdfDataExporter<Mixed>("Mixed").BuildDocument(
            [new Mixed { N = 1, State = "NoPlannedEffortRecordedAnywhere" }], Generated));

        Assert.True(table.Columns[1].Width.Point > table.Columns[0].Width.Point * 2,
            $"{table.Columns[1].Width.Point} vs {table.Columns[0].Width.Point}");
        Assert.Equal(Unit.FromCentimeter(26.7).Point, table.Columns[0].Width.Point + table.Columns[1].Width.Point, 1);
    }

    [Theory]
    [InlineData("ScheduleVarianceDays", "Schedule Variance Days")]
    [InlineData("HTMLParser", "HTML Parser")]
    [InlineData("Id", "Id")]
    [InlineData("ResourceId", "Resource Id")]
    public void PascalCaseNamesSplitIntoWords(string name, string words)
        => Assert.Equal(words, PdfDataExporter<Row>.Words(name));
}
