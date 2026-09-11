using System.Collections.Generic;
using Xunit;
using Foundry.Schema.Compiler;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// Guards the behavior of custom endpoints whose response shape differs from the target entity,
/// particularly for report-style endpoints that project into DTOs rather than returning raw entities.
/// </summary>
public class CustomEndpointResponseTypeTests
{
    [Fact]
    public void NoResponseType_GeneratesExactlyTodaysOutput_RegressionGuard()
    {
        // Arrange
        var ep = new CustomEndpoint
        {
            Route = "/api/v1/orders/lookup",
            Method = "GET",
            RequestType = "OrderLookupQuery",
            OperationType = "Query",
            TargetEntity = "Order",
            FilterField = "CustomerId",
            FilterSourceValue = "CustomerId"
        };

        // Act
        var code = TestHelpers.GenerateForSingleCustomEndpoint(ep, "Sales.Contracts");

        // Assert
        Assert.Contains("namespace Sales.Contracts.Handlers;", code);
        Assert.Contains("public class OrderLookupQueryHandler : IRequestHandler<OrderLookupQuery, System.Collections.Generic.IReadOnlyList<Order>>", code);

        var schema = new SchemaModel
        {
            Namespace = "Sales.Contracts",
            CustomEndpoints = new List<CustomEndpoint> { ep }
        };
        var result = PocoGenerator.Generate(schema);
        var requestCode = result[$"Commands/{ep.RequestType}"];

        Assert.Contains("IRequest<System.Collections.Generic.IReadOnlyList<Order>>", requestCode);
        Assert.Contains("var items = await _repository.FindManyAsync(", code);
        Assert.Contains("return items;", code);
        Assert.DoesNotContain("NotImplementedException", code);
    }

    [Fact]
    public void GetEndpoint_WithDtoResponseType_GeneratesListOfDtoRequestType()
    {
        // Arrange
        var ep = new CustomEndpoint
        {
            Route = "/api/v1/reports/utilization",
            Method = "GET",
            RequestType = "ResourceUtilizationQuery",
            OperationType = "Query",
            TargetEntity = "CapacitySnapshot",
            ResponseType = "ResourceUtilizationRow",
            FilterField = "TenantId",
            FilterSourceValue = "TenantId"
        };

        // Act
        var schema = new SchemaModel
        {
            Namespace = "Sales.Contracts",
            CustomEndpoints = new List<CustomEndpoint> { ep },
            Dtos = new List<DtoModel>
            {
                new DtoModel { Name = "ResourceUtilizationRow" }
            }
        };
        var result = PocoGenerator.Generate(schema);
        var requestCode = result[$"Commands/{ep.RequestType}"];

        // Assert
        Assert.Contains("IRequest<System.Collections.Generic.IReadOnlyList<ResourceUtilizationRow>>", requestCode);
        Assert.DoesNotContain("IReadOnlyList<CapacitySnapshot>", requestCode);
    }

    [Fact]
    public void GetEndpoint_WithDifferentResponseType_HandlerDoesNotReturnItemsAndThrowsNotImplemented()
    {
        // Arrange
        var ep = new CustomEndpoint
        {
            Route = "/api/v1/reports/utilization",
            Method = "GET",
            RequestType = "ResourceUtilizationQuery",
            OperationType = "Query",
            TargetEntity = "CapacitySnapshot",
            ResponseType = "ResourceUtilizationRow",
            FilterField = "TenantId",
            FilterSourceValue = "TenantId"
        };

        // Act
        var code = TestHelpers.GenerateForSingleCustomEndpoint(ep, "Sales.Contracts");

        // Assert
        Assert.Contains("namespace Sales.Contracts.Handlers;", code);
        Assert.Contains("public class ResourceUtilizationQueryHandler : IRequestHandler<ResourceUtilizationQuery, System.Collections.Generic.IReadOnlyList<ResourceUtilizationRow>>", code);

        Assert.DoesNotContain("        return items;", code);
        Assert.Contains("NotImplementedException", code);
        Assert.Contains("CapacitySnapshot", code);
        Assert.Contains("ResourceUtilizationRow", code);
    }

    [Fact]
    public void NonGetEndpoint_WithResponseType_ReturnsResponseTypeNotBool()
    {
        // Arrange
        var ep = new CustomEndpoint
        {
            Route = "/api/v1/reports/submit",
            Method = "POST",
            RequestType = "SubmitReportCommand",
            OperationType = "Custom",
            ResponseType = "ReportAcceptedResult"
        };

        // Act
        var code = TestHelpers.GenerateForSingleCustomEndpoint(ep, "Sales.Contracts");

        // Assert
        Assert.Contains("public class SubmitReportCommandHandler : IRequestHandler<SubmitReportCommand, ReportAcceptedResult>", code);
        Assert.DoesNotContain("IRequestHandler<SubmitReportCommand, bool>", code);
    }

    [Fact]
    public void ResponseType_NamingUndeclaredType_IsRefusedWithFDY2015()
    {
        // Arrange
        var schema = new SchemaModel
        {
            Namespace = "Sales.Contracts",
            Entities = new List<Entity>
            {
                new Entity
                {
                    Name = "Order",
                    Properties = new List<Property>
                    {
                        new Property
                        {
                            Name = "Id",
                            Type = "ObjectId",
                            IsKey = true
                        }
                    }
                }
            },
            CustomEndpoints = new List<CustomEndpoint>
            {
                new CustomEndpoint
                {
                    Route = "/api/v1/orders/report",
                    Method = "GET",
                    RequestType = "OrderReportQuery",
                    OperationType = "Query",
                    TargetEntity = "Order",
                    ResponseType = "NoSuchType"
                }
            }
        };

        // Act
        var diagnostics = SchemaValidator.Validate(schema);

        // Assert
        Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCatalog.EndpointUnknownResponseType);
        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void ResponseTypeEqualToTargetEntity_GeneratesIdenticalOutputToUnset()
    {
        // Arrange
        var endpointUnset = new CustomEndpoint
        {
            Route = "/api/v1/orders/lookup",
            Method = "GET",
            RequestType = "OrderLookupQueryA",
            OperationType = "Query",
            TargetEntity = "Order",
            FilterField = "CustomerId",
            FilterSourceValue = "CustomerId"
        };

        var endpointSet = new CustomEndpoint
        {
            Route = "/api/v1/orders/lookup",
            Method = "GET",
            RequestType = "OrderLookupQueryB",
            OperationType = "Query",
            TargetEntity = "Order",
            ResponseType = "Order", // Equal to TargetEntity
            FilterField = "CustomerId",
            FilterSourceValue = "CustomerId"
        };

        // Act
        var codeUnset = TestHelpers.GenerateForSingleCustomEndpoint(endpointUnset, "Sales.Contracts");
        var codeSet = TestHelpers.GenerateForSingleCustomEndpoint(endpointSet, "Sales.Contracts");

        // Assert
        Assert.Contains("var items = await _repository.FindManyAsync(", codeUnset);
        Assert.Contains("return items;", codeUnset);
        Assert.DoesNotContain("NotImplementedException", codeUnset);

        Assert.Contains("var items = await _repository.FindManyAsync(", codeSet);
        Assert.Contains("return items;", codeSet);
        Assert.DoesNotContain("NotImplementedException", codeSet);

        Assert.Contains("IRequestHandler<OrderLookupQueryA, System.Collections.Generic.IReadOnlyList<Order>>", codeUnset);
        Assert.Contains("IRequestHandler<OrderLookupQueryB, System.Collections.Generic.IReadOnlyList<Order>>", codeSet);
    }

    [Fact]
    public void UpdateEndpoint_WithDifferentResponseType_HandlerHasNoBooleanReturnsAndThrowsNotImplemented()
    {
        // Arrange
        var ep = new CustomEndpoint
        {
            Route = "/api/v1/widgets/resize",
            Method = "PUT",
            RequestType = "ResizeWidgetCommand",
            OperationType = "Update",
            TargetEntity = "Widget",
            ResponseType = "WidgetRow",
            FilterSourceValue = "Id",
            Assignments = new List<AssignmentRule>
            {
                new AssignmentRule { EntityProperty = "Size", SourceValue = "NewSize" }
            }
        };

        // Act
        var code = TestHelpers.GenerateForSingleCustomEndpoint(ep, "Sales.Contracts");

        // Assert
        Assert.DoesNotContain("return false;", code);
        Assert.DoesNotContain("return true;", code);
        Assert.Contains("NotImplementedException", code);
        Assert.Contains("Widget", code);
        Assert.Contains("WidgetRow", code);
    }

    [Fact]
    public void UpdateEndpoint_WithDifferentResponseType_StillAppliesDeclaredAssignmentsViaWithExpression()
    {
        // Arrange
        var ep = new CustomEndpoint
        {
            Route = "/api/v1/widgets/resize",
            Method = "PUT",
            RequestType = "ResizeWidgetCommand",
            OperationType = "Update",
            TargetEntity = "Widget",
            ResponseType = "WidgetRow",
            FilterSourceValue = "Id",
            Assignments = new List<AssignmentRule>
            {
                new AssignmentRule { EntityProperty = "Size", SourceValue = "NewSize" }
            }
        };

        // Act
        var code = TestHelpers.GenerateForSingleCustomEndpoint(ep, "Sales.Contracts");

        // Assert
        Assert.Contains("entity = entity with", code);
        Assert.Contains("Size = request.NewSize,", code);
    }

    [Fact]
    public void UpdateEndpoint_WithNoResponseType_GeneratesExactlyTodaysBody_RegressionGuard()
    {
        // Arrange
        var ep = new CustomEndpoint
        {
            Route = "/api/v1/widgets/rename",
            Method = "PUT",
            RequestType = "RenameWidgetCommand",
            OperationType = "Update",
            TargetEntity = "Widget",
            FilterSourceValue = "Id",
            Assignments = new List<AssignmentRule>
            {
                new AssignmentRule { EntityProperty = "Name", SourceValue = "NewName" }
            }
        };

        // Act
        var code = TestHelpers.GenerateForSingleCustomEndpoint(ep, "Sales.Contracts");

        // Assert
        Assert.Contains("return true;", code);
        Assert.Contains("return false;", code);
        Assert.DoesNotContain("NotImplementedException", code);
        Assert.Contains("IRequestHandler<RenameWidgetCommand, bool>", code);
    }
}

