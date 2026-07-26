using Foundry.FileIO;
using Xunit;

namespace Foundry.FileIO.Tests;

/// <summary>
/// Cell-to-property conversion used by <see cref="ExcelDataParser{TOut}"/>.
/// </summary>
/// <remarks>
/// Tested directly rather than through a real workbook: this is where the defect was, and building a
/// valid .xlsx in a test would exercise ExcelDataReader rather than Foundry. The end-to-end path is
/// covered by the integration suite.
/// </remarks>
public class ExcelConversionTests
{
    private enum Grade
    {
        Standard,
        Premium
    }

    private sealed class Row
    {
        public string Name { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public int Quantity { get; set; }
        public DateTime Due { get; set; }
        public Guid Reference { get; set; }
        public Grade Grade { get; set; }
    }

    private static bool Convert(object value, Type target, out object? converted)
        => ExcelDataParser<Row>.TryConvert(value, target, out converted);

    // ---- values that should convert ----

    [Fact]
    public void AnAlreadyCorrectlyTypedValuePassesThrough()
    {
        Assert.True(Convert(42m, typeof(decimal), out var converted));
        Assert.Equal(42m, converted);
    }

    [Fact]
    public void ADoubleFromASpreadsheetBecomesADecimal()
    {
        // ExcelDataReader hands back numeric cells as double, so this is the common path.
        Assert.True(Convert(42.5d, typeof(decimal), out var converted));
        Assert.Equal(42.5m, converted);
    }

    [Fact]
    public void ANumericStringConverts()
    {
        Assert.True(Convert("42.5", typeof(decimal), out var converted));
        Assert.Equal(42.5m, converted);
    }

    [Fact]
    public void ANumericStringIsParsedInvariantly()
    {
        // Convert.ChangeType without an explicit culture reads the ambient one, so the same
        // spreadsheet imported on a de-DE server turned 42.5 into 425.
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("de-DE");

            Assert.True(Convert("42.5", typeof(decimal), out var converted));
            Assert.Equal(42.5m, converted);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ADateValuePassesThrough()
    {
        var date = new DateTime(2026, 7, 26);
        Assert.True(Convert(date, typeof(DateTime), out var converted));
        Assert.Equal(date, converted);
    }

    [Fact]
    public void AnIsoDateStringConverts()
    {
        Assert.True(Convert("2026-07-26", typeof(DateTime), out var converted));
        Assert.Equal(new DateTime(2026, 7, 26), converted);
    }

    [Fact]
    public void AGuidStringConverts()
    {
        var id = Guid.NewGuid();
        Assert.True(Convert(id.ToString(), typeof(Guid), out var converted));
        Assert.Equal(id, converted);
    }

    [Fact]
    public void AnEnumNameConverts()
    {
        // Previously unreachable: Convert.ChangeType cannot parse an enum name, and the fallback did
        // not handle enums either, so every enum column silently imported as its zero value.
        Assert.True(Convert("Premium", typeof(Grade), out var converted));
        Assert.Equal(Grade.Premium, converted);
    }

    [Fact]
    public void AnEnumNameIsCaseInsensitive()
    {
        Assert.True(Convert("premium", typeof(Grade), out var converted));
        Assert.Equal(Grade.Premium, converted);
    }

    // ---- values that should not convert ----

    [Theory]
    [InlineData("1,234.00 USD")]
    [InlineData("N/A")]
    [InlineData("twelve")]
    [InlineData("")]
    public void AnUnconvertibleValueIsReportedRatherThanSubstituted(string value)
    {
        // The core defect. These used to be swallowed, leaving the property at 0 -- so a row with
        // "1,234.00 USD" imported as an amount of zero and the import reported success.
        Assert.False(Convert(value, typeof(decimal), out var converted));
        Assert.Null(converted);
    }

    [Fact]
    public void AnOutOfRangeNumberIsReported()
    {
        Assert.False(Convert("999999999999999999999999", typeof(int), out _));
    }

    [Fact]
    public void AnUnknownEnumNameIsReported()
    {
        Assert.False(Convert("Platinum", typeof(Grade), out _));
    }

    [Fact]
    public void AMalformedGuidIsReported()
    {
        Assert.False(Convert("not-a-guid", typeof(Guid), out _));
    }

    [Fact]
    public void AMalformedDateIsReported()
    {
        Assert.False(Convert("the 32nd of Maytember", typeof(DateTime), out _));
    }

    // ---- error reporting shape ----

    [Fact]
    public void ACellErrorDescribesItselfUsefully()
    {
        var error = new ExcelCellError(7, "Amount", "1,234.00 USD", typeof(decimal));
        var text = error.ToString();

        Assert.Contains("7", text);
        Assert.Contains("Amount", text);
        Assert.Contains("1,234.00 USD", text);
        Assert.Contains("Decimal", text);
    }

    [Fact]
    public void LenientModeIsOptIn()
    {
        // The default has to be the loud one: an import that quietly substitutes defaults returns the
        // row count the user expected and data they cannot rely on.
        Assert.False(new ExcelDataParser<Row>().SkipUnconvertibleValues);
        Assert.True(new ExcelDataParser<Row> { SkipUnconvertibleValues = true }.SkipUnconvertibleValues);
    }

    [Fact]
    public void ANewParserHasNoRecordedErrors()
    {
        Assert.Empty(new ExcelDataParser<Row>().UnconvertibleValues);
    }
}
