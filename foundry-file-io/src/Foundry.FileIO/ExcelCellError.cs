using System;

namespace Foundry.FileIO;

/// <summary>
/// A cell whose value could not be converted to the target property's type.
/// </summary>
/// <param name="Row">1-based row number in the sheet, counting the header as row 1.</param>
/// <param name="PropertyName">Name of the destination property.</param>
/// <param name="Value">The cell's value as text.</param>
/// <param name="TargetType">The type conversion was attempted to.</param>
/// <remarks>
/// Exists so that a lenient import can still tell the caller exactly what it dropped. The previous
/// behaviour discarded this information entirely, leaving the property at its default with no record
/// that anything had gone wrong.
/// </remarks>
public sealed record ExcelCellError(int Row, string PropertyName, string? Value, Type TargetType)
{
    /// <inheritdoc />
    public override string ToString()
        => $"Row {Row}, column '{PropertyName}': cannot convert '{Value}' to {TargetType.Name}.";
}
