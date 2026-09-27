using System.Collections.Generic;
using System.Text.Json;
using Xunit;
using Foundry.Schema.Compiler;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// Guards the behavior of custom endpoints whose response is a file download rather than JSON,
/// declared via <see cref="CustomEndpoint.ResponseMediaType"/>.
/// </summary>
public class CustomEndpointResponseMediaTypeTests
{
    // A schema with zero entities is refused outright (FDY1002), independent of anything a
    // custom endpoint declares, so every case here needs one unrelated entity present to isolate
    // the responseMediaType behaviour under test.
    private static SchemaModel SchemaWith(CustomEndpoint endpoint) => new()
    {
        Namespace = "Sales.Contracts",
        Entities = new List<Entity>
        {
            new Entity
            {
                Name = "Placeholder",
                Properties = new List<Property>
                {
                    new Property { Name = "Id", Type = "ObjectId", IsKey = true }
                }
            }
        },
        CustomEndpoints = new List<CustomEndpoint> { endpoint }
    };

    [Fact]
    public void GetEndpoint_WithTextCsv_ValidatesWithNoErrors()
    {
        // If this regresses, a schema declaring the one supported export media type on a GET
        // endpoint would be refused, and every CSV export endpoint would fail to build.

        // Arrange
        var schema = SchemaWith(new CustomEndpoint
        {
            Route = "/api/v1/orders/export",
            Method = "GET",
            RequestType = "OrderExportQuery",
            OperationType = "Custom",
            ResponseMediaType = "text/csv"
        });

        // Act
        var diagnostics = SchemaValidator.Validate(schema);

        // Assert
        Assert.DoesNotContain(diagnostics.Items, d => d.Code == DiagnosticCatalog.EndpointUnsupportedResponseMediaType);
        Assert.False(diagnostics.HasErrors);
    }

    [Fact]
    public void GetEndpoint_WithXlsx_ValidatesWithNoErrors()
    {
        // Listed with ExcelDataExporter, which is what produces it. If this regresses, every .xlsx
        // export endpoint is refused at build time.
        var schema = SchemaWith(new CustomEndpoint
        {
            Route = "/api/v1/orders/export/xlsx",
            Method = "GET",
            RequestType = "OrderXlsxQuery",
            OperationType = "Custom",
            ResponseMediaType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        });

        var diagnostics = SchemaValidator.Validate(schema);

        Assert.DoesNotContain(diagnostics.Items, d => d.Code == DiagnosticCatalog.EndpointUnsupportedResponseMediaType);
        Assert.False(diagnostics.HasErrors);
    }

    [Fact]
    public void GetEndpoint_WithApplicationPdf_ProducesFDY2016()
    {
        // If this regresses, a schema could declare a media type no exporter in the framework can
        // produce, and the endpoint would build without ever generating the bytes it promises.

        // Arrange
        var schema = SchemaWith(new CustomEndpoint
        {
            Route = "/api/v1/orders/export",
            Method = "GET",
            RequestType = "OrderExportQuery",
            OperationType = "Custom",
            ResponseMediaType = "application/pdf"
        });

        // Act
        var diagnostics = SchemaValidator.Validate(schema);

        // Assert
        Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCatalog.EndpointUnsupportedResponseMediaType);
        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void PostEndpoint_WithTextCsv_ProducesFDY2016()
    {
        // If this regresses, a POST could declare a file response, but nothing in the request
        // pipeline can stream a file body back from a POST, so the endpoint would accept a schema
        // it cannot honor at runtime.

        // Arrange
        var schema = SchemaWith(new CustomEndpoint
        {
            Route = "/api/v1/orders/export",
            Method = "POST",
            RequestType = "OrderExportCommand",
            OperationType = "Custom",
            ResponseMediaType = "text/csv"
        });

        // Act
        var diagnostics = SchemaValidator.Validate(schema);

        // Assert
        Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCatalog.EndpointUnsupportedResponseMediaType);
        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void NoResponseMediaType_ValidatesCleanly_AndManifestOmitsTheKeyEntirely()
    {
        // If this regresses by emitting the key unconditionally, every checked-in api-manifest.json
        // in every consuming repository changes for a field it does not use, and their
        // manifest-verification CI fails on an unrelated schema change.

        // Arrange
        var schema = SchemaWith(new CustomEndpoint
        {
            Route = "/api/v1/orders/lookup",
            Method = "GET",
            RequestType = "OrderLookupQuery",
            OperationType = "Custom"
        });

        // Act
        var diagnostics = SchemaValidator.Validate(schema);
        var manifest = JsonDocument.Parse(ApiManifestGenerator.Generate(schema)).RootElement;
        var custom = Assert.Single(manifest.GetProperty("CustomEndpoints").EnumerateArray());

        // Assert
        Assert.False(diagnostics.HasErrors);
        Assert.False(custom.TryGetProperty("ResponseMediaType", out _));
    }

    [Fact]
    public void GetEndpoint_WithTextCsv_ManifestCarriesResponseMediaType()
    {
        // If this regresses, the manifest — the only channel between the compiler and the running
        // application — would never tell the runtime route generator to declare the endpoint's
        // content type, and every CSV export would be served as application/json instead.

        // Arrange
        var schema = SchemaWith(new CustomEndpoint
        {
            Route = "/api/v1/orders/export",
            Method = "GET",
            RequestType = "OrderExportQuery",
            OperationType = "Custom",
            ResponseMediaType = "text/csv"
        });

        // Act
        var manifest = JsonDocument.Parse(ApiManifestGenerator.Generate(schema)).RootElement;
        var custom = Assert.Single(manifest.GetProperty("CustomEndpoints").EnumerateArray());

        // Assert
        Assert.Equal("text/csv", custom.GetProperty("ResponseMediaType").GetString());
    }
}
