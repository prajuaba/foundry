using System;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.TypeConversion;
using MongoDB.Bson;

namespace Foundry.FileIO;

/// <summary>
/// Converts <see cref="ObjectId"/> to and from its 24-character lowercase hex string representation.
/// </summary>
/// <remarks>
/// <para>
/// Without this converter, CsvHelper has no registered converter for <see cref="ObjectId"/>, so it
/// treats it as a complex object and auto-maps its public members (<c>Timestamp</c>,
/// <c>CreationTime</c>, etc.) as separate columns in place of the id value itself. The result is a
/// header like <c>Timestamp,CreationTime</c> where the property's own name should have appeared, and
/// the id itself is absent from the file. A type with two <see cref="ObjectId"/> properties produces
/// two identically-named pairs of columns, which is worse: the header is ambiguous and neither id can
/// be recovered.
/// </para>
/// <para>
/// Registered as a global converter (<c>AddConverter&lt;ObjectId&gt;</c>) the same way
/// <see cref="FormulaSafeStringConverter"/> is registered as <c>AddConverter&lt;string&gt;</c>, so it
/// cannot be forgotten per field, and every <see cref="ObjectId"/> property is written under its own
/// property name as a single column.
/// </para>
/// </remarks>
public sealed class ObjectIdConverter : DefaultTypeConverter
{
    /// <inheritdoc />
    public override string? ConvertToString(object? value, IWriterRow row, MemberMapData memberMapData)
    {
        return value is ObjectId objectId ? objectId.ToString() : base.ConvertToString(value, row, memberMapData);
    }

    /// <inheritdoc />
    public override object? ConvertFromString(string? text, IReaderRow row, MemberMapData memberMapData)
    {
        if (string.IsNullOrEmpty(text))
        {
            // An empty CSV cell has no id to parse. Returning ObjectId.Empty (the all-zero default)
            // rather than throwing lets it round-trip like other optional fields degrade in this
            // library (see CsvDataParser's MissingFieldFound = null), instead of failing the whole row.
            return ObjectId.Empty;
        }

        // A malformed value is left to CsvHelper's normal FormatException-wrapping behaviour rather
        // than swallowed here -- a corrupt id in an import file should surface as an error, not
        // silently become ObjectId.Empty.
        return ObjectId.Parse(text);
    }
}
