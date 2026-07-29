using System.Collections.Generic;
using Xunit;
using Foundry.Schema.Compiler;

namespace Foundry.Schema.Compiler.Tests;

public class HandlerGenerationTests
{
    [Fact]
    public void Generate_QueryHandler_GeneratesMongoFindProjection()
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
        Assert.Contains("private readonly IRepository<Order> _repository;", code);
        Assert.Contains("var items = await _repository.FindManyAsync(", code);
        // Typed, and not stringly. The handler used to compare `x.Field.ToString()` against a
        // string request property, which compiles for any pair of types and compares the wrong
        // things for most of them.
        Assert.Contains("x => x.CustomerId == request.CustomerId", code);
    }

    [Fact]
    public void Generate_UpdateHandler_GeneratesMongoUpdateAssignments()
    {
        // Arrange
        var ep = new CustomEndpoint
        {
            Route = "/api/v1/customers/status",
            Method = "POST",
            RequestType = "UpdateCustomerStatusCommand",
            OperationType = "Update",
            TargetEntity = "Customer",
            FilterField = "Id",
            FilterSourceValue = "CustomerId",
            Assignments = new List<AssignmentRule>
            {
                new AssignmentRule { EntityProperty = "Status", SourceValue = "NewStatus" },
                new AssignmentRule { EntityProperty = "ModifiedBy", SourceValue = "AdminUserId" }
            }
        };

        // Act
        var code = TestHelpers.GenerateForSingleCustomEndpoint(ep, "Sales.Contracts");

        // Assert
        Assert.Contains("public class UpdateCustomerStatusCommandHandler : IRequestHandler<UpdateCustomerStatusCommand, bool>", code);
        Assert.Contains("var entity = await _repository.GetByIdAsync(request.CustomerId);", code);
        Assert.Contains("entity = entity with", code);
        Assert.Contains("Status = request.NewStatus,", code);
        Assert.Contains("ModifiedBy = request.AdminUserId,", code);
        Assert.Contains("await _repository.UpdateAsync(entity);", code);
    }

    [Fact]
    public void Generate_InsertHandler_GeneratesMongoInsertStatement()
    {
        // Arrange
        var ep = new CustomEndpoint
        {
            Route = "/api/v1/orders/new",
            Method = "POST",
            RequestType = "CreateOrderCommand",
            OperationType = "Insert",
            TargetEntity = "Order"
        };

        // Act
        var code = TestHelpers.GenerateForSingleCustomEndpoint(ep, "Sales.Contracts");

        // Assert
        Assert.Contains("public class CreateOrderCommandHandler : IRequestHandler<CreateOrderCommand, bool>", code);
        Assert.Contains("var entity = new Order", code);
        Assert.Contains("await _repository.InsertAsync(entity, ct: cancellationToken);", code);
    }

    [Fact]
    public void Generate_CustomHandler_GeneratesStubWithException()
    {
        // Arrange
        var ep = new CustomEndpoint
        {
            Route = "/api/v1/checkout",
            Method = "POST",
            RequestType = "CheckoutCommand",
            OperationType = "Custom"
        };

        // Act
        var code = TestHelpers.GenerateForSingleCustomEndpoint(ep, "Sales.Contracts");

        // Assert
        Assert.Contains("public class CheckoutCommandHandler : IRequestHandler<CheckoutCommand, bool>", code);
        Assert.Contains("throw new NotImplementedException(\"Custom logic handler.\");", code);
    }
}
