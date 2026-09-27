using Xunit;
using Foundry.Schema.Compiler;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// The generated *FileService exports to each format it may import from.
/// </summary>
/// <remarks>
/// It exported CSV only, so a type allowed .xlsx could be read from a workbook and never written to
/// one -- "the CSV and XLSX file services are generated" held for import and not for export.
/// </remarks>
public class FileServiceExportTests
{
    private static string ServiceFor(params string[] extensions)
    {
        var schema = new SchemaModel
        {
            Namespace = "Sales.Domain",
            Dtos =
            [
                new DtoModel
                {
                    Name = "SalesRow",
                    FileIoEnabled = true,
                    FileIoAllowedExtensions = [.. extensions],
                    Properties = [new DtoProperty { Name = "Region", Type = "string" }]
                }
            ]
        };

        return PocoGenerator.Generate(schema)["Services/SalesRowFileService"];
    }

    [Fact]
    public void ATypeAllowedXlsxCanBeExportedToIt()
    {
        var service = ServiceFor(".csv", ".xlsx");

        Assert.Contains("private readonly ExcelDataExporter<SalesRow> _xlsxExporter = new();", service);
        Assert.Contains("public Task ExportToXlsxAsync(", service);
        Assert.Contains("=> _xlsxExporter.ExportAsync(items, outputStream, ct);", service);
        Assert.Contains("public Task ExportToCsvAsync(", service);
    }

    [Fact]
    public void ACsvOnlyTypeIsUnchanged()
    {
        var service = ServiceFor(".csv");

        Assert.DoesNotContain("ExcelDataExporter", service);
        Assert.DoesNotContain("ExportToXlsxAsync", service);
    }

    [Fact]
    public void TheDefaultExtensionsIncludeXlsxAndSoTheExport()
    {
        // No list declared falls back to .csv, .xlsx and .xls.
        Assert.Contains("ExportToXlsxAsync", ServiceFor());
    }
}
